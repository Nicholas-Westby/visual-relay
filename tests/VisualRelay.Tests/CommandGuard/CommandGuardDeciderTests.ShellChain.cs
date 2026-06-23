using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // shell mode — chain / operator-boundary scoping (defects 2 & 3)
    //
    // These tests MUST FAIL against the current tree:
    //   Defect 2 (over-strip):  -n belonging to a later non-commit
    //     subcommand is wrongly stripped because the single-subIdx
    //     logic leaks across shell operators.
    //   Defect 3 (under-strip): git commit -n after ; / && / ( / env
    //     prefix is passed through because the early-out requires
    //     tokens[0] == "git".
    // ═══════════════════════════════════════════════════════════════════

    // ── Defect 2 — over-strip: -n on non-commit subcommands must be
    //    preserved ────────────────────────────────────────────────────

    [Fact]
    public void Shell_ChainWithGrepN_PreservesGrepN()
    {
        var payload = Payload("shell", "git commit -m x && git grep -n foo");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough,
            "grep's -n (--line-number) must be preserved — the strip must not leak across &&");
    }

    [Fact]
    public void Shell_ChainWithGitLogN_PreservesLogN()
    {
        var payload = Payload("shell", "git commit -m x; git log -n 5");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough,
            "log's -n must be preserved — the strip must not leak across ;");
    }

    [Fact]
    public void Shell_ChainWithCommitN_AndGrepN_StripsOnlyCommitN()
    {
        var payload = Payload("shell", "git commit -n -m x && git grep -n foo");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x && git grep -n foo", result.Command);
    }

    // ── Defect 3 — under-strip: git commit -n after a shell operator
    //    or env-var prefix must be stripped ───────────────────────────

    [Fact]
    public void Shell_EchoThenCommitN_StripsN()
    {
        var payload = Payload("shell", "echo hi; git commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("echo hi; git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_SubshellCommitN_StripsN()
    {
        var payload = Payload("shell", "(git commit -n -m x)");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("(git commit -m x)", result.Command);
    }

    [Fact]
    public void Shell_EnvPrefixCommitN_StripsN()
    {
        var payload = Payload("shell", "FOO=1 git commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("FOO=1 git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GrepThenCommitN_StripsN()
    {
        var payload = Payload("shell", "git grep foo && git commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git grep foo && git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GluedOperatorCommitN_StripsN()
    {
        var payload = Payload("shell", "true&&git commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("true&&git commit -m x", result.Command);
    }
}
