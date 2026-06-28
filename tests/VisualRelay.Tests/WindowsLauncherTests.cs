using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Hermetic tests for the Windows launcher <c>visual-relay.ps1</c>, the sibling of
/// the bash <c>visual-relay</c> (mirrors <see cref="Installer5Bootstrap2LauncherTests"/>).
/// They run the real <c>.ps1</c> with a crafted PATH and stub <c>dotnet</c>/<c>git</c>/
/// <c>uv</c> recording argv, asserting it execs
/// <c>dotnet run --project …VisualRelay.Cli… -- &lt;cmd&gt; &lt;args&gt;</c> with args
/// (including a space-containing one) intact, and that a missing-tool +
/// non-interactive run prints the manual one-liner and does not install. Gated to
/// Windows (where <c>powershell.exe</c> and the <c>.cmd</c> stubs work), so they run
/// on the Phase 4 Windows CI and skip — never silently pass — elsewhere.
/// </summary>
public sealed class WindowsLauncherTests
{
    private static string Ps1 => Path.Combine(RepoSetup.Root, "visual-relay.ps1");

    private static string SystemRoot =>
        Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    private static string PowerShellExe =>
        Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    // A minimal PATH carrying only PowerShell's own essentials plus the stub dir,
    // so the real dotnet/git/uv on the machine PATH never leak into the test.
    private static string MinimalPath(string stubBin) => string.Join(';',
        stubBin,
        Path.Combine(SystemRoot, "System32"),
        SystemRoot,
        Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0"));

    // ReSharper disable once UnusedTupleComponentInReturnValue — callers discard stdout intentionally
    private static (int ExitCode, string Stdout, string Stderr) RunPs1(
        string stubBin, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(PowerShellExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Ps1);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        psi.Environment["PATH"] = MinimalPath(stubBin);
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NewStubBin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-ps1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCmdStub(string stubBin, string name, string body) =>
        File.WriteAllText(Path.Combine(stubBin, name + ".cmd"), "@echo off\r\n" + body + "\r\n");

    [Fact]
    public void Launch_ExecsCliViaDotnetRun_WithArgsIntact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only launcher (powershell + .cmd stubs)");

        var stubBin = NewStubBin();
        var argvFile = Path.Combine(stubBin, "dotnet-argv.txt");
        try
        {
            // dotnet stub: report a .NET 10 SDK for the detection probe, then record
            // each received arg on its OWN line (boundary-preserving, like the bash
            // test's `printf '%s\n' "$@"`) so a space-containing arg can be asserted
            // as a single token rather than a flattened %* string.
            WriteCmdStub(stubBin, "dotnet",
                "if \"%~1\"==\"--list-sdks\" ( echo 10.0.100 [C:\\sdk]& exit /b 0 )\r\n" +
                ":loop\r\n" +
                "if \"%~1\"==\"\" goto done\r\n" +
                ">>\"%VR_DOTNET_ARGV%\" echo %~1\r\n" +
                "shift\r\n" +
                "goto loop\r\n" +
                ":done");
            WriteCmdStub(stubBin, "git", "exit /b 0");
            WriteCmdStub(stubBin, "uv", "exit /b 0");

            var (exit, _, stderr) = RunPs1(stubBin,
                ["launch", "arg with spaces"],
                new Dictionary<string, string> { ["VR_DOTNET_ARGV"] = argvFile });

            Assert.True(File.Exists(argvFile), $"launcher never invoked dotnet run. stderr:\n{stderr}");
            var argv = File.ReadAllLines(argvFile).Select(l => l.Trim()).ToList();
            Assert.Contains("run", argv);
            Assert.Contains("--project", argv);
            Assert.Contains(argv, l => l.EndsWith("VisualRelay.Cli.csproj", StringComparison.Ordinal));
            Assert.Contains("--", argv);
            Assert.Contains("launch", argv);
            // The space-containing arg survives as ONE token (full-line match).
            Assert.Contains("arg with spaces", argv);
            Assert.Equal(0, exit);
        }
        finally { TryDelete(stubBin); }
    }

    [Fact]
    public void MissingDotnet_NonInteractive_PrintsHint_DoesNotInstall_NonZero()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only launcher (powershell + .cmd stubs)");

        var stubBin = NewStubBin();
        var installerRan = Path.Combine(stubBin, "installer-ran.txt");
        try
        {
            // No dotnet stub: the launcher must detect the missing SDK. A stub
            // installer that, if ever run, leaves evidence — it must NOT run in a
            // non-interactive context (redirected stdio ⇒ no TTY).
            WriteCmdStub(stubBin, "vr-dotnet-installer", $">\"{installerRan}\" echo ran");

            var (exit, _, stderr) = RunPs1(stubBin,
                ["launch"],
                new Dictionary<string, string>
                {
                    ["VISUAL_RELAY_DOTNET_INSTALLER"] = Path.Combine(stubBin, "vr-dotnet-installer.cmd"),
                });

            Assert.NotEqual(0, exit);
            Assert.Matches("(?i)dotnet-install|winget install Microsoft.DotNet|\\.NET 10", stderr);
            Assert.False(File.Exists(installerRan), "installer must not run in a non-interactive context");
        }
        finally { TryDelete(stubBin); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
