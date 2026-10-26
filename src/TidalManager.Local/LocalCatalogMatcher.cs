using System.Text;

namespace TidalManager.Local;

public sealed record LocalCatalogMatch(
    IReadOnlyList<PlaylistTrackReference> Missing,
    int AlreadyPresent,
    IReadOnlyList<string> Warnings);

// Tags of one local FLAC, as needed to match it against TIDAL metadata.
internal sealed record LocalTrackTags(string Title, string Album, string PrimaryArtist,
    string? AlbumArtist, IReadOnlyList<string> Artists);

public sealed class LocalCatalogMatcher
{
    public LocalCatalogMatch FindMissing(string folder, IReadOnlyList<PlaylistTrackReference> tracks) =>
        FindMissing([folder], tracks);

    public LocalCatalogMatch FindMissing(IReadOnlyList<string> folders, IReadOnlyList<PlaylistTrackReference> tracks)
    {
        var local = new List<LocalTrackTags>();
        var warnings = new List<string>();
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A download folder that tiddl has not created yet simply contains nothing.
            if (!Directory.Exists(folder))
            {
                warnings.Add($"Music folder does not exist yet: {folder}");
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(folder, "*", enumeration)
                         .Where(path => Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var file = TagLib.File.Create(path);
                    var tags = ReadTags(file.Tag);
                    if (tags is null)
                    {
                        warnings.Add($"Could not match untagged FLAC: {path}");
                        continue;
                    }
                    local.Add(tags);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                           or TagLib.UnsupportedFormatException)
                {
                    warnings.Add($"Could not read {path}: {ex.Message}");
                }
            }
        }

        var byAlbum = local.ToLookup(tags => Normalize(tags.Album));
        var missing = tracks.Where(track => !byAlbum[Normalize(track.Album)].Any(tags => Matches(track, tags)))
            .ToArray();
        return new(missing, tracks.Count - missing.Length, warnings);
    }

    internal static LocalTrackTags? ReadTags(TagLib.Tag tag)
    {
        var title = tag.Title?.Trim();
        var album = tag.Album?.Trim();
        var performers = tag.Performers.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).ToArray();
        var albumArtist = tag.AlbumArtists.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        var primary = performers.FirstOrDefault() ?? albumArtist;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(primary))
            return null;
        var artists = performers.Concat(albumArtist is null ? [] : [albumArtist])
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        return new(title, album, primary, albumArtist, artists);
    }

    // tiddl writes TITLE as "Title (Version)" and ARTIST as an alphabetically sorted list of every
    // track artist, while TIDAL's API returns the version separately and the main artist first.
    internal static bool Matches(PlaylistTrackReference track, LocalTrackTags local)
    {
        if (Normalize(local.Album) != Normalize(track.Album)) return false;
        var title = Normalize(local.Title);
        if (title != Normalize(track.Title) &&
            (string.IsNullOrWhiteSpace(track.Version) || title != Normalize($"{track.Title} ({track.Version})")))
            return false;
        var wanted = (track.Artists is { Count: > 0 } ? track.Artists : [track.Artist])
            .Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return local.Artists.Any(artist => wanted.Contains(Normalize(artist)));
    }

    internal static string Key(string title, string artist, string album) =>
        string.Join('\u001f', new[] { artist, album, title }.Select(Normalize));

    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormC).Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
            }
            else builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }
}
