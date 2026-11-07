using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MeuralImageBackup.Core.Services;

// Small copies of the images for the grids (the originals are often several megabytes). Kept in
// the thumbnails folder, named after the original's path, size and time, so a changed file gets a
// new thumbnail. It's only a cache: deleting the folder is harmless.
public sealed class ThumbnailService(StoragePaths storage, ILogger<ThumbnailService> logger)
{
    public const int LongestSide = 480;
    private const int Quality = 80;

    // The thumbnail's path, made first if needed; null when the image can't be read.
    public async Task<string?> GetAsync(string original, CancellationToken ct = default)
    {
        var info = new FileInfo(original);
        if (!info.Exists)
            return null;

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..32];
        var path = Path.Combine(storage.ThumbnailsPath, key[..2], key + ".jpg");
        if (File.Exists(path))
            return path;

        return await Task.Run(() => Make(original, path), ct);
    }

    private string? Make(string original, string path)
    {
        try
        {
            using var codec = SKCodec.Create(original);
            if (codec is null)
                return null;

            // Decode at a reduced size straight away where the format allows it (JPEG does), which
            // is much faster than decoding the whole image and shrinking it.
            var scale = Math.Min(1f, (float)LongestSide / Math.Max(codec.Info.Width, codec.Info.Height));
            var decodeSize = codec.GetScaledDimensions(Math.Max(scale, 1f / 8));
            var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            var upright = decoded;
            var finalScale = Math.Min(1f, (float)LongestSide / Math.Max(upright.Width, upright.Height));
            var width = Math.Max(1, (int)Math.Round(upright.Width * finalScale));
            var height = Math.Max(1, (int)Math.Round(upright.Height * finalScale));
            using var resized = upright.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var image = SKImage.FromBitmap(resized ?? upright);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            using (var file = File.Create(temp))
                data.SaveTo(file);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Couldn't make a thumbnail of {File}: {Reason}", original, ex.Message);
            return null;
        }
    }
}
