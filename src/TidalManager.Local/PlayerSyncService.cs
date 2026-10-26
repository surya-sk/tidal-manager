using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TidalManager.Local;

public sealed record PlaylistTrackReference(string Id, string Title, string Artist, string Album,
    string? Version = null, IReadOnlyList<string>? Artists = null, Uri? AlbumArtwork = null)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Version) ? Title : $"{Title} ({Version})";
}

// A TIDAL playlist or album to put on the player. Playlists also get an M3U8.
public sealed record SyncCollection(string Id, string Name, bool WritePlaylist,
    IReadOnlyList<PlaylistTrackReference> Tracks);

public sealed record PlayerCopy(string SourcePath, string DestinationPath, string Identity,
    Uri? Artwork, string? CoverPath);
public sealed record PlayerReuse(string SourcePath, string DestinationPath);
// A synced track found outside <player>\Music that is moved into it.
public sealed record PlayerMove(string From, string To);
public sealed record PlannedPlaylist(string Id, string Name, string Path,
    IReadOnlyList<string> Entries, IReadOnlyList<PlaylistTrackReference> Missing);
// An album folder on the player, its TIDAL cover (if known), and the synced tracks in it.
public sealed record PlannedArtwork(string Folder, Uri? Artwork, IReadOnlyList<string> Files);

public sealed record PlayerSyncPlan(
    string SourceFolder, string PlayerFolder,
    IReadOnlyList<PlayerCopy> Copies, IReadOnlyList<PlayerReuse> ReusedFiles,
    IReadOnlyList<PlannedPlaylist> Playlists, IReadOnlyList<PlaylistTrackReference> MissingTracks,
    IReadOnlyList<PlannedArtwork> Artwork, IReadOnlyList<string> Warnings, IReadOnlyList<PlayerMove> Moves)
{
    public int Reused => ReusedFiles.Count;
}

public sealed record PlayerSyncResult(int Copied, int Reused, IReadOnlyList<string> PlaylistPaths,
    int ArtworkWritten, int Deleted, IReadOnlyList<string> Warnings, int Moved = 0);

public sealed record SyncStep(int Done, int Total, string Message);

public delegate Task<byte[]> ArtworkDownloader(Uri artwork, CancellationToken cancellationToken);

// Re-encodes an image as a baseline JPEG that Rockbox can decode.
public delegate Task<byte[]> ArtworkConverter(byte[] image, CancellationToken cancellationToken);

// tagCachePath keeps the tags read from each FLAC (keyed by size and time) between app runs,
// so only new or changed files on the player are read again.
public sealed class PlayerSyncService(string? tagCachePath = null)
{
    // Rockbox shows album art from any of these beside the track (case does not matter on FAT).
    private static readonly string[] CoverNames = ["cover.jpg", "cover.jpeg", "cover.png", "cover.bmp",
        "folder.jpg", "folder.jpeg", "folder.png", "folder.bmp"];

    // Plans which FLACs to copy for the wanted tracks. Tracks already anywhere on the player are reused.
    public PlayerSyncPlan Preview(string sourceFolder, string playerFolder, IReadOnlyList<SyncCollection> collections,
        IProgress<SyncStep>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceFolder);
        var player = Path.GetFullPath(playerFolder);
        if (!Directory.Exists(player)) throw new DirectoryNotFoundException($"Player folder not found: {player}. Connect your player.");
        if (IsWithin(source, player) || IsWithin(player, source))
            throw new ArgumentException("The download folder and the player must be separate, non-nested folders.");

        var warnings = new List<string>();
        LoadTagCache();
        var sourceTracks = Directory.Exists(source)
            ? ReadTracks(source, warnings, progress, "your downloads", cancellationToken) : [];
        var playerTracks = ReadTracks(player, warnings, progress, "your player", cancellationToken);
        SaveTagCache(warnings);
        var music = Path.Combine(player, "Music");
        // Prefer a copy that is already in Music when the same track exists twice.
        int InMusicFirst(TaggedTrack track) => IsWithin(music, track.Path) ? 0 : 1;
        var sourceByAlbum = sourceTracks.ToLookup(track => LocalCatalogMatcher.Normalize(track.Album));
        var playerByAlbum = playerTracks.ToLookup(track => LocalCatalogMatcher.Normalize(track.Album));
        var playerIdentities = playerTracks.GroupBy(track => track.Identity)
            .ToDictionary(group => group.Key, group => group.OrderBy(InMusicFirst)
                .ThenBy(track => track.Path, StringComparer.OrdinalIgnoreCase).First());
        var reservedPaths = new HashSet<string>(playerTracks.Select(track => track.Path), StringComparer.OrdinalIgnoreCase);
        var moves = new List<PlayerMove>();
        var moveByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string UsePlayerFile(TaggedTrack track)
        {
            if (IsWithin(music, track.Path)) return track.Path;
            if (moveByPath.TryGetValue(track.Path, out var planned)) return planned;
            var destination = PlanDestination(player, track, reservedPaths);
            moves.Add(new(track.Path, destination));
            return moveByPath[track.Path] = destination;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new List<PlayerCopy>();
        var copyBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reused = new Dictionary<string, PlayerReuse>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<PlaylistTrackReference>();
        var artwork = new Dictionary<string, (Uri? Uri, List<string> Files)>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in collections.SelectMany(collection => collection.Tracks))
        {
            if (resolved.ContainsKey(track.Id) || missing.Any(item => item.Id == track.Id)) continue;
            var key = LocalCatalogMatcher.Normalize(track.Album);
            var onPlayer = playerByAlbum[key].Where(candidate => LocalCatalogMatcher.Matches(track, candidate.Tags))
                .OrderBy(InMusicFirst).ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var inSource = FirstMatch(sourceByAlbum[key], track);
            string? path = null;
            if (onPlayer is not null)
            {
                path = UsePlayerFile(onPlayer);
                if (inSource is not null) reused.TryAdd(inSource.Path, new(inSource.Path, path));
            }
            else if (inSource is not null && playerIdentities.TryGetValue(inSource.Identity, out var samePlayerFile))
            {
                path = UsePlayerFile(samePlayerFile);
                reused.TryAdd(inSource.Path, new(inSource.Path, path));
            }
            else if (inSource is not null && copyBySource.TryGetValue(inSource.Path, out var planned))
                path = planned;
            else if (inSource is not null)
            {
                path = PlanDestination(player, inSource, reservedPaths);
                copyBySource[inSource.Path] = path;
                copies.Add(new(inSource.Path, path, inSource.Identity, track.AlbumArtwork, inSource.CoverPath));
            }

            if (path is null)
            {
                missing.Add(track);
                continue;
            }
            resolved[track.Id] = path;
            var folder = Path.GetDirectoryName(path)!;
            if (!artwork.TryGetValue(folder, out var entry)) artwork[folder] = entry = (track.AlbumArtwork, []);
            else if (entry.Uri is null && track.AlbumArtwork is not null) artwork[folder] = entry = (track.AlbumArtwork, entry.Files);
            if (!entry.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) entry.Files.Add(path);
        }

        var playlists = new List<PlannedPlaylist>();
        var playlistPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections.Where(collection => collection.WritePlaylist))
        {
            var folder = Path.Combine(player, "Playlists");
            var stem = LocalMusicOrganizer.SafeComponent(collection.Name);
            var path = Path.Combine(folder, stem + ".m3u8");
            for (var suffix = 2; playlistPaths.Contains(path) ||
                 (File.Exists(path) && !OwnsPlaylist(path, collection.Id)); suffix++)
                path = Path.Combine(folder, $"{stem} ({suffix}).m3u8");
            playlistPaths.Add(path);
            playlists.Add(new(collection.Id, collection.Name, path,
                collection.Tracks.Where(track => resolved.ContainsKey(track.Id)).Select(track => resolved[track.Id]).ToArray(),
                collection.Tracks.Where(track => !resolved.ContainsKey(track.Id)).ToArray()));
        }

        return new(source, player, copies, reused.Values.ToArray(), playlists, missing,
            artwork.Select(item => new PlannedArtwork(item.Key, item.Value.Uri, item.Value.Files)).ToArray(), warnings, moves);
    }

    public async Task<PlayerSyncResult> SyncAsync(PlayerSyncPlan plan, ArtworkDownloader? downloadArtwork = null,
        IProgress<SyncStep>? progress = null, CancellationToken cancellationToken = default,
        bool deleteSourceAfterVerifiedTransfer = false, string? cleanupSourceFolder = null,
        ArtworkConverter? makeRockboxCompatible = null)
    {
        if (deleteSourceAfterVerifiedTransfer &&
            (string.IsNullOrWhiteSpace(cleanupSourceFolder) ||
             !Path.GetFullPath(cleanupSourceFolder).Equals(plan.SourceFolder, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cleanup is allowed only for the configured download folder.");

        var warnings = new List<string>();
        var artworkCache = new Dictionary<Uri, byte[]?>();
        async Task<byte[]?> ArtworkAsync(Uri uri)
        {
            if (downloadArtwork is null) return null;
            if (artworkCache.TryGetValue(uri, out var cached)) return cached;
            try { return artworkCache[uri] = await downloadArtwork(uri, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Could not download album art {uri}: {ex.Message}");
                return artworkCache[uri] = null;
            }
        }

        // Returns the image unchanged unless it is a progressive JPEG that can be converted.
        async Task<byte[]> CompatibleAsync(byte[] image)
        {
            if (makeRockboxCompatible is null || !Jpeg.IsProgressive(image)) return image;
            try { return await makeRockboxCompatible(image, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Could not convert album art for Rockbox: {ex.Message}");
                return image;
            }
        }

        var total = plan.Moves.Count + plan.Copies.Count + plan.Playlists.Count + plan.Artwork.Count;
        var done = 0;
        var moved = 0;
        foreach (var move in plan.Moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(done, total, $"Moving {Path.GetFileNameWithoutExtension(move.From)} into Music"));
            if (!IsWithin(plan.PlayerFolder, move.From) || !IsWithin(Path.Combine(plan.PlayerFolder, "Music"), move.To))
                throw new InvalidDataException("A planned move is outside the player's Music folder.");
            if (!File.Exists(move.From)) throw new FileNotFoundException("A player file disappeared during sync.", move.From);
            if (File.Exists(move.To)) throw new IOException($"Player file appeared during sync: {move.To}.");
            Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
            File.Move(move.From, move.To);
            moved++;
            done++;
        }
        if (moved > 0)
        {
            await UpdateOtherPlaylistsAsync(plan, warnings, cancellationToken);
            TidyMovedFolders(plan, warnings);
        }
        var copied = 0;
        var verifiedCopies = new List<(string Source, string Destination, byte[] SourceHash, byte[] DestinationHash)>();
        foreach (var item in plan.Copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(done, total, $"Copying {Path.GetFileNameWithoutExtension(item.SourcePath)}"));
            if (!IsWithin(plan.SourceFolder, item.SourcePath) || !IsWithin(plan.PlayerFolder, item.DestinationPath))
                throw new InvalidDataException("A planned path is outside the download or player folder.");
            if (!File.Exists(item.SourcePath))
                throw new FileNotFoundException("A downloaded FLAC disappeared during sync.", item.SourcePath);
            if (File.Exists(item.DestinationPath))
                throw new IOException($"Player file appeared during sync: {item.DestinationPath}.");

            var sourceHash = deleteSourceAfterVerifiedTransfer ? await HashFileAsync(item.SourcePath, cancellationToken) : null;
            var folder = Path.GetDirectoryName(item.DestinationPath)!;
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, ".tidalmanager-" + Guid.NewGuid().ToString("N") + ".flac");
            try
            {
                await using (var input = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, cancellationToken);

                var copyHash = sourceHash is not null ? await HashFileAsync(temporary, cancellationToken) : null;
                if (sourceHash is not null && !sourceHash.AsSpan().SequenceEqual(copyHash))
                    throw new IOException($"Copy verification failed for {item.SourcePath}. Source was kept.");

                await EmbedArtworkAsync(temporary, item, ArtworkAsync, CompatibleAsync);
                var destinationHash = sourceHash is not null ? await HashFileAsync(temporary, cancellationToken) : null;
                File.Move(temporary, item.DestinationPath);
                if (destinationHash is not null)
                {
                    var movedHash = await HashFileAsync(item.DestinationPath, cancellationToken);
                    if (!destinationHash.AsSpan().SequenceEqual(movedHash))
                        throw new IOException($"Player verification failed for {item.DestinationPath}. Source was kept.");
                    using var verifiedFile = TagLib.File.Create(item.DestinationPath);
                    var tags = LocalCatalogMatcher.ReadTags(verifiedFile.Tag);
                    if (tags is null || Identity(tags.Title, tags.PrimaryArtist, tags.Album) != item.Identity)
                        throw new IOException($"Player FLAC tags do not match: {item.DestinationPath}. Source was kept.");
                    verifiedCopies.Add((item.SourcePath, item.DestinationPath, sourceHash!, destinationHash));
                }
                copied++;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            done++;
        }

        var playlistPaths = new List<string>();
        foreach (var playlist in plan.Playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(done, total, $"Writing playlist {playlist.Name}"));
            await WritePlaylistAsync(plan.PlayerFolder, playlist, cancellationToken);
            playlistPaths.Add(playlist.Path);
            done++;
        }

        var artworkWritten = 0;
        foreach (var item in plan.Artwork)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(done, total, "Adding album art"));
            done++;
            if (!IsWithin(plan.PlayerFolder, item.Folder) || !Directory.Exists(item.Folder)) continue;
            try
            {
                var covers = CoverFiles(item.Folder);
                if (covers.Length == 0)
                {
                    if (item.Artwork is not null && await ArtworkAsync(item.Artwork) is { } bytes)
                    {
                        await ReplaceFileAsync(Path.Combine(item.Folder, "cover.jpg"), await CompatibleAsync(bytes), cancellationToken);
                        artworkWritten++;
                    }
                }
                else
                {
                    // Earlier syncs wrote TIDAL's progressive JPEGs, which Rockbox cannot show.
                    foreach (var cover in covers)
                    {
                        var bytes = await File.ReadAllBytesAsync(cover, cancellationToken);
                        var compatible = await CompatibleAsync(bytes);
                        if (ReferenceEquals(compatible, bytes)) continue;
                        await ReplaceFileAsync(cover, compatible, cancellationToken);
                        artworkWritten++;
                    }
                }
                foreach (var file in item.Files.Where(file => IsWithin(item.Folder, file) && File.Exists(file)))
                    if (await RepairEmbeddedArtworkAsync(file, CompatibleAsync)) artworkWritten++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException)
            {
                warnings.Add($"Could not update album art in {item.Folder}: {ex.Message}");
            }
        }

        var deleted = 0;
        if (deleteSourceAfterVerifiedTransfer)
        {
            foreach (var item in verifiedCopies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await DeleteIfVerifiedAsync(item.Source, item.Destination,
                        item.SourceHash, item.DestinationHash, plan, warnings, cancellationToken))
                    deleted++;
            }
            foreach (var item in plan.ReusedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var sourceHash = await HashFileAsync(item.SourcePath, cancellationToken);
                    var destinationHash = await HashFileAsync(item.DestinationPath, cancellationToken);
                    if (await DeleteIfVerifiedAsync(item.SourcePath, item.DestinationPath,
                            sourceHash, destinationHash, plan, warnings, cancellationToken))
                        deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Kept {item.SourcePath}: {ex.Message}");
                }
            }
        }

        progress?.Report(new(total, total, "Done"));
        return new(copied, plan.Reused, playlistPaths, artworkWritten, deleted, warnings, moved);
    }

    // Other playlists in <player>\Playlists (yours or older ones) that pointed at a moved track are updated.
    private static async Task UpdateOtherPlaylistsAsync(PlayerSyncPlan plan, List<string> warnings, CancellationToken ct)
    {
        var folder = Path.Combine(plan.PlayerFolder, "Playlists");
        if (!Directory.Exists(folder)) return;
        var moved = plan.Moves.ToDictionary(move => Path.GetFullPath(move.From), move => move.To, StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in Directory.EnumerateFiles(folder)
                     .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".m3u" or ".m3u8"))
        {
            try
            {
                var encoding = Path.GetExtension(playlist).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
                    ? (Encoding)new UTF8Encoding(false) : Encoding.Latin1;
                var text = await File.ReadAllTextAsync(playlist, encoding, ct);
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
                var changed = false;
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var native = line.Replace('/', Path.DirectorySeparatorChar);
                    string resolved;
                    try
                    {
                        resolved = line[0] is '/' or '\\'
                            ? Path.GetFullPath(Path.Combine(plan.PlayerFolder, native.TrimStart(Path.DirectorySeparatorChar)))
                            : Path.IsPathFullyQualified(native) ? Path.GetFullPath(native)
                            : Path.GetFullPath(Path.Combine(folder, native));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                    if (!moved.TryGetValue(resolved, out var to)) continue;
                    var entry = line[0] is '/' or '\\'
                        ? "/" + Path.GetRelativePath(plan.PlayerFolder, to)
                        : Path.GetRelativePath(folder, to);
                    lines[index] = line.Contains('\\') ? entry.Replace('/', '\\') : entry.Replace('\\', '/');
                    changed = true;
                }
                if (changed) await ReplaceFileAsync(playlist, encoding.GetBytes(string.Join(newline, lines)), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not update {playlist} for moved tracks: {ex.Message}");
            }
        }
    }

    // After moving tracks into Music, bring their cover along and remove folders that are left empty.
    // A folder that still holds anything else (other tracks, notes, playlists) is left as it is.
    private static void TidyMovedFolders(PlayerSyncPlan plan, List<string> warnings)
    {
        foreach (var group in plan.Moves.GroupBy(move => Path.GetDirectoryName(move.From)!, StringComparer.OrdinalIgnoreCase))
        {
            var oldFolder = group.Key;
            var newFolder = Path.GetDirectoryName(group.First().To)!;
            try
            {
                if (!Directory.Exists(oldFolder) || Directory.EnumerateDirectories(oldFolder).Any()) continue;
                var leftovers = Directory.EnumerateFiles(oldFolder).ToArray();
                if (leftovers.Any(path => !CoverNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                    continue;
                if (leftovers.Any(path => File.Exists(Path.Combine(newFolder, Path.GetFileName(path))))) continue;
                foreach (var cover in leftovers) File.Move(cover, Path.Combine(newFolder, Path.GetFileName(cover)));
                Directory.Delete(oldFolder);
                var parent = Path.GetDirectoryName(oldFolder);
                if (parent is not null && IsWithin(plan.PlayerFolder, parent) &&
                    !Path.TrimEndingDirectorySeparator(parent).Equals(Path.TrimEndingDirectorySeparator(plan.PlayerFolder),
                        StringComparison.OrdinalIgnoreCase) &&
                    !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not tidy {oldFolder}: {ex.Message}");
            }
        }
    }

    private static async Task EmbedArtworkAsync(string path, PlayerCopy item, Func<Uri, Task<byte[]?>> artwork,
        Func<byte[], Task<byte[]>> compatible)
    {
        bool hasPicture;
        using (var existing = TagLib.File.Create(path)) hasPicture = existing.Tag.Pictures.Length > 0;
        if (hasPicture)
        {
            await RepairEmbeddedArtworkAsync(path, compatible);
            return;
        }
        var bytes = item.Artwork is not null ? await artwork(item.Artwork) : null;
        if (bytes is null && item.CoverPath is not null) bytes = await File.ReadAllBytesAsync(item.CoverPath);
        if (bytes is null) return;
        bytes = await compatible(bytes);
        using var file = TagLib.File.Create(path);
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(bytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = Jpeg.IsJpeg(bytes) ? "image/jpeg" : "image/png"
            }
        ];
        file.Save();
    }

    // Rockbox prefers embedded art over cover.jpg, so a progressive embedded JPEG hides the cover.
    // Only the picture block is rewritten; the audio is untouched.
    private static async Task<bool> RepairEmbeddedArtworkAsync(string path, Func<byte[], Task<byte[]>> compatible)
    {
        using var file = TagLib.File.Create(path);
        var pictures = file.Tag.Pictures;
        if (!pictures.Any(picture => Jpeg.IsProgressive(picture.Data.Data))) return false;
        var repaired = new TagLib.IPicture[pictures.Length];
        var changed = false;
        for (var index = 0; index < pictures.Length; index++)
        {
            var original = pictures[index];
            var bytes = await compatible(original.Data.Data);
            if (ReferenceEquals(bytes, original.Data.Data) || Jpeg.IsProgressive(bytes))
            {
                repaired[index] = original;
                continue;
            }
            repaired[index] = new TagLib.Picture(new TagLib.ByteVector(bytes))
            {
                Type = original.Type, Description = original.Description, MimeType = "image/jpeg"
            };
            changed = true;
        }
        if (!changed) return false;
        file.Tag.Pictures = repaired;
        file.Save();
        return true;
    }

    private static async Task ReplaceFileAsync(string target, byte[] bytes, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(folder, ".tidalmanager-" + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WritePlaylistAsync(string playerFolder, PlannedPlaylist playlist, CancellationToken ct)
    {
        foreach (var path in playlist.Entries)
            if (!File.Exists(path)) throw new FileNotFoundException("A playlist track is missing on the player.", path);
        var folder = Path.GetDirectoryName(playlist.Path)!;
        Directory.CreateDirectory(folder);
        var lines = new List<string> { "#EXTM3U", $"# TidalManager playlist ID: {playlist.Id}" };
        lines.AddRange(playlist.Entries.Select(path => PlaylistEntry(playerFolder, folder, path)));
        var temporary = Path.Combine(folder, ".tidalmanager-" + Guid.NewGuid().ToString("N") + ".m3u8");
        try
        {
            await File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(false), ct);
            if (File.Exists(playlist.Path))
            {
                if (!OwnsPlaylist(playlist.Path, playlist.Id))
                    throw new IOException($"{playlist.Path} was created by something else; it was not overwritten.");
                File.Move(temporary, playlist.Path, overwrite: true);
            }
            else File.Move(temporary, playlist.Path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        VerifyPlaylist(playlist.Path, playerFolder, playlist.Entries.Count);
    }

    // Rockbox resolves an entry without a leading slash relative to the playlist's own folder.
    // On a player/SD-card root, absolute "/Music/..." paths are unambiguous; in a subfolder use "../" paths.
    internal static string PlaylistEntry(string playerFolder, string playlistFolder, string trackPath)
    {
        var root = Path.GetPathRoot(playerFolder) ?? "";
        var isDriveRoot = Path.TrimEndingDirectorySeparator(playerFolder)
            .Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        var entry = isDriveRoot
            ? "/" + Path.GetRelativePath(playerFolder, trackPath)
            : Path.GetRelativePath(playlistFolder, trackPath);
        return entry.Replace('\\', '/');
    }

    // Re-read the written playlist and resolve every entry the way Rockbox does.
    private static void VerifyPlaylist(string playlistPath, string playerFolder, int expectedEntries)
    {
        var folder = Path.GetDirectoryName(playlistPath)!;
        var entries = File.ReadLines(playlistPath).Where(line => line.Length > 0 && line[0] != '#').ToArray();
        if (entries.Length != expectedEntries)
            throw new IOException($"Playlist {playlistPath} has {entries.Length} entries; expected {expectedEntries}.");
        foreach (var entry in entries)
        {
            var native = entry.Replace('/', Path.DirectorySeparatorChar);
            var resolved = entry.StartsWith('/')
                ? Path.Combine(playerFolder, native.TrimStart(Path.DirectorySeparatorChar))
                : Path.GetFullPath(Path.Combine(folder, native));
            if (!File.Exists(resolved))
                throw new FileNotFoundException($"Playlist entry does not resolve on the player: {entry}", resolved);
        }
    }

    private static string PlanDestination(string player, TaggedTrack track, HashSet<string> reservedPaths)
    {
        var folder = Path.Combine(player, "Music", LocalMusicOrganizer.SafeComponent(track.AlbumArtist),
            LocalMusicOrganizer.SafeComponent(track.Album));
        var prefix = track.Disc > 1 ? $"{track.Disc}-" : "";
        var number = track.Number > 0 ? $"{track.Number:00} - " : "";
        var stem = LocalMusicOrganizer.SafeComponent(prefix + number + track.Title);
        var destination = Path.Combine(folder, stem + ".flac");
        for (var suffix = 2; reservedPaths.Contains(destination) || File.Exists(destination); suffix++)
            destination = Path.Combine(folder, $"{stem} ({suffix}).flac");
        reservedPaths.Add(destination);
        return destination;
    }

    private static TaggedTrack? FirstMatch(IEnumerable<TaggedTrack> candidates, PlaylistTrackReference track) =>
        candidates.Where(candidate => LocalCatalogMatcher.Matches(track, candidate.Tags))
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string[] CoverFiles(string folder) =>
        Directory.EnumerateFiles(folder).Where(path =>
            CoverNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)).ToArray();

    private static async Task<bool> DeleteIfVerifiedAsync(string source, string destination,
        byte[] expectedSourceHash, byte[] expectedDestinationHash, PlayerSyncPlan plan,
        List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsWithin(plan.SourceFolder, source) || !IsWithin(plan.PlayerFolder, destination) ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Kept {source}: unsafe cleanup path.");
                return false;
            }
            var currentSourceHash = await HashFileAsync(source, cancellationToken);
            var currentDestinationHash = await HashFileAsync(destination, cancellationToken);
            if (!expectedSourceHash.SequenceEqual(currentSourceHash) ||
                !expectedDestinationHash.SequenceEqual(currentDestinationHash))
            {
                warnings.Add($"Kept {source}: a file changed after transfer.");
                return false;
            }
            if (!expectedSourceHash.SequenceEqual(expectedDestinationHash) &&
                !plan.Copies.Any(item => item.SourcePath.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                                         item.DestinationPath.Equals(destination, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Kept {source}: the matching player file is not byte-identical.");
                return false;
            }
            File.Delete(source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Kept {source}: {ex.Message}");
            return false;
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    // Tags read so far, keyed by path; an entry is reused while the file's size and time are unchanged.
    private static readonly ConcurrentDictionary<string, CachedTags> TagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static string? _loadedCachePath;

    private sealed record CachedTags(long Length, long WriteTicks, string? Title, string? Album, string? PrimaryArtist,
        string? AlbumArtist, string[]? Artists, uint Track, uint Disc, bool HasPicture)
    {
        public LocalTrackTags? Tags => Title is null || Album is null || PrimaryArtist is null ? null
            : new(Title, Album, PrimaryArtist, AlbumArtist, Artists ?? []);
    }

    private void LoadTagCache()
    {
        lock (CacheGate)
        {
            if (tagCachePath is null || _loadedCachePath == tagCachePath || !File.Exists(tagCachePath)) return;
            _loadedCachePath = tagCachePath;
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, CachedTags>>(File.ReadAllText(tagCachePath));
                foreach (var (path, tags) in saved ?? []) TagCache.TryAdd(path, tags);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    private void SaveTagCache(List<string> warnings)
    {
        if (tagCachePath is null) return;
        lock (CacheGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tagCachePath)!);
                var temporary = tagCachePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, CachedTags>(TagCache)));
                File.Move(temporary, tagCachePath, overwrite: true);
                _loadedCachePath = tagCachePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not save the tag cache: {ex.Message}");
            }
        }
    }

    private static List<TaggedTrack> ReadTracks(string folder, List<string> warnings,
        IProgress<SyncStep>? progress = null, string label = "", CancellationToken cancellationToken = default)
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var paths = Directory.EnumerateFiles(folder, "*", enumeration)
            .Where(path => Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsInDotFolder(folder, path))
            .ToArray();
        var tracks = new List<TaggedTrack>(paths.Length);
        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 25 == 0) progress?.Report(new(index, paths.Length, $"Reading {label} · {index} of {paths.Length}"));
            var path = paths[index];
            try
            {
                var info = new FileInfo(path);
                if (!TagCache.TryGetValue(path, out var cached) ||
                    cached.Length != info.Length || cached.WriteTicks != info.LastWriteTimeUtc.Ticks)
                {
                    cached = ReadTags(path, info);
                    TagCache[path] = cached;
                }
                if (cached.Tags is not { } tags)
                {
                    warnings.Add($"Skipped untagged FLAC: {path}");
                    continue;
                }
                var cover = cached.HasPicture ? null : FindCover(Path.GetDirectoryName(path)!);
                tracks.Add(new(path, tags, cached.Track, cached.Disc, Identity(tags.Title, tags.PrimaryArtist, tags.Album), cover));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException)
            {
                warnings.Add($"Skipped {path}: {ex.Message}");
            }
        }
        progress?.Report(new(paths.Length, paths.Length, $"Reading {label} · {paths.Length} of {paths.Length}"));
        return tracks;
    }

    private static CachedTags ReadTags(string path, FileInfo info)
    {
        // The fast reader skips embedded pictures; TagLib is the fallback for anything unusual.
        var quick = FlacTagReader.TryRead(path);
        if (quick is not null)
            return new(info.Length, info.LastWriteTimeUtc.Ticks, quick.Tags?.Title, quick.Tags?.Album,
                quick.Tags?.PrimaryArtist, quick.Tags?.AlbumArtist, quick.Tags?.Artists.ToArray(),
                quick.Track, quick.Disc, quick.HasPicture);
        using var file = TagLib.File.Create(path);
        var tags = LocalCatalogMatcher.ReadTags(file.Tag);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, tags?.Title, tags?.Album, tags?.PrimaryArtist,
            tags?.AlbumArtist, tags?.Artists.ToArray(), file.Tag.Track, file.Tag.Disc, file.Tag.Pictures.Length > 0);
    }

    // Skips .rockbox, .Trash-1000, .git and similar hidden folders, and our own temporary files.
    private static bool IsInDotFolder(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'));

    private static string Identity(string title, string artist, string album) =>
        LocalCatalogMatcher.Key(title, artist, album);

    private static string? FindCover(string folder)
    {
        foreach (var name in new[] { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && new FileInfo(path).Length <= 10 * 1024 * 1024) return path;
        }
        return null;
    }

    private static bool OwnsPlaylist(string path, string id)
    {
        try { return File.ReadLines(path).Take(2).Any(line => line == $"# TidalManager playlist ID: {id}"); }
        catch (IOException) { return false; }
    }

    // A drive root such as "D:\" already ends in a separator and must not get a second one.
    internal static bool IsWithin(string folder, string possibleChild)
    {
        static string WithSeparator(string path)
        {
            var full = Path.GetFullPath(path);
            return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
        }
        return WithSeparator(possibleChild).StartsWith(WithSeparator(folder), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TaggedTrack(string Path, LocalTrackTags Tags, uint Number, uint Disc,
        string Identity, string? CoverPath)
    {
        public string Title => Tags.Title;
        public string Album => Tags.Album;
        public string AlbumArtist => Tags.AlbumArtist ?? Tags.PrimaryArtist;
    }
}
