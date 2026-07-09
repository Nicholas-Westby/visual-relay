using System.Diagnostics;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A differential test fixture holding two parallel repos over identical on-disk
/// files: a REAL git repo (driven by spawning the git binary, the way production's
/// <c>GitInvoker</c> does — <c>-C &lt;root&gt;</c>, combined output) and a GitSim repo.
/// The same argv is run against both and their exit codes + consumer-relevant output
/// compared. Real-git processes pin <c>GIT_CONFIG_GLOBAL</c>/<c>GIT_CONFIG_SYSTEM</c>
/// to <c>/dev/null</c> and an explicit author/committer identity, so results are
/// host-independent.
/// </summary>
internal sealed class ParityHarness : IDisposable
{
    private static readonly string GitBinary = File.Exists("/usr/bin/git") ? "/usr/bin/git" : "git";

    private static readonly Dictionary<string, string> Identity = new(StringComparer.Ordinal)
    {
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_SYSTEM"] = "/dev/null",
        ["GIT_AUTHOR_NAME"] = "Parity Test",
        ["GIT_AUTHOR_EMAIL"] = "parity@example.test",
        ["GIT_AUTHOR_DATE"] = "2020-09-13T12:26:40+00:00",
        ["GIT_COMMITTER_NAME"] = "Parity Test",
        ["GIT_COMMITTER_EMAIL"] = "parity@example.test",
        ["GIT_COMMITTER_DATE"] = "2020-09-13T12:26:40+00:00",
    };

    private readonly GitSimEngine _sim = new();

    public string RealRoot { get; } = NewTempDir();
    public string SimRoot { get; } = NewTempDir();

    public ParityHarness(string branch = "main")
    {
        RealGit("init", "-b", branch);
        _sim.InitRepo(SimRoot, branch);
    }

    public void WriteBoth(string rel, string content)
    {
        foreach (var root in new[] { RealRoot, SimRoot })
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    /// <summary>Writes a file to both repos, then stages and commits it in both (identical state).</summary>
    public void SeedCommit(string rel, string content, string message)
    {
        WriteBoth(rel, content);
        RealGit("add", "-A");
        SimGit("add", "-A");
        RealGit("commit", "-m", message);
        SimGit("commit", "-m", message);
    }

    public void DeleteBoth(string rel)
    {
        foreach (var root in new[] { RealRoot, SimRoot })
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
                File.Delete(full);
        }
    }

    public (int Exit, string Stdout, string Stderr) RealGit(params string[] args)
    {
        var psi = new ProcessStartInfo(GitBinary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RealRoot,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(RealRoot);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment.Remove("DEVELOPER_DIR");
        psi.Environment.Remove("SDKROOT");
        foreach (var (k, v) in Identity)
            psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    public (int Exit, string Output) SimGit(params string[] args)
    {
        var (exit, output, _) = _sim.RunAsync(SimRoot, args, CancellationToken.None).GetAwaiter().GetResult();
        return (exit, output);
    }

    /// <summary>Runs the argv on both and asserts equal exit codes.</summary>
    public void AssertExitParity(params string[] args)
    {
        var real = RealGit(args);
        var sim = SimGit(args);
        Assert.True(real.Exit == sim.Exit, $"exit mismatch for `git {string.Join(' ', args)}`: real={real.Exit} sim={sim.Exit}");
    }

    /// <summary>Asserts equal exit codes and the same set of path tokens on stdout (split on newline or NUL).</summary>
    public void AssertPathSetParity(char separator, params string[] args)
    {
        var real = RealGit(args);
        var sim = SimGit(args);
        Assert.Equal(real.Exit, sim.Exit);
        Assert.Equal(Tokens(real.Stdout, separator), Tokens(sim.Output, separator));
    }

    /// <summary>Asserts equal exit codes and byte-identical stdout.</summary>
    public void AssertExactParity(params string[] args)
    {
        var real = RealGit(args);
        var sim = SimGit(args);
        Assert.Equal(real.Exit, sim.Exit);
        Assert.Equal(real.Stdout, sim.Output);
    }

    /// <summary>Asserts equal exit codes and that both stdout values are a 40-hex sha.</summary>
    public void AssertShaShapeParity(params string[] args)
    {
        var real = RealGit(args);
        var sim = SimGit(args);
        Assert.Equal(real.Exit, sim.Exit);
        Assert.True(IsSha(real.Stdout), $"real not a sha: '{real.Stdout.Trim()}'");
        Assert.True(IsSha(sim.Output), $"sim not a sha: '{sim.Output.Trim()}'");
    }

    private static IReadOnlyList<string> Tokens(string output, char separator) =>
        output.Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('\r', '\n'))
            .Where(s => s.Length > 0)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static bool IsSha(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length == 40 && trimmed.All(Uri.IsHexDigit);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitsim-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(RealRoot);
        TestFileSystem.DeleteDirectoryResilient(SimRoot);
    }
}
