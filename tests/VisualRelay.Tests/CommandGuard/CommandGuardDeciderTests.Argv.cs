using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // argv mode — strip tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Argv_GitCommit_NoVerifyLongFlag_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "--no-verify", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitCommit_NoVerifyShortFlag_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitCommit_NoVerifyLongFlag_OnlyFlag_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit" }, result.Command);
    }

    [Fact]
    public void Argv_GitCommit_NoVerifyShortFlag_OnlyFlag_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "-n"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit" }, result.Command);
    }

    [Fact]
    public void Argv_GitWithCDash_CommitNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "-C", "/r", "commit", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "-C", "/r", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitWithCDash_CommitLongNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "-C", "/r", "commit", "--no-verify", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "-C", "/r", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitWithCDashValue_CommitNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "-c", "user.name=bot", "commit", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "-c", "user.name=bot", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitWithGitDir_CommitNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "--git-dir=/tmp/g", "commit", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "--git-dir=/tmp/g", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_GitPush_ShortFlagN_Kept()
    {
        var payload = Payload("argv", ["git", "push", "-n"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_GitPush_LongNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "push", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "push" }, result.Command);
    }

    [Fact]
    public void Argv_GitMerge_ShortFlagN_Kept()
    {
        var payload = Payload("argv", ["git", "merge", "-n", "feature"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_GitMerge_LongNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "merge", "--no-verify", "feature"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "merge", "feature" }, result.Command);
    }

    [Fact]
    public void Argv_SortN_Unchanged()
    {
        var payload = Payload("argv", ["sort", "-n", "file"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_GrepN_Unchanged()
    {
        var payload = Payload("argv", ["grep", "-n", "foo"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_HeadN_Unchanged()
    {
        var payload = Payload("argv", ["head", "-n", "5", "f"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_EchoN_Unchanged()
    {
        var payload = Payload("argv", ["echo", "-n", "hi"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_LsLa_Unchanged()
    {
        var payload = Payload("argv", ["ls", "-la"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_CombinedShortFlags_Nm_StrippedN_KeepsM()
    {
        var payload = Payload("argv", ["git", "commit", "-nm", "msg"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "msg" }, result.Command);
    }

    [Fact]
    public void Argv_CombinedShortFlags_Nm_OnlyFlag_StrippedN_BecomesEmpty()
    {
        var payload = Payload("argv", ["git", "commit", "-n"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit" }, result.Command);
    }

    [Fact]
    public void Argv_CombinedShortFlags_NAndSeparateM_StrippedN_KeepsM()
    {
        var payload = Payload("argv", ["git", "commit", "-n", "-m", "msg"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "msg" }, result.Command);
    }

    [Fact]
    public void Argv_CombinedShortFlags_Nmf_StrippedN_KeepsMf()
    {
        var payload = Payload("argv", ["git", "commit", "-nmf", "msg"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-mf", "msg" }, result.Command);
    }

    [Fact]
    public void Argv_CombinedShortFlags_OnlyN_Removed()
    {
        var payload = Payload("argv", ["git", "commit", "-n"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit" }, result.Command);
    }

    [Fact]
    public void Argv_CombinedShortFlags_NmWithMessage_KeepsMAndMessage()
    {
        var payload = Payload("argv", ["git", "commit", "-nm", "hello world"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "hello world" }, result.Command);
    }

    [Fact]
    public void Argv_MultipleGitOptionsBeforeCommit_StripsN()
    {
        var payload = Payload("argv",
            ["git", "-C", "/tmp", "-c", "a=b", "--git-dir=/g", "commit", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(
            new[] { "git", "-C", "/tmp", "-c", "a=b", "--git-dir=/g", "commit", "-m", "x" },
            result.Command);
    }

    [Fact]
    public void Argv_NoVerifyBeforeSubcommand_Stripped()
    {
        var payload = Payload("argv", ["git", "--no-verify", "commit", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_ShortNBeforeSubcommand_Kept()
    {
        var payload = Payload("argv", ["git", "-n", "commit", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Argv_NoVerifyAndShortNBoth_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "--no-verify", "-n", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("argv", result.Mode);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_NoVerifyWithEqualsSign_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "--no-verify", "-m", "x"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Argv_NonGitCommandWithNoVerifyInArgs_Unchanged()
    {
        var payload = Payload("argv", ["some-tool", "--no-verify", "run"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }
}
