using System.Diagnostics;
using System.Security.Cryptography;
using TidalManager.Local;

var ffmpeg = new ProcessStartInfo("ffmpeg")
{
    UseShellExecute = false,
    RedirectStandardError = true,
    RedirectStandardOutput = true
};
var temp = Directory.CreateTempSubdirectory("TidalManagerLocalTests-");
try
{
    var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "source"));
    var destination = Path.Combine(temp.FullName, "organized");
    var albumFolder = Directory.CreateDirectory(Path.Combine(source.FullName, "unorganized"));
    var flac = Path.Combine(albumFolder.FullName, "sample.flac");
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                 "anullsrc=r=44100:cl=stereo", "-t", "0.1", "-c:a", "flac", flac })
        ffmpeg.ArgumentList.Add(argument);
    using (var process = Process.Start(ffmpeg) ?? throw new Exception("ffmpeg could not start"))
    {
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "test FLAC generated");
    }
    Tag(flac, "A/B: C", "An Album", ["An Artist"], "An Artist", 3);
    var png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    var cover = Path.Combine(albumFolder.FullName, "cover.png");
    File.WriteAllBytes(cover, png);

    // Organizer: copies into Artist/Album, never touches originals.
    var originalHash = SHA256.HashData(File.ReadAllBytes(flac));
    var organizer = new LocalMusicOrganizer();
    var plan = organizer.Preview(source.FullName, destination);
    Check(plan.Tracks.Count == 1 && plan.Warnings.Count == 0, "FLAC previewed");
    Check(plan.Tracks[0].DestinationPath.EndsWith(
        Path.Combine("An Artist", "An Album", "03 - A_B_ C.flac")), "safe organizer filename");
    Check(plan.Tracks[0].CoverPath == cover, "nearby cover detected");
    var result = await organizer.CopyAsync(plan);
    Check(result.Copied == 1 && result.Errors.Count == 0, "FLAC copied");
    Check(SHA256.HashData(File.ReadAllBytes(flac)).SequenceEqual(originalHash), "original unchanged");
    using (var copied = TagLib.File.Create(plan.Tracks[0].DestinationPath))
        Check(copied.Tag.Title == "A/B: C" && copied.Tag.Pictures.Length == 1, "tags kept and cover embedded");
    Check(organizer.Preview(source.FullName, destination).Tracks[0].DestinationPath.Contains(" (2).flac"),
        "existing file not overwritten");
    File.Delete(cover);

    // Player sync: a playlist and an album that share a track.
    var downloads = Directory.CreateDirectory(Path.Combine(temp.FullName, "downloads"));
    var trackA = Path.Combine(downloads.FullName, "Artist", "Album", "Song A.flac");
    var trackB = Path.Combine(downloads.FullName, "Artist", "Album", "Song B.flac");
    var unrelated = Path.Combine(downloads.FullName, "Other", "Other", "Not Wanted.flac");
    Copy(flac, trackA, "Song A (Remastered)", "The Album", ["Guest", "The Artist"], "The Artist", 1);
    Copy(flac, trackB, "Song B", "The Album", ["The Artist"], "The Artist", 2);
    Copy(flac, unrelated, "Not Wanted", "Other", ["Other"], "Other", 1);
    var art = new Uri("https://resources.tidal.com/images/a/640x640.jpg");
    var refA = new PlaylistTrackReference("t1", "Song A", "The Artist", "The Album", "Remastered", ["The Artist", "Guest"], art);
    var refB = new PlaylistTrackReference("t2", "Song B", "The Artist", "The Album", null, ["The Artist"], art);
    var refMissing = new PlaylistTrackReference("t3", "Not Downloaded", "The Artist", "The Album", null, null, art);
    var mix = new SyncCollection("playlist-1", "My Mix", true, [refB, refA, refMissing]);
    var album = new SyncCollection("album-1", "The Album", false, [refA, refB]);

    var player = Directory.CreateDirectory(Path.Combine(temp.FullName, "player"));
    var trash = Path.Combine(player.FullName, ".Trash-1000", "Song B.flac");
    Copy(flac, trash, "Song B", "The Album", ["The Artist"], "The Artist", 2);
    var tagCache = Path.Combine(temp.FullName, "tag-cache.json");
    var sync = new PlayerSyncService(tagCache);
    var downloadsRequested = 0;
    ArtworkDownloader fakeArt = (_, _) => { downloadsRequested++; return Task.FromResult(png); };

    var playerPlan = sync.Preview(downloads.FullName, player.FullName, [mix, album]);
    Check(playerPlan.Copies.Count == 2, "only wanted tracks are copied; hidden player folders are ignored");
    Check(playerPlan.MissingTracks.Select(track => track.Id).SequenceEqual(["t3"]), "undownloaded track reported");
    Check(playerPlan.Playlists.Count == 1 && playerPlan.Playlists[0].Entries.Count == 2 &&
        playerPlan.Playlists[0].Missing.Count == 1, "playlist planned with matched entries and its missing track");
    Check(playerPlan.Artwork.Count == 1, "one album art target per album folder");
    var playerResult = await sync.SyncAsync(playerPlan, fakeArt);
    Check(playerResult.Copied == 2 && playerResult.PlaylistPaths.Count == 1 && playerResult.ArtworkWritten == 1,
        "tracks, playlist and album art written");
    Check(downloadsRequested == 1, "album art downloaded once per album");
    var albumOnPlayer = Path.Combine(player.FullName, "Music", "The Artist", "The Album");
    Check(File.Exists(Path.Combine(albumOnPlayer, "cover.jpg")), "cover.jpg written beside the album for Rockbox");
    using (var copied = TagLib.File.Create(Path.Combine(albumOnPlayer, "01 - Song A (Remastered).flac")))
        Check(copied.Tag.Pictures.Length == 1, "album art embedded in the copied FLAC");
    var lines = File.ReadAllLines(playerResult.PlaylistPaths[0]);
    Check(lines.Length == 4 &&
        lines[2] == "../Music/The Artist/The Album/02 - Song B.flac" &&
        lines[3] == "../Music/The Artist/The Album/01 - Song A (Remastered).flac",
        "playlist keeps TIDAL order with paths relative to Playlists below a drive root");
    Check(PlayerSyncService.PlaylistEntry(@"D:\", @"D:\Playlists", @"D:\Music\A\B\01 - C.flac") == "/Music/A/B/01 - C.flac",
        "a player drive root gets absolute Rockbox paths");
    Check(PlayerSyncService.IsWithin(@"D:\", @"D:\Music\A\B\01 - C.flac") &&
        PlayerSyncService.IsWithin(@"D:\Music", @"D:\Music\A") &&
        !PlayerSyncService.IsWithin(@"D:\Music", @"D:\Musical\A") &&
        !PlayerSyncService.IsWithin(@"D:\", @"E:\Music"),
        "folder containment works for drive roots and rejects look-alike siblings");

    var repeated = sync.Preview(downloads.FullName, player.FullName, [mix, album]);
    Check(repeated.Copies.Count == 0 && repeated.Reused == 2, "second sync reuses tracks already on the player");
    var repeatedResult = await sync.SyncAsync(repeated, fakeArt);
    Check(repeatedResult.PlaylistPaths[0] == playerResult.PlaylistPaths[0] && repeatedResult.ArtworkWritten == 0,
        "second sync rewrites its own playlist and leaves the existing cover alone");
    var other = sync.Preview(downloads.FullName, player.FullName, [mix with { Id = "different-id" }]);
    Check(other.Playlists[0].Path != playerResult.PlaylistPaths[0],
        "a same-named playlist from another ID does not overwrite an existing playlist");

    // A synced track elsewhere on the player (e.g. D:\Artist\Album) is moved into Music, not duplicated,
    // and other playlists that pointed at it follow it.
    var looseArtist = Path.Combine(player.FullName, "Loose Artist");
    var looseAlbum = Path.Combine(looseArtist, "Loose Album");
    Copy(flac, Path.Combine(looseAlbum, "Loose.flac"), "Loose", "Loose Album", ["Loose Artist"], "Loose Artist", 1);
    File.WriteAllBytes(Path.Combine(looseAlbum, "folder.jpg"), png);
    var oldPlaylist = Path.Combine(player.FullName, "Playlists", "Old Favourites.m3u8");
    File.WriteAllLines(oldPlaylist, ["Loose Artist/Loose Album/Loose.flac", "/Loose Artist/Loose Album/Loose.flac",
        "../Music/The Artist/The Album/02 - Song B.flac"]);
    var loose = new PlaylistTrackReference("t9", "Loose", "Loose Artist", "Loose Album", null, null, art);
    var loosePlan = sync.Preview(downloads.FullName, player.FullName, [new SyncCollection("p9", "Loose", true, [loose])]);
    Check(loosePlan.Copies.Count == 0 && loosePlan.MissingTracks.Count == 0 && loosePlan.Moves.Count == 1,
        "a track outside Music is reused and planned to move into Music");
    var looseResult = await sync.SyncAsync(loosePlan, fakeArt);
    var movedAlbum = Path.Combine(player.FullName, "Music", "Loose Artist", "Loose Album");
    Check(looseResult.Moved == 1 && File.Exists(Path.Combine(movedAlbum, "01 - Loose.flac")) && !Directory.Exists(looseArtist),
        "track moved into Music\\Artist\\Album and the emptied folders removed");
    Check(File.Exists(Path.Combine(movedAlbum, "folder.jpg")) && !File.Exists(Path.Combine(movedAlbum, "cover.jpg")),
        "the album's own cover moves with it and is not replaced");
    Check(File.ReadAllLines(Path.Combine(player.FullName, "Playlists", "Loose.m3u8"))[2] == "../Music/Loose Artist/Loose Album/01 - Loose.flac",
        "the synced playlist points into Music");
    // Rockbox resolves a path without a leading slash from the playlist's folder, so the first entry
    // never pointed at this track and stays as it was.
    var updated = File.ReadAllLines(oldPlaylist);
    Check(updated.SequenceEqual(["Loose Artist/Loose Album/Loose.flac",
            "/Music/Loose Artist/Loose Album/01 - Loose.flac", "../Music/The Artist/The Album/02 - Song B.flac"]),
        "other playlists are updated for moved tracks, keeping their path style: " + string.Join(" | ", updated));
    var looseAgain = sync.Preview(downloads.FullName, player.FullName, [new SyncCollection("p9", "Loose", true, [loose])]);
    Check(looseAgain.Moves.Count == 0, "nothing moves on the next sync");

    // TIDAL serves progressive JPEGs, which Rockbox cannot decode. New covers are converted, and
    // progressive covers left by earlier syncs (cover.jpg and embedded) are repaired.
    byte[] progressive = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC2, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xDA, 0x00];
    byte[] baseline = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xDA, 0x00];
    Check(Jpeg.IsProgressive(progressive) && !Jpeg.IsProgressive(baseline) && !Jpeg.IsProgressive(png),
        "progressive JPEG detection");
    ArtworkDownloader tidalArt = (_, _) => Task.FromResult(progressive);
    var conversions = 0;
    ArtworkConverter toBaseline = (_, _) => { conversions++; return Task.FromResult(baseline); };
    var artAlbum = Path.Combine(player.FullName, "Music", "Art Artist", "Art Album");
    var artTrack = Path.Combine(artAlbum, "Art.flac");
    Copy(flac, artTrack, "Art", "Art Album", ["Art Artist"], "Art Artist", 1);
    var artRef = new PlaylistTrackReference("t10", "Art", "Art Artist", "Art Album", null, null, art);
    var artCollection = new SyncCollection("a10", "Art Album", false, [artRef]);
    await sync.SyncAsync(sync.Preview(downloads.FullName, player.FullName, [artCollection]), tidalArt,
        makeRockboxCompatible: toBaseline);
    Check(File.ReadAllBytes(Path.Combine(artAlbum, "cover.jpg")).SequenceEqual(baseline),
        "new cover.jpg is written as a baseline JPEG");

    File.WriteAllBytes(Path.Combine(artAlbum, "cover.jpg"), progressive);
    using (var file = TagLib.File.Create(artTrack))
    {
        file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(progressive)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
        file.Save();
    }
    var repair = await sync.SyncAsync(sync.Preview(downloads.FullName, player.FullName, [artCollection]), tidalArt,
        makeRockboxCompatible: toBaseline);
    Check(File.ReadAllBytes(Path.Combine(artAlbum, "cover.jpg")).SequenceEqual(baseline),
        "a progressive cover.jpg from an earlier sync is replaced");
    using (var file = TagLib.File.Create(artTrack))
        Check(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Data.Data.SequenceEqual(baseline) &&
            file.Tag.Pictures[0].Type == TagLib.PictureType.FrontCover, "progressive embedded art is re-encoded");
    Check(repair.ArtworkWritten == 2, "repairs are counted");
    var settled = await sync.SyncAsync(sync.Preview(downloads.FullName, player.FullName, [artCollection]), tidalArt,
        makeRockboxCompatible: toBaseline);
    Check(settled.ArtworkWritten == 0, "baseline art is left alone on the next sync");

    // Cleanup only deletes verified copies, and only in the configured download folder.
    try
    {
        await sync.SyncAsync(repeated, fakeArt, deleteSourceAfterVerifiedTransfer: true,
            cleanupSourceFolder: source.FullName);
        throw new Exception("cleanup accepted a different source folder");
    }
    catch (InvalidOperationException) { }
    var nonIdentical = await sync.SyncAsync(repeated, fakeArt, deleteSourceAfterVerifiedTransfer: true,
        cleanupSourceFolder: downloads.FullName);
    Check(nonIdentical.Deleted == 0 && File.Exists(trackA) && File.Exists(trackB),
        "downloads are kept when the player copy differs (embedded art)");

    var cleanupSource = Directory.CreateDirectory(Path.Combine(temp.FullName, "cleanup"));
    var cleanupFlac = Path.Combine(cleanupSource.FullName, "Cleanup.flac");
    Copy(flac, cleanupFlac, "Cleanup Track", "Cleanup Album", ["The Artist"], "The Artist", 5);
    File.WriteAllText(Path.Combine(cleanupSource.FullName, "keep.txt"), "not music");
    var cleanupRef = new PlaylistTrackReference("t5", "Cleanup Track", "The Artist", "Cleanup Album");
    var cleanupResult = await sync.SyncAsync(
        sync.Preview(cleanupSource.FullName, player.FullName, [new SyncCollection("a5", "Cleanup Album", false, [cleanupRef])]),
        fakeArt, deleteSourceAfterVerifiedTransfer: true, cleanupSourceFolder: cleanupSource.FullName);
    Check(cleanupResult.Copied == 1 && cleanupResult.Deleted == 1 && !File.Exists(cleanupFlac) &&
        File.Exists(Path.Combine(cleanupSource.FullName, "keep.txt")),
        "verified transferred FLAC is deleted; unrelated files are kept");

    Check(File.Exists(tagCache) && new PlayerSyncService(tagCache)
            .Preview(downloads.FullName, player.FullName, [mix]).Playlists[0].Entries.Count == 2,
        "tag cache is saved and reused by a new instance");

    var albumMatch = new LocalCatalogMatcher().FindMissing(
        [downloads.FullName, Path.Combine(temp.FullName, "not-created-yet")], [refA, refB, refMissing]);
    Check(albumMatch.AlreadyPresent == 2 && albumMatch.Missing.Single().Id == "t3" && albumMatch.Warnings.Count == 1,
        "tiddl tags match; a missing folder is a warning");
    Console.WriteLine("Local organizer and player sync checks passed.");
}
finally
{
    if (Directory.Exists(temp.FullName)) Directory.Delete(temp.FullName, recursive: true);
}

static void Copy(string template, string path, string title, string album, string[] artists, string albumArtist, uint track)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.Copy(template, path);
    Tag(path, title, album, artists, albumArtist, track);
}

static void Tag(string path, string title, string album, string[] artists, string albumArtist, uint track)
{
    using var file = TagLib.File.Create(path);
    file.Tag.Title = title;
    file.Tag.Album = album;
    file.Tag.Performers = artists;
    file.Tag.AlbumArtists = [albumArtist];
    file.Tag.Track = track;
    file.Tag.Pictures = [];
    file.Save();
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}
