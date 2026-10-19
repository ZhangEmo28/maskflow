using SkiaSharp;

namespace MaskFlow.Api.Tests;

public class ImageToolsTests
{
    // 64x32 JPEG: left half red, right half blue, tagged with the given EXIF orientation.
    static byte[] JpegWithOrientation(ushort orientation)
    {
        using var bitmap = new SKBitmap(64, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawRect(SKRect.Create(0, 0, 32, 32), new SKPaint { Color = SKColors.Red });
            canvas.DrawRect(SKRect.Create(32, 0, 32, 32), new SKPaint { Color = SKColors.Blue });
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = encoded.ToArray();
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }

    static bool IsRed(SKColor color) => color.Red > 180 && color.Blue < 80;
    static bool IsBlue(SKColor color) => color.Blue > 180 && color.Red < 80;

    [Fact]
    public void DecodeOriented_RotatesPortraitPhotos()
    {
        using var image = ImageTools.DecodeOriented(JpegWithOrientation(6));

        Assert.Equal(32, image.Width);
        Assert.Equal(64, image.Height);
        // Orientation 6 = rotate 90° clockwise: the stored left (red) half becomes the top.
        Assert.True(IsRed(image.GetPixel(16, 12)));
        Assert.True(IsBlue(image.GetPixel(16, 52)));
    }

    [Fact]
    public void DecodeOriented_LeavesNormalImagesAlone()
    {
        using var image = ImageTools.DecodeOriented(JpegWithOrientation(1));
        Assert.Equal(64, image.Width);
        Assert.True(IsRed(image.GetPixel(12, 16)));
        Assert.True(IsBlue(image.GetPixel(52, 16)));
    }

    [Fact]
    public void EncodeJpegCrop_CropsOrientedPixels()
    {
        using var image = ImageTools.DecodeOriented(JpegWithOrientation(6));
        var rect = ImageTools.CropRectangle(new YoloBox(0.5, 0.25, 1, 0.5), image.Width, image.Height);
        Assert.Equal(SKRectI.Create(0, 0, 32, 32), rect);

        using var crop = SKBitmap.Decode(ImageTools.EncodeJpegCrop(image, rect));
        Assert.Equal(32, crop.Width);
        Assert.True(IsRed(crop.GetPixel(16, 16)));
    }

    [Fact]
    public void DecodeOriented_RejectsGarbage()
    {
        Assert.Throws<Microsoft.AspNetCore.Http.BadHttpRequestException>(() => ImageTools.DecodeOriented([1, 2, 3, 4]));
    }

    [Theory]
    [InlineData("..", "_")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData(" car ", "car")]
    [InlineData("安全帽", "安全帽")]
    public void SanitizeFileName_ProducesSafeZipSegments(string input, string expected)
    {
        Assert.Equal(expected, MaskFlowStore.SanitizeFileName(input));
    }
}
