using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    /// <summary>
    /// Renders the live MainWindow to a PNG ON THE UI THREAD and returns the raw
    /// bytes. When <paramref name="path"/> is a non-empty absolute path, the PNG
    /// is ALSO written there and the resolved path is returned so the server can
    /// surface it via the X-Screenshot-Path header.
    /// </summary>
    public Task<(byte[] Png, string? WrittenPath)> CaptureScreenshotAsync(string? path) =>
        Dispatcher.UIThread.InvokeAsync(() => CaptureOnUiThread(path)).GetTask();

    private (byte[] Png, string? WrittenPath) CaptureOnUiThread(string? path)
    {
        var pixelSize = ResolvePixelSize();
        // Match the window's render scaling so the capture is crisp on HiDPI.
        var dpi = window.RenderScaling > 0 ? 96 * window.RenderScaling : 96;

        using var rtb = new RenderTargetBitmap(pixelSize, new Vector(dpi, dpi));
        rtb.Render(window);

        using var memory = new MemoryStream();
        rtb.Save(memory);
        var bytes = memory.ToArray();

        string? written = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(full, bytes);
            written = full;
        }

        return (bytes, written);
    }

    /// <summary>
    /// The window's pixel size, falling back to its logical Width/Height (scaled)
    /// and finally to a sane default when the window has not been measured yet
    /// (e.g. never shown). Never returns a zero/negative dimension — RenderTargetBitmap
    /// throws on those.
    /// </summary>
    private PixelSize ResolvePixelSize()
    {
        var clientSize = window.ClientSize;
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;

        var width = clientSize.Width > 0 ? clientSize.Width : window.Width;
        var height = clientSize.Height > 0 ? clientSize.Height : window.Height;

        if (double.IsNaN(width) || width <= 0)
        {
            width = 1440;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 900;
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scaling));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scaling));
        return new PixelSize(pixelWidth, pixelHeight);
    }
}
