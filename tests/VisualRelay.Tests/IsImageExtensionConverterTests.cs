using System.Globalization;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="IsImageExtensionConverter"/>.
/// </summary>
public sealed class IsImageExtensionConverterTests
{
    [Theory]
    [InlineData(".png", true)]
    [InlineData(".PNG", true)]
    [InlineData(".jpg", true)]
    [InlineData(".JPG", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".JPEG", true)]
    [InlineData(".gif", true)]
    [InlineData(".GIF", true)]
    [InlineData(".bmp", true)]
    [InlineData(".BMP", true)]
    [InlineData(".webp", true)]
    [InlineData(".WEBP", true)]
    [InlineData(".txt", false)]
    [InlineData(".md", false)]
    [InlineData(".pdf", false)]
    [InlineData(".cs", false)]
    [InlineData(".json", false)]
    [InlineData("", false)]
    public void Convert_ReturnsExpected(string extension, bool expected)
    {
        var path = extension.Length > 0
            ? $"/tmp/test{extension}"
            : string.Empty;

        var result = IsImageExtensionConverter.Instance.Convert(
            path, typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NullPath_ReturnsFalse()
    {
        var result = IsImageExtensionConverter.Instance.Convert(
            null, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        var a = IsImageExtensionConverter.Instance;
        var b = IsImageExtensionConverter.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void ConvertBack_ReturnsValueUnchanged()
    {
        var result = IsImageExtensionConverter.Instance.ConvertBack(
            true, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertBack_Null_ReturnsNull()
    {
        var result = IsImageExtensionConverter.Instance.ConvertBack(
            null, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }
}
