using System.Text;

namespace TidalManager.Local;

internal sealed record FlacTags(LocalTrackTags? Tags, uint Track, uint Disc, bool HasPicture);

// Reads only the Vorbis comments of a FLAC file and seeks past everything else.
// TagLib loads every embedded picture, which over a player's USB connection makes a scan of a
// large library take minutes; this reads a few kilobytes per file.
internal static class FlacTagReader
{
    private const int VorbisComment = 4;
    private const int Picture = 6;
    private const int MaxCommentBlock = 1 << 20;

    public static FlacTags? TryRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header[..4]) != 4) return null;
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            // An ID3v2 tag in front of the FLAC stream: skip it.
            if (stream.Read(header[4..10]) != 6) return null;
            var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            stream.Seek(10 + size + ((header[5] & 0x10) != 0 ? 10 : 0), SeekOrigin.Begin);
            if (stream.Read(header[..4]) != 4) return null;
        }
        if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C') return null;

        var comments = new List<(string Key, string Value)>();
        var hasPicture = false;
        var last = false;
        while (!last)
        {
            if (stream.Read(header[..4]) != 4) return null;
            last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == Picture) hasPicture = true;
            if (type == VorbisComment && length <= MaxCommentBlock)
            {
                var block = new byte[length];
                stream.ReadExactly(block);
                if (!ParseComments(block, comments)) return null;
            }
            else stream.Seek(length, SeekOrigin.Current);
            if (type == 127) return null;
        }

        string? First(params string[] keys) => keys
            .SelectMany(key => comments.Where(comment => comment.Key == key).Select(comment => comment.Value))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        var performers = comments.Where(comment => comment.Key == "ARTIST")
            .Select(comment => comment.Value.Trim()).Where(value => value.Length > 0).ToArray();
        var albumArtist = First("ALBUMARTIST", "ALBUM ARTIST", "ENSEMBLE");
        var title = First("TITLE");
        var album = First("ALBUM");
        var primary = performers.FirstOrDefault() ?? albumArtist;
        LocalTrackTags? tags = null;
        if (title is not null && album is not null && primary is not null)
            tags = new(title, album, primary, albumArtist,
                performers.Concat(albumArtist is null ? [] : [albumArtist])
                    .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToArray());
        return new(tags, Number(First("TRACKNUMBER")), Number(First("DISCNUMBER")), hasPicture);
    }

    private static bool ParseComments(byte[] block, List<(string Key, string Value)> comments)
    {
        var position = 0;
        if (!TryReadInt(block, ref position, out var vendorLength) || vendorLength > block.Length - position) return false;
        position += vendorLength;
        if (!TryReadInt(block, ref position, out var count)) return false;
        for (var index = 0; index < count; index++)
        {
            if (!TryReadInt(block, ref position, out var length) || length > block.Length - position) return false;
            var comment = Encoding.UTF8.GetString(block, position, length);
            position += length;
            var separator = comment.IndexOf('=');
            if (separator > 0) comments.Add((comment[..separator].ToUpperInvariant(), comment[(separator + 1)..]));
        }
        return true;
    }

    private static bool TryReadInt(byte[] block, ref int position, out int value)
    {
        value = 0;
        if (position + 4 > block.Length) return false;
        var raw = BitConverter.ToUInt32(block, position);
        position += 4;
        if (raw > int.MaxValue) return false;
        value = (int)raw;
        return true;
    }

    // "3", "03" and "3/12" all mean track 3.
    private static uint Number(string? value)
    {
        if (value is null) return 0;
        var digits = value.Split('/')[0].Trim();
        return uint.TryParse(digits, out var number) ? number : 0;
    }
}
