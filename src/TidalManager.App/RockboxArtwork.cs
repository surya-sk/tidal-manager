using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TidalManager.App;

// Rockbox's JPEG decoder handles only baseline JPEGs. TIDAL serves progressive ones,
// so covers are re-encoded with Windows' JPEG encoder, which writes baseline.
internal static class RockboxArtwork
{
    private const uint MaxSize = 640;

    public static async Task<byte[]> ToBaselineJpegAsync(byte[] image, CancellationToken cancellationToken)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(image.AsBuffer()).AsTask(cancellationToken);
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);

        using var output = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(0.92f, PropertyType.Single)
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties).AsTask(cancellationToken);
        var largest = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        var scale = largest > MaxSize ? (double)MaxSize / largest : 1;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Round(decoder.OrientedPixelWidth * scale),
            ScaledHeight = (uint)Math.Round(decoder.OrientedPixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken);

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
        return bytes;
    }
}
