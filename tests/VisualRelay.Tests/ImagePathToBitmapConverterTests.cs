using System.Globalization;
using Avalonia.Media.Imaging;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ImagePathToBitmapConverter"/>.
/// </summary>
[Collection("Headless")]
public sealed class ImagePathToBitmapConverterTests
{
    // Minimal 1×1 red PNG (valid, decodable).
    private static readonly byte[] MinimalPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static string WriteTempFile(string extension, byte[]? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vr-converter-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content ?? Array.Empty<byte>());
        return path;
    }

    [AvaloniaFact]
    public void Convert_PngPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".png", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_JpgPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".jpg", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_JpegPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".jpeg", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_GifPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".gif", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_BmpPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".bmp", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_WebpPath_ReturnsNonNullBitmap()
    {
        var path = WriteTempFile(".webp", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.IsType<Bitmap>(result);
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_TxtPath_ReturnsNull()
    {
        var path = WriteTempFile(".txt", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_MdPath_ReturnsNull()
    {
        var path = WriteTempFile(".md", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_PdfPath_ReturnsNull()
    {
        var path = WriteTempFile(".pdf", MinimalPngBytes);
        try
        {
            var result = ImagePathToBitmapConverter.Instance.Convert(
                path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Convert_NullPath_ReturnsNull()
    {
        var result = ImagePathToBitmapConverter.Instance.Convert(
            null, typeof(Bitmap), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_EmptyPath_ReturnsNull()
    {
        var result = ImagePathToBitmapConverter.Instance.Convert(
            string.Empty, typeof(Bitmap), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vr-nonexistent-{Guid.NewGuid():N}.png");
        var result = ImagePathToBitmapConverter.Instance.Convert(
            path, typeof(Bitmap), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_Instance_IsSingleton()
    {
        var a = ImagePathToBitmapConverter.Instance;
        var b = ImagePathToBitmapConverter.Instance;
        Assert.Same(a, b);
    }

    [AvaloniaFact]
    public void ConvertBack_ReturnsValueUnchanged()
    {
        // Create a minimal Bitmap via a memory stream.
        using var stream = new MemoryStream(MinimalPngBytes);
        var bitmap = new Bitmap(stream);
        var result = ImagePathToBitmapConverter.Instance.ConvertBack(
            bitmap, typeof(Bitmap), null, CultureInfo.InvariantCulture);
        Assert.Same(bitmap, result);
    }

    [AvaloniaFact]
    public void ConvertBack_Null_ReturnsNull()
    {
        var result = ImagePathToBitmapConverter.Instance.ConvertBack(
            null, typeof(Bitmap), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }
}
