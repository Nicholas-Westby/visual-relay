using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ShellScriptClassifier.IsShellScript"/> —
/// detection by extension (.sh/.bash/.zsh) or hashbang (^#!.*\bsh\b).
/// Written TDD: these must fail against absent code.
/// </summary>
public sealed class ShellScriptClassifierTests
{
    // ── Extension detection ──────────────────────────────────────────

    [Fact]
    public void ShExtension_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("tools/foo.sh", null));
    }

    [Fact]
    public void BashExtension_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("scripts/bar.bash", null));
    }

    [Fact]
    public void ZshExtension_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("bin/baz.zsh", null));
    }

    [Fact]
    public void ExtensionCheck_IsCaseInsensitive()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("tools/Foo.SH", null));
    }

    // ── Hashbang detection (extensionless files) ────────────────────

    [Fact]
    public void Extensionless_BashHashbang_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("visual-relay", "#!/usr/bin/env bash"));
    }

    [Fact]
    public void Extensionless_ShHashbang_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("some-script", "#!/bin/sh"));
    }

    [Fact]
    public void Extensionless_ZshHashbang_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("zshrc", "#!/usr/bin/env zsh"));
    }

    [Fact]
    public void Extensionless_DashHashbang_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript("dash-script", "#!/bin/dash"));
    }

    [Fact]
    public void GithooksPreCommit_ByHashbang_IsShellScript()
    {
        Assert.True(ShellScriptClassifier.IsShellScript(".githooks/pre-commit", "#!/usr/bin/env bash"));
    }

    // ── Non-shell files ─────────────────────────────────────────────

    [Fact]
    public void PyExtension_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("app.py", null));
    }

    [Fact]
    public void CsExtension_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("src/Program.cs", null));
    }

    [Fact]
    public void ReadmeNoExtensionNoHashbang_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("README", null));
    }

    [Fact]
    public void PythonHashbang_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("runner", "#!/usr/bin/env python3"));
    }

    [Fact]
    public void NullFirstLine_NoExtension_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("Makefile", null));
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Fact]
    public void FileWithShInFilenameButNotExtension_IsNotShellScript()
    {
        // "sh" appears in the filename but not as an extension.
        Assert.False(ShellScriptClassifier.IsShellScript("push.shovel.txt", null));
    }

    [Fact]
    public void EmptyFirstLine_Extensionless_IsNotShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("empty-file", ""));
    }
}
