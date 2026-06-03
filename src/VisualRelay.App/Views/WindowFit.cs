using System;
using Avalonia;

namespace VisualRelay.App.Views;

/// <summary>
/// Pure geometry helpers for fitting and centering the main window inside a
/// screen's working area. Kept side-effect free so the clamp/centre maths can
/// be unit-tested without an Avalonia window.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// Clamps the desired window size down to the working area, independently
    /// per dimension, so the window never opens larger than the screen.
    /// </summary>
    public static (double Width, double Height) FitToWorkArea(
        double desiredWidth, double desiredHeight, double workAreaWidth, double workAreaHeight)
    {
        var width = Math.Min(desiredWidth, workAreaWidth);
        var height = Math.Min(desiredHeight, workAreaHeight);
        return (width, height);
    }

    /// <summary>
    /// Returns the top-left device-pixel position that centers a window of the
    /// given DIP size within the working area, never positioning off the top-left.
    /// </summary>
    public static PixelPoint CenterPosition(PixelRect workingArea, double width, double height, double scaling)
    {
        var pixelWidth = (int)Math.Round(width * scaling);
        var pixelHeight = (int)Math.Round(height * scaling);
        var x = workingArea.X + Math.Max(0, (workingArea.Width - pixelWidth) / 2);
        var y = workingArea.Y + Math.Max(0, (workingArea.Height - pixelHeight) / 2);
        return new PixelPoint(x, y);
    }
}
