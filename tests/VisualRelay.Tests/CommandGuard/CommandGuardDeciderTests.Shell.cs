using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // shell mode — strip tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Shell_GitCommit_NoVerifyLongFlag_Stripped()
    {
        var payload = Payload("shell", "git commit --no-verify -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitCommit_NoVerifyShortFlag_Stripped()
    {
        var payload = Payload("shell", "git commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitCommit_NoVerifyLongFlag_OnlyFlag_Stripped()
    {
        var payload = Payload("shell", "git commit --no-verify");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit", result.Command);
    }

    [Fact]
    public void Shell_GitWithCDash_CommitNoVerify_Stripped()
    {
        var payload = Payload("shell", "git -C /r commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git -C /r commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitWithCDash_CommitLongNoVerify_Stripped()
    {
        var payload = Payload("shell", "git -C /r commit --no-verify -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git -C /r commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitWithCConfig_CommitNoVerify_Stripped()
    {
        var payload = Payload("shell", "git -c user.name=bot commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git -c user.name=bot commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitWithGitDir_CommitNoVerify_Stripped()
    {
        var payload = Payload("shell", "git --git-dir=/tmp/g commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git --git-dir=/tmp/g commit -m x", result.Command);
    }

    [Fact]
    public void Shell_GitPush_ShortFlagN_Kept()
    {
        var payload = Payload("shell", "git push -n");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_GitPush_LongNoVerify_Stripped()
    {
        var payload = Payload("shell", "git push --no-verify");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git push", result.Command);
    }

    [Fact]
    public void Shell_GitMerge_ShortFlagN_Kept()
    {
        var payload = Payload("shell", "git merge -n feature");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_GitMerge_LongNoVerify_Stripped()
    {
        var payload = Payload("shell", "git merge --no-verify feature");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git merge feature", result.Command);
    }

    [Fact]
    public void Shell_SortN_Unchanged()
    {
        var payload = Payload("shell", "sort -n file");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_GrepN_Unchanged()
    {
        var payload = Payload("shell", "grep -n foo");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_HeadN_Unchanged()
    {
        var payload = Payload("shell", "head -n 5 f");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_EchoN_Unchanged()
    {
        var payload = Payload("shell", "echo -n hi");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_LsLa_Unchanged()
    {
        var payload = Payload("shell", "ls -la");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Shell_CombinedShortFlags_Nm_StrippedN_KeepsM()
    {
        var payload = Payload("shell", "git commit -nm msg");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m msg", result.Command);
    }

    [Fact]
    public void Shell_CombinedShortFlags_Nmf_StrippedN_KeepsMf()
    {
        var payload = Payload("shell", "git commit -nmf msg");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -mf msg", result.Command);
    }

    [Fact]
    public void Shell_NoVerifyBeforeSubcommand_Stripped()
    {
        var payload = Payload("shell", "git --no-verify commit -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_NoVerifyAndShortNBoth_Stripped()
    {
        var payload = Payload("shell", "git commit --no-verify -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Shell_ByteExactPreservation_AroundStrippedFlag()
    {
        var payload = Payload("shell", "git  commit  --no-verify  -m  \"hello world\"");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git  commit  -m  \"hello world\"", result.Command);
    }

    [Fact]
    public void Shell_ByteExactPreservation_ShortN()
    {
        var payload = Payload("shell", "git  commit  -n  -m  x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git  commit  -m  x", result.Command);
    }

    [Fact]
    public void Shell_MultipleGitOptionsBeforeCommit_StripsN()
    {
        var payload = Payload("shell",
            "git -C /tmp -c a=b --git-dir=/g commit -n -m x");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git -C /tmp -c a=b --git-dir=/g commit -m x", result.Command);
    }

    [Fact]
    public void Shell_NonGitCommand_Unchanged()
    {
        var payload = Payload("shell", "some-tool --no-verify run");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }
}
