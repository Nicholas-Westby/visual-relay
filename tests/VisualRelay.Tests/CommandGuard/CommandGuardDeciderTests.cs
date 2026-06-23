using System.Text.Json;
using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Robustness — malformed / unexpected payloads → fail-open
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Robustness_EmptyJson_Allows()
    {
        var payload = JsonDocument.Parse("{}").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Robustness_MissingMode_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","tool":"run_command","command":["git","commit","-n"]}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_MissingCommand_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","tool":"run_command","mode":"argv"}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_NullCommand_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"argv","command":null}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_CommandNotArrayInArgvMode_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"argv","command":"git commit -n"}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_CommandNotStringInShellMode_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"shell","command":["git","commit","-n"]}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_UnknownMode_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"bogus","command":"git commit -n"}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_EmptyArgvCommand_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"argv","command":[]}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_EmptyShellCommand_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"shell","command":""}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_WhitespaceOnlyShellCommand_Allows()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","mode":"shell","command":"   "}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_ExtraFields_StillDecides()
    {
        var payload = JsonDocument.Parse(
            """{"phase":"before","tool":"run_command","cwd":"/tmp","mode":"argv","command":["git","commit","--no-verify","-m","x"],"timeout":30,"is_subagent":true,"extra":"ignored"}""").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Robustness_ArgvCommandWithNullElements_Allows()
    {
        var json = """{"phase":"before","mode":"argv","command":["git",null,"commit"]}""";
        var payload = JsonDocument.Parse(json).RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    [Fact]
    public void Robustness_NotJsonObject_Allows()
    {
        var payload = JsonDocument.Parse("[1,2,3]").RootElement;
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsAllow);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Edge_Argv_GitCommit_NoVerifyIsOnlyArgAfterCommit_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit" }, result.Command);
    }

    [Fact]
    public void Edge_Shell_GitCommit_NoVerifyIsOnlyArgAfterCommit_Stripped()
    {
        var payload = Payload("shell", "git commit --no-verify");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("git commit", result.Command);
    }

    [Fact]
    public void Edge_Argv_GitWithoutSubcommand_Unchanged()
    {
        var payload = Payload("argv", ["git", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Shell_GitWithoutSubcommand_Unchanged()
    {
        var payload = Payload("shell", "git --no-verify");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Argv_GitCommit_NoVerifyAtEnd_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "-m", "x", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Edge_Shell_GitCommit_NoVerifyAtEnd_Stripped()
    {
        var payload = Payload("shell", "git commit -m x --no-verify");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Edge_Argv_GitCommit_ShortNAtEnd_Stripped()
    {
        var payload = Payload("argv", ["git", "commit", "-m", "x", "-n"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, result.Command);
    }

    [Fact]
    public void Edge_Shell_GitCommit_ShortNAtEnd_Stripped()
    {
        var payload = Payload("shell", "git commit -m x -n");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal("shell", result.Mode);
        Assert.Equal("git commit -m x", result.Command);
    }

    [Fact]
    public void Edge_Argv_GitClone_ShortN_Kept()
    {
        var payload = Payload("argv", ["git", "clone", "-n", "url"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Shell_GitClone_ShortN_Kept()
    {
        var payload = Payload("shell", "git clone -n url");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Argv_GitTag_ShortN_Kept()
    {
        var payload = Payload("argv", ["git", "tag", "-n", "v1"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Shell_GitTag_ShortN_Kept()
    {
        var payload = Payload("shell", "git tag -n v1");
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Argv_GitLog_ShortN_Kept()
    {
        var payload = Payload("argv", ["git", "log", "-n", "5"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsPassThrough);
    }

    [Fact]
    public void Edge_Argv_GitLog_LongNoVerify_Stripped()
    {
        var payload = Payload("argv", ["git", "log", "--no-verify"]);
        var result = CommandGuardDecider.Decide(payload);
        Assert.True(result.IsRewritten);
        Assert.Equal(new[] { "git", "log" }, result.Command);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static JsonElement Payload(string mode, string[] command)
    {
        var json = $$"""
            {
                "phase": "before",
                "tool": "run_command",
                "cwd": "/tmp/test",
                "mode": "{{mode}}",
                "command": [{{string.Join(", ", command.Select(c => $"\"{c}\""))}}],
                "timeout": 30,
                "is_subagent": false
            }
            """;
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement Payload(string mode, string command)
    {
        var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json = $$"""
            {
                "phase": "before",
                "tool": "run_shell_command",
                "cwd": "/tmp/test",
                "mode": "{{mode}}",
                "command": "{{escaped}}",
                "timeout": 30,
                "is_subagent": false
            }
            """;
        return JsonDocument.Parse(json).RootElement;
    }
}
