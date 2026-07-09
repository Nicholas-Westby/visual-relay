namespace VisualRelay.Tests;

/// <summary>
/// Differential parity: drives an identical scripted argv sequence against the real
/// git binary and against GitSim over identical on-disk files, comparing exit codes
/// and the consumer-relevant output. Opt-in via <c>VR_RUN_SLOW_INTEGRATION=1</c>.
/// Covers the working-tree query and mutation groups; plumbing lives in the companion
/// class.
/// </summary>
public sealed class GitSimParityTests
{
    private static bool Ready()
    {
        SlowIntegration.SkipIfNotOptedIn();
        if (!SlowIntegration.ToolAvailable("git"))
        {
            Assert.Skip("git binary not on PATH.");
            return false;
        }

        return true;
    }

    [Fact]
    public void RevParse_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.AssertExactParity("rev-parse", "--is-inside-work-tree");
        h.AssertExitParity("rev-parse", "--verify", "--quiet", "HEAD"); // unborn → exit 1 both
        h.SeedCommit("a.txt", "1", "seed");
        h.AssertShaShapeParity("rev-parse", "HEAD");
    }

    [Fact]
    public void LsFiles_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.WriteBoth(".gitignore", "*.log\n");
        h.SeedCommit("a.txt", "1", "seed");
        h.WriteBoth("u.txt", "x");
        h.WriteBoth("build.log", "noise");

        h.AssertPathSetParity('\n', "ls-files");
        h.AssertPathSetParity('\n', "ls-files", "--others", "--exclude-standard");
        h.AssertPathSetParity('\n', "ls-files", "--others", "--ignored", "--exclude-standard", "--directory");
    }

    [Fact]
    public void Diff_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "seed");
        h.WriteBoth("a.txt", "2");
        h.AssertExactParity("diff", "HEAD", "--name-only", "-z");
        h.AssertExitParity("diff", "--quiet", "HEAD", "--", "a.txt");
    }

    [Fact]
    public void Status_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "seed");
        h.AssertExactParity("status", "--porcelain"); // clean → empty both
        h.WriteBoth("a.txt", "2");
        h.WriteBoth("u.txt", "x");
        h.AssertExactParity("status", "--porcelain"); // " M a.txt" + "?? u.txt"
    }

    [Fact]
    public void AddCommit_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "seed");
        h.WriteBoth("b.txt", "2");
        h.AssertExitParity("add", "-A", "--", "b.txt");
        h.AssertExitParity("commit", "-m", "feat: add file");
        h.AssertShaShapeParity("rev-parse", "HEAD");
    }

    [Fact]
    public void CheckIgnore_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.WriteBoth(".gitignore", "swival.toml\ndir/\n");
        h.SeedCommit("a.txt", "1", "seed");
        h.AssertPathSetParity('\n', "check-ignore", "--", "swival.toml", "src/app.cs", "dir/nested.txt");
        h.AssertExitParity("check-ignore", "--", "src/app.cs"); // none ignored → exit 1 both
    }
}
