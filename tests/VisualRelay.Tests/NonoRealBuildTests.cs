using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Real in-sandbox build tests (.NET restore+build+test, Swift build+test,
/// Node npm install+test). <b>Opt-in only</b>: gated behind
/// <c>VR_RUN_NONO_INTEGRATION=1</c> — skipped by default even when nono IS
/// installed, so the default <c>dotnet test</c> stays fast.
/// </summary>
public sealed class NonoRealBuildTests
{
    [Fact]
    public async Task RealBuild_DotNet_RestoreBuildTest_InSandbox()
    {
        SkipIfNotOptedIn();
        if (!ToolAvailable("dotnet")) Assert.Skip("dotnet is not on PATH");

        using var repo = CreateScratchRepo("dotnet-sandbox-test");
        await CreateMinimalDotNetProjectAsync(repo);

        var config = TestConfig();
        var sut = new SandboxedTestRunner(
            new ShellTestRunner(TimeSpan.FromMinutes(3)), config);

        Assert.Equal(0, (await sut.RunAsync(repo.Root, "dotnet restore", CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await sut.RunAsync(repo.Root, "dotnet build --nologo", CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await sut.RunAsync(repo.Root, "dotnet test --nologo", CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task RealBuild_Swift_BuildTest_InSandbox()
    {
        SkipIfNotOptedIn();
        if (!ToolAvailable("swift")) Assert.Skip("swift is not on PATH");

        using var repo = CreateScratchRepo("swift-sandbox-test");
        await CreateMinimalSwiftProjectAsync(repo);

        var config = TestConfig();
        var sut = new SandboxedTestRunner(
            new ShellTestRunner(TimeSpan.FromMinutes(3)), config);

        Assert.Equal(0, (await sut.RunAsync(repo.Root, "swift build", CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await sut.RunAsync(repo.Root, "swift test", CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task RealBuild_Node_NpmInstallTest_InSandbox()
    {
        SkipIfNotOptedIn();
        if (!ToolAvailable("node") || !ToolAvailable("npm"))
            Assert.Skip("node/npm is not on PATH");

        using var repo = CreateScratchRepo("node-sandbox-test");
        await CreateMinimalNodeProjectAsync(repo);

        var config = TestConfig();
        var sut = new SandboxedTestRunner(
            new ShellTestRunner(TimeSpan.FromMinutes(3)), config);

        Assert.Equal(0, (await sut.RunAsync(repo.Root, "npm install", CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await sut.RunAsync(repo.Root, "npm test", CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task RealVerify_ShellCommandWithSpaces_RunsThroughNonoSandbox()
    {
        SkipIfNotOptedIn();

        using var repo = CreateScratchRepo("shell-verify-sandbox");
        var config = TestConfig();
        var sut = new SandboxedTestRunner(
            new ShellTestRunner(TimeSpan.FromMinutes(1)), config);

        // A spaced, always-green command exercising the full nono-wrapped shell
        // verify. Before the -c/command split fix this returned exit 2
        // ("/bin/sh: - : invalid option") for ANY command, because -c and the
        // command were merged into one argument /bin/sh could not parse.
        var result = await sut.RunAsync(repo.Root, "echo verify ok && true", CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void SkipIfNotOptedIn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VR_RUN_NONO_INTEGRATION"),
                "1", StringComparison.Ordinal))
        {
            Assert.Skip("VR_RUN_NONO_INTEGRATION=1 required for real in-sandbox builds.");
        }
        if (!ToolAvailable("nono"))
            Assert.Skip("nono is not on PATH.");
    }

    private static bool ToolAvailable(string name) =>
        !string.IsNullOrEmpty(FindOnPath(name));

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        return pathEnv.Split(sep)
            .Select(dir => Path.Combine(dir.Trim(), name))
            .FirstOrDefault(File.Exists);
    }

    private static ScratchRepo CreateScratchRepo(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "visual-relay-tests", name);
        TestFileSystem.DeleteDirectoryResilient(root);
        Directory.CreateDirectory(root);
        return new ScratchRepo(root);
    }

    private static async Task CreateMinimalDotNetProjectAsync(ScratchRepo repo)
    {
        var psi = new ProcessStartInfo("dotnet",
            "new xunit --no-restore --output . --name SandboxTest")
        {
            WorkingDirectory = repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet new xunit failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    private static async Task CreateMinimalSwiftProjectAsync(ScratchRepo repo)
    {
        var psi = new ProcessStartInfo("swift",
            "package init --type executable --name SandboxTest")
        {
            WorkingDirectory = repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "Package.swift"),
                """
                // swift-tools-version: 5.9
                import PackageDescription
                let package = Package(
                    name: "SandboxTest",
                    targets: [.executableTarget(name: "SandboxTest")]
                )
                """);
            Directory.CreateDirectory(Path.Combine(repo.Root, "Sources", "SandboxTest"));
            await File.WriteAllTextAsync(
                Path.Combine(repo.Root, "Sources", "SandboxTest", "main.swift"),
                "print(\"hello\")");
        }
    }

    private static async Task CreateMinimalNodeProjectAsync(ScratchRepo repo)
    {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "package.json"),
            """{"name":"sandbox-test","version":"1.0.0","scripts":{"test":"node -e 'console.log(\"ok\")'"},"devDependencies":{}}""");
    }

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2);

    private sealed class ScratchRepo(string root) : IDisposable
    {
        public string Root => root;
        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }
}
