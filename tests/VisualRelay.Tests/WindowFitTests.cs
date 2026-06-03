using Avalonia;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

public sealed class WindowFitTests
{
    [Fact]
    public void FitToWorkArea_UsesDesiredWhenScreenIsLarger()
    {
        var (width, height) = WindowFit.FitToWorkArea(1440, 900, 1920, 1080);

        Assert.Equal(1440, width);
        Assert.Equal(900, height);
    }

    [Fact]
    public void FitToWorkArea_ClampsToWorkAreaWhenScreenIsSmaller()
    {
        var (width, height) = WindowFit.FitToWorkArea(1440, 900, 1280, 720);

        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void FitToWorkArea_ClampsEachDimensionIndependently()
    {
        var (width, height) = WindowFit.FitToWorkArea(1440, 900, 1000, 2000);

        Assert.Equal(1000, width);
        Assert.Equal(900, height);
    }

    [Fact]
    public void CenterPosition_CentersWithinWorkArea()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1080);

        var position = WindowFit.CenterPosition(workingArea, 1000, 600, scaling: 1);

        Assert.Equal(460, position.X);
        Assert.Equal(240, position.Y);
    }

    [Fact]
    public void CenterPosition_OffsetsByWorkAreaOrigin()
    {
        var workingArea = new PixelRect(100, 50, 1920, 1080);

        var position = WindowFit.CenterPosition(workingArea, 1000, 600, scaling: 1);

        Assert.Equal(560, position.X);
        Assert.Equal(290, position.Y);
    }
}
