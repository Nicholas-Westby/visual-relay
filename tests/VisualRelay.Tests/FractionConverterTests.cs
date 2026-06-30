using System.Globalization;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="FractionConverter"/>.
/// </summary>
public sealed class FractionConverterTests
{
    [Theory]
    [InlineData(100.0, "0.5", 50.0)]
    [InlineData(200.0, "0.75", 150.0)]
    [InlineData(400.0, "0.25", 100.0)]
    [InlineData(0.0, "0.75", 0.0)]
    [InlineData(100.0, "1.0", 100.0)]
    public void Convert_MultipliesByParameter(double value, string parameter, double expected)
    {
        var result = FractionConverter.Instance.Convert(
            value, typeof(double), parameter, CultureInfo.InvariantCulture);

        Assert.IsType<double>(result);
        Assert.Equal(expected, (double)result!, 9);
    }

    [Fact]
    public void Convert_NoParameter_DefaultsToPoint75()
    {
        var result = FractionConverter.Instance.Convert(
            200.0, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.IsType<double>(result);
        Assert.Equal(150.0, (double)result!, 9); // 200 * 0.75 = 150
    }

    [Fact]
    public void Convert_NullValue_ReturnsNull()
    {
        var result = FractionConverter.Instance.Convert(
            null, typeof(double), "0.5", CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_NonDoubleValue_ReturnsNull()
    {
        var result = FractionConverter.Instance.Convert(
            "not-a-number", typeof(double), "0.5", CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_NonNumericParameter_DefaultsToPoint75()
    {
        var result = FractionConverter.Instance.Convert(
            200.0, typeof(double), "not-a-number", CultureInfo.InvariantCulture);

        Assert.IsType<double>(result);
        Assert.Equal(150.0, (double)result!, 9); // falls back to 0.75
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        var a = FractionConverter.Instance;
        var b = FractionConverter.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void ConvertBack_ReturnsValueUnchanged()
    {
        var result = FractionConverter.Instance.ConvertBack(
            42.0, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(42.0, result);
    }

    [Fact]
    public void ConvertBack_Null_ReturnsNull()
    {
        var result = FractionConverter.Instance.ConvertBack(
            null, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }
}
