using System.Text;

namespace TidalManager.Local;

public sealed record LocalTrackPlan(
    string SourcePath, string DestinationPath, string Artist, string Album, string Title,
    uint TrackNumber, bool HasEmbeddedArt, string? CoverPath);

public sealed record LocalImportPlan(
    string SourceFolder, string DestinationFolder, IReadOnlyList<LocalTrackPlan> Tracks,
    IReadOnlyList<string> Warnings);

public sealed record LocalImportResult(int Copied, IReadOnlyList<string> Errors);

public sealed class LocalMusicOrganizer
{
    private static readonly string[] CoverNames = ["cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png"];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public LocalImportPlan Preview(string sourceFolder, string destinationFolder)
    {
        var source = Path.GetFullPath(sourceFolder);
        var destination = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Source folder does not exist.");
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new ArgumentException("Source and destination folders must be separate, non-nested locations.");

        var tracks = new List<LocalTrackPlan>();
        var warnings = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var file = TagLib.File.Create(path);
                var tag = file.Tag;
                var artist = First(tag.AlbumArtists) ?? First(tag.Performers) ?? "Unknown Artist";
                var album = CleanValue(tag.Album) ?? "Unknown Album";
                var title = CleanValue(tag.Title) ?? Path.GetFileNameWithoutExtension(path);
                var prefix = tag.DiscCount > 1 && tag.Disc > 0 ? $"{tag.Disc}-" : "";
                var number = tag.Track > 0 ? $"{tag.Track:00} - " : "";
                var filename = SafeComponent(prefix + number + title) + ".flac";
                var folder = Path.Combine(destination, SafeComponent(artist), SafeComponent(album));
                var proposed = UniquePath(Path.Combine(folder, filename), reserved);
                var embedded = tag.Pictures?.Length > 0;
                var cover = embedded ? null : FindCover(Path.GetDirectoryName(path)!);
                tracks.Add(new(path, proposed, artist, album, title, tag.Track, embedded, cover));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException)
            {
                warnings.Add($"Skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return new(source, destination, tracks, warnings);
    }

    public async Task<LocalImportResult> CopyAsync(LocalImportPlan plan,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var copied = 0;
        for (var index = 0; index < plan.Tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = plan.Tracks[index];
            var folder = Path.GetDirectoryName(track.DestinationPath)!;
            var temporary = Path.Combine(folder, ".tidalmanager-" + Guid.NewGuid().ToString("N") + ".flac");
            try
            {
                Directory.CreateDirectory(folder);
                await using (var input = new FileStream(track.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, cancellationToken);

                using (var file = TagLib.File.Create(temporary))
                {
                    var tag = file.Tag;
                    var changed = false;
                    if (string.IsNullOrWhiteSpace(tag.Title)) { tag.Title = track.Title; changed = true; }
                    if (string.IsNullOrWhiteSpace(tag.Album)) { tag.Album = track.Album; changed = true; }
                    if (First(tag.Performers) is null) { tag.Performers = [track.Artist]; changed = true; }
                    if (First(tag.AlbumArtists) is null) { tag.AlbumArtists = [track.Artist]; changed = true; }
                    if (tag.Pictures.Length == 0 && track.CoverPath is not null)
                    {
                        var picture = new TagLib.Picture(track.CoverPath);
                        picture.Type = TagLib.PictureType.FrontCover;
                        tag.Pictures = [picture];
                        changed = true;
                    }
                    if (changed) file.Save();
                }

                var finalPath = UniquePath(track.DestinationPath, null);
                File.Move(temporary, finalPath);
                copied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{Path.GetFileName(track.SourcePath)}: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException ex) { errors.Add($"Temporary copy cleanup failed: {ex.Message}"); }
                progress?.Report((index + 1, plan.Tracks.Count, Path.GetFileName(track.SourcePath)));
            }
        }
        return new(copied, errors);
    }

    private static string? First(IEnumerable<string>? values) =>
        values?.Select(CleanValue).FirstOrDefault(value => value is not null);

    private static string? CleanValue(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string SafeComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) || character is '/' or '\\' ? '_' : character);
        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length > 100) result = result[..100].TrimEnd(' ', '.');
        if (result.Length == 0) result = "Unknown";
        if (ReservedNames.Contains(result)) result += "_";
        return result;
    }

    private static string UniquePath(string path, HashSet<string>? reserved)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = path;
        for (var suffix = 2; File.Exists(candidate) || (reserved?.Contains(candidate) ?? false); suffix++)
            candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        reserved?.Add(candidate);
        return candidate;
    }

    private static bool IsWithin(string folder, string possibleChild) =>
        PlayerSyncService.IsWithin(folder, possibleChild);

    private static string? FindCover(string folder)
    {
        foreach (var name in CoverNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && new FileInfo(path).Length <= 10 * 1024 * 1024) return path;
        }
        return null;
    }
}
