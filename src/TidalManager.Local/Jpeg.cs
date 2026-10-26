namespace TidalManager.Local;

internal static class Jpeg
{
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

    // Rockbox decodes only baseline JPEGs; TIDAL serves its covers as progressive JPEGs.
    public static bool IsProgressive(ReadOnlySpan<byte> data)
    {
        if (!IsJpeg(data)) return false;
        var index = 2;
        while (index + 3 < data.Length)
        {
            if (data[index] != 0xFF) return false;
            var marker = data[index + 1];
            if (marker == 0xFF) { index++; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD8) { index += 2; continue; }
            // Start-of-frame markers for progressive (and progressive arithmetic) images.
            if (marker is 0xC2 or 0xC6 or 0xCA or 0xCE) return true;
            if (marker is 0xC0 or 0xC1 or 0xDA or 0xD9) return false;
            index += 2 + ((data[index + 2] << 8) | data[index + 3]);
        }
        return false;
    }
}
