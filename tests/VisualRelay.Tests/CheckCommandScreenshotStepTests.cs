using VisualRelay.Cli.Commands;

namespace VisualRelay.Tests;

/// <summary>
/// The gate's screenshot step. It renders into <c>.relay/scratch</c> and never
/// into <c>docs/images/</c> — rendering there left both committed PNGs modified
/// on every <c>check</c>, so a clean tree ended dirty and the images became
/// whatever somebody last forgot to revert. What the step proves instead is that
/// the render is a pure function of the commit: two renders of the main window
/// must come out byte for byte the same. Driven through an injected render
/// delegate, so no window is booted here.
/// </summary>
public sealed class CheckCommandScreenshotStepTests : IDisposable
{
    private readonly DirectoryInfo _scratch =
        Directory.CreateTempSubdirectory("vr-check-screenshots-");

    private string OutDir => Path.Combine(_scratch.FullName, "check-screenshots");

    public void Dispose() => _scratch.Delete(recursive: true);

    [Fact]
    public void RenderTarget_IsUnderRelayScratch_NotDocsImages()
    {
        var root = Path.Combine(_scratch.FullName, "repo");

        var target = CheckCommand.ScreenshotCheckDir(root);

        Assert.Equal(Path.Combine(root, ".relay", "scratch", "check-screenshots"), target);
        Assert.DoesNotContain(Path.Combine("docs", "images"), target, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoIdenticalMainRenders_PassAndTheCompactSizeIsRenderedOnce()
    {
        var rendered = new List<(string Output, int Width, int Height)>();

        var (exitCode, message) = CheckCommand.RenderAndCompare(OutDir, (output, width, height) =>
        {
            rendered.Add((output, width, height));
            File.WriteAllBytes(output, [0x89, 0x50, 0x4E, 0x47]);
            return 0;
        });

        Assert.Equal(0, exitCode);
        Assert.Null(message);
        Assert.Equal([(1440, 900), (1440, 900), (1060, 720)], rendered.Select(r => (r.Width, r.Height)));
        Assert.All(rendered, r => Assert.StartsWith(OutDir, r.Output, StringComparison.Ordinal));
    }

    [Fact]
    public void OneDifferingByte_FailsAndBlamesTheClockOrTheMachine()
    {
        var calls = 0;

        var (exitCode, message) = CheckCommand.RenderAndCompare(OutDir, (output, _, _) =>
        {
            File.WriteAllBytes(output, [0x89, 0x50, 0x4E, calls++ == 0 ? (byte)0x47 : (byte)0x48]);
            return 0;
        });

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "visual-relay: the screenshot render is not deterministic (1 bytes differ); "
            + "something in the UI reads the clock or the machine",
            message);
        // The compact size is never reached: the determinism failure is the answer.
        Assert.Equal(2, calls);
    }

    [Fact]
    public void AFailingRender_ShortCircuitsWithItsOwnExitCode()
    {
        var calls = 0;

        var (exitCode, message) = CheckCommand.RenderAndCompare(OutDir, (_, _, _) =>
        {
            calls++;
            return 3;
        });

        Assert.Equal(3, exitCode);
        Assert.Null(message);
        Assert.Equal(1, calls);
    }
}
