using System.Runtime.InteropServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Hermetic coverage for <c>tools/dotnet-test-files.sh</c> — the stage-5 red-gate
/// narrower wired into this repo's <c>testFileCmd</c>. Runs the REAL committed
/// script with a fake <c>dotnet</c> first on PATH (so nothing is built or run)
/// and asserts the <c>dotnet --filter</c> it constructs from authored .cs paths.
/// </summary>
public sealed class DotnetTestFilesScriptTests
{
    private static bool PosixUnsupported =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
        !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    [Fact]
    public async Task NarrowsCsPathsToFullyQualifiedNameFilter()
    {
        if (PosixUnsupported) return;

        var output = await RunScriptAsync(
            "tests/VisualRelay.Tests/FooTests.cs",
            "tests/VisualRelay.Tests/BarTests.Part.cs", // partial-class file
            "README.md");                               // non-.cs, excluded

        Assert.Contains("--filter", output);
        // Class names derived from file stems; the partial-class ".Part" suffix is
        // stripped; the non-.cs README contributes nothing.
        Assert.Contains("FullyQualifiedName~FooTests|FullyQualifiedName~BarTests", output);
        Assert.DoesNotContain("README", output);
        Assert.DoesNotContain("Part", output);
    }

    [Fact]
    public async Task NoCsFiles_RunsFullSuite_WithoutFilter()
    {
        if (PosixUnsupported) return;

        var output = await RunScriptAsync("README.md");

        Assert.DoesNotContain("--filter", output);
        Assert.Contains("VisualRelay.Tests.csproj", output);
    }

    private static async Task<string> RunScriptAsync(params string[] args)
    {
        var script = Path.Combine(RepoSetup.Root, "tools", "dotnet-test-files.sh");
        Assert.True(File.Exists(script), $"missing script: {script}");

        // Fake `dotnet` that echoes its argv, first on PATH so the script's
        // `exec dotnet test ...` is captured instead of really running anything.
        var bin = Path.Combine(Path.GetTempPath(), "vr-fake-dotnet", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        try
        {
            // WriteExecutableAsync handles the (OS-guarded) chmod for us.
            await SwivalTestHelpers.WriteExecutableAsync(bin, "dotnet", "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");

            var argv = new List<string> { script };
            argv.AddRange(args);
            var path = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            var (exit, output, timedOut) = await ProcessCapture.RunAsync(
                "/bin/sh", argv, RepoSetup.Root, TimeSpan.FromSeconds(20),
                CancellationToken.None,
                environment: new Dictionary<string, string> { ["PATH"] = path });

            Assert.False(timedOut);
            Assert.Equal(0, exit);
            return output;
        }
        finally
        {
            try { Directory.Delete(bin, recursive: true); } catch { /* best effort */ }
        }
    }
}
