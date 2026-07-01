using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Display-normalization tests for <see cref="SandboxPathInspector"/>: the visible
/// <c>Raw</c> path must use a single <c>~</c> convention no matter which producer
/// emitted it (vr-guard literal <c>$HOME/…</c> vs group <c>~/…</c>), while
/// <c>Expanded</c> tooltips and non-home absolute paths stay untouched.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    // ── NormalizeRawForDisplay (the helper itself) ───────────────────────
    [Theory]
    [InlineData("$HOME/.cargo", "~/.cargo")]           // leading $HOME → ~
    [InlineData("${HOME}/.npm", "~/.npm")]             // braced form → ~
    [InlineData("$HOME", "~")]                          // bare $HOME → ~
    [InlineData("${HOME}", "~")]                        // bare ${HOME} → ~
    [InlineData("~/go", "~/go")]                        // already canonical
    [InlineData("/", "/")]                              // filesystem root
    [InlineData("/usr/local/go", "/usr/local/go")]      // non-home absolute
    [InlineData("$TMPDIR", "$TMPDIR")]                  // unrelated env var
    [InlineData("$XDG_CACHE_HOME/NuGet", "$XDG_CACHE_HOME/NuGet")] // not $HOME
    [InlineData("$HOMEBREW/bin", "$HOMEBREW/bin")]      // $HOME not a full segment
    [InlineData("${HOME}x", "${HOME}x")]                // braced token, not a full segment
    [InlineData("", "")]                                // empty stays empty
    public void NormalizeRawForDisplay_RewritesOnlyLeadingHomeToken(string raw, string expected)
    {
        Assert.Equal(expected, SandboxPathInspector.NormalizeRawForDisplay(raw));
    }

    // ── vr-guard producer ($HOME literal) → ~ in the displayed Raw ───────
    [Fact]
    public void ParseOwnDirectives_NormalizesHomePrefixInRawToTilde()
    {
        const string json =
            """
            { "filesystem": {
                "read": ["/", "$HOME/.gitconfig"],
                "allow": ["$HOME/.cargo"] } }
            """;

        var entries = SandboxPathInspector.ParseOwnDirectives(json);

        // $HOME/… rows now render as ~/… …
        Assert.Contains(entries, e => e.Raw == "~/.gitconfig");
        Assert.Contains(entries, e => e.Raw == "~/.cargo");
        Assert.DoesNotContain(entries, e => e.Raw.StartsWith("$HOME", StringComparison.Ordinal));
        // … while the "/" root row is left exactly as-is.
        Assert.Contains(entries, e => e.Raw == "/");
    }

    // ── vr-guard Expanded tooltip stays the concrete home path ───────────
    [Fact]
    public void ParseOwnDirectives_KeepsExpandedAsConcreteHomePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        const string json = """{ "filesystem": { "allow": ["$HOME/.cargo"] } }""";

        var entry = SandboxPathInspector.ParseOwnDirectives(json).Single();

        Assert.Equal("~/.cargo", entry.Raw);
        Assert.Equal(Path.Combine(home, ".cargo"), entry.Expanded);
    }

    // ── group producer: ~ preserved AND a $HOME group entry → ~ ──────────
    [Fact]
    public void ParseGroupJson_NormalizesRawToTildeForBothConventions()
    {
        const string json =
            """
            { "allow": { "read": [
                { "raw": "~/go", "expanded": "/Users/you/go", "platform": "cross-platform" },
                { "raw": "$HOME/.rustup", "expanded": "/Users/you/.rustup", "platform": "cross-platform" },
                { "raw": "/usr/local/go", "expanded": "/usr/local/go", "platform": "cross-platform" }
            ] } }
            """;

        var entries = SandboxPathInspector.ParseGroupJson(json, "go_runtime");

        Assert.Contains(entries, e => e.Raw == "~/go");          // ~ preserved
        Assert.Contains(entries, e => e.Raw == "~/.rustup");     // $HOME → ~
        Assert.Contains(entries, e => e.Raw == "/usr/local/go"); // absolute untouched
        Assert.DoesNotContain(entries, e => e.Raw.StartsWith("$HOME", StringComparison.Ordinal));
    }

    // ── group deny.access rows are normalized too (Blocked list) ─────────
    [Fact]
    public void ParseGroupJson_NormalizesRawInDenyAccessEntries()
    {
        const string json =
            """
            { "deny": { "access": ["$HOME/.ssh", "/etc/secret"] } }
            """;

        var blocked = SandboxPathInspector.ParseGroupJson(json, "go_runtime");

        Assert.Contains(blocked, e => e.Raw == "~/.ssh" && e.Access == SandboxAccess.Blocked);
        Assert.Contains(blocked, e => e.Raw == "/etc/secret" && e.Access == SandboxAccess.Blocked);
        Assert.DoesNotContain(blocked, e => e.Raw.StartsWith("$HOME", StringComparison.Ordinal));
    }

    // ── group Expanded tooltip preserved verbatim when Raw is normalized ─
    [Fact]
    public void ParseGroupJson_KeepsExpandedUnchangedWhenNormalizingRaw()
    {
        const string json =
            """
            { "allow": { "read": [
                { "raw": "$HOME/.rustup", "expanded": "/Users/you/.rustup", "platform": "cross-platform" }
            ] } }
            """;

        var entry = SandboxPathInspector.ParseGroupJson(json, "rust_runtime").Single();

        Assert.Equal("~/.rustup", entry.Raw);
        Assert.Equal("/Users/you/.rustup", entry.Expanded); // tooltip untouched
    }
}
