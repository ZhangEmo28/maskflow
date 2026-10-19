using SkiaSharp;

// Image decoding/cropping on SkiaSharp (MIT). Coordinates everywhere in MaskFlow refer to the
// EXIF-oriented image, matching what browsers and the SAM service (PIL exif_transpose) see.
public static class ImageTools
{
    public const long MaxPixels = 100_000_000;

    public static async Task<SKBitmap> DecodeOrientedAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return DecodeOriented(buffer.ToArray());
    }

    public static SKBitmap DecodeOriented(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new BadHttpRequestException("Invalid image content.", 400);
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
        {
            throw new BadHttpRequestException("Image dimensions are not supported.", 400);
        }

        var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            throw new BadHttpRequestException("Invalid image content.", 400);
        }

        return ApplyOrigin(decoded, codec.EncodedOrigin);
    }

    // Same transforms as PIL's ImageOps.exif_transpose. Takes ownership of `source`.
    public static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
        }

        float w = source.Width, h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity
        };

        var target = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.SetMatrix(matrix);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        source.Dispose();
        return target;
    }

    public static SKRectI CropRectangle(YoloBox box, int imageWidth, int imageHeight)
    {
        var left = Clamp01(box.Cx - box.Width / 2) * imageWidth;
        var top = Clamp01(box.Cy - box.Height / 2) * imageHeight;
        var right = Clamp01(box.Cx + box.Width / 2) * imageWidth;
        var bottom = Clamp01(box.Cy + box.Height / 2) * imageHeight;
        var x = Math.Clamp((int)Math.Floor(left), 0, Math.Max(0, imageWidth - 1));
        var y = Math.Clamp((int)Math.Floor(top), 0, Math.Max(0, imageHeight - 1));
        var width = Math.Clamp((int)Math.Ceiling(right) - x, 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Ceiling(bottom) - y, 1, imageHeight - y);
        return SKRectI.Create(x, y, width, height);
    }

    // Transparent pixels are flattened onto white so JPEG crops never come out black.
    public static byte[] EncodeJpegCrop(SKBitmap image, SKRectI rect, int quality = 92)
    {
        using var crop = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(crop))
        using (var source = SKImage.FromBitmap(image))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(source, SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height), SKRect.Create(0, 0, rect.Width, rect.Height), new SKSamplingOptions(SKFilterMode.Nearest));
        }

        using var encoded = crop.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encoded.ToArray();
    }

    static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
}
