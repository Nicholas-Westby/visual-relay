using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="NonoProfileEnsurer"/> — VR owning + self-healing the
/// nono vr-guard profile at <c>$XDG_CONFIG_HOME/visual-relay/vr-guard.json</c>.
/// These FAIL against the pre-change code (the type does not exist; the profile
/// was installed only-if-absent under the global nono profiles dir).
/// </summary>
public sealed class NonoProfileEnsurerTests
{
    // ── Path resolution through the injected accessor ────────────────────

    [Fact]
    public void ResolveProfilePath_UsesXdgConfigHome_WhenSet()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["XDG_CONFIG_HOME"] = "/xdg/conf",
            ["HOME"] = "/home/ignored"
        };

        var path = NonoProfileEnsurer.ResolveProfilePath(env);

        Assert.Equal(Path.Combine("/xdg/conf", "visual-relay", "vr-guard.json"), path);
        Assert.True(Path.IsPathRooted(path), "profile path must be absolute");
    }

    [Fact]
    public void ResolveProfilePath_FallsBackToHomeDotConfig_WhenXdgUnset()
    {
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = "/home/alice" };

        var path = NonoProfileEnsurer.ResolveProfilePath(env);

        Assert.Equal(
            Path.Combine("/home/alice", ".config", "visual-relay", "vr-guard.json"),
            path);
    }

    [Fact]
    public void ResolveProfilePath_LandsBesideTheDotEnv()
    {
        // VR's .env and the owned profile must share the same visual-relay dir.
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = "/c" };

        var profile = NonoProfileEnsurer.ResolveProfilePath(env);
        var dotEnvDir = Path.Combine(XdgConfig.ResolveConfigDir(env), "visual-relay");

        Assert.Equal(dotEnvDir, Path.GetDirectoryName(profile));
    }

    // ── EnsureAsync: write → exists → content parity ─────────────────────

    [Fact]
    public async Task EnsureAsync_AbsentFile_CreatesDirAndWritesEmbeddedContent()
    {
        using var tmp = new TempXdg();

        var written = await NonoProfileEnsurer.EnsureAsync(tmp.Env);

        Assert.Equal(tmp.ExpectedProfilePath, written);
        Assert.True(File.Exists(written), "profile must be created");
        Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(written));
        // The created profile is valid JSON that extends swival (sanity).
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(written));
        Assert.Equal("swival", doc.RootElement.GetProperty("extends").GetString());
    }

    [Fact]
    public async Task EnsureAsync_StalePreSeededFile_IsOverwrittenToMatchEmbedded()
    {
        using var tmp = new TempXdg();
        Directory.CreateDirectory(Path.GetDirectoryName(tmp.ExpectedProfilePath)!);
        await File.WriteAllTextAsync(tmp.ExpectedProfilePath,
            "{ \"extends\": \"swival\", \"stale\": true }");

        var written = await NonoProfileEnsurer.EnsureAsync(tmp.Env);

        Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(written));
        Assert.DoesNotContain("stale", await File.ReadAllTextAsync(written));
    }

    [Fact]
    public async Task EnsureAsync_IdenticalFile_IsNotRewritten_NoMtimeChurn()
    {
        using var tmp = new TempXdg();
        var first = await NonoProfileEnsurer.EnsureAsync(tmp.Env);
        var stampBefore = File.GetLastWriteTimeUtc(first);
        File.SetLastWriteTimeUtc(first, stampBefore.AddDays(-1)); // age it
        var aged = File.GetLastWriteTimeUtc(first);

        var second = await NonoProfileEnsurer.EnsureAsync(tmp.Env);

        Assert.Equal(first, second);
        // Bytes already match → no write → mtime unchanged from the aged value.
        Assert.Equal(aged, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public async Task EnsureAsync_WriteFails_ThrowsActionableError_NotSilent()
    {
        // A directory that cannot be created (parent is an existing FILE) makes
        // the write fail. EnsureAsync must surface an actionable error rather than
        // silently let the run proceed to a sandboxed stage with no profile.
        using var tmp = new TempXdg();
        var blocker = Path.Combine(Path.GetTempPath(), "vr-nono-blocker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "not a directory");
        try
        {
            // XDG points INTO the regular file → the visual-relay subdir can't be made.
            tmp.Env["XDG_CONFIG_HOME"] = blocker;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NonoProfileEnsurer.EnsureAsync(tmp.Env));
            Assert.Contains("vr-guard", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    // ── Embedded == on-disk packaging/nono/vr-guard.json (drift guard) ───

    [Fact]
    public void EmbeddedContent_EqualsRepoPackagingFile_ByteForByte()
    {
        // The single-source-of-truth guard: the embedded profile the runtime
        // writes must equal the on-disk file NonoProfileStructureTests /
        // VrGuardProfileRollbackTests validate, so they can never drift apart.
        var onDisk = File.ReadAllText(
            Path.Combine(RepoSetup.Root, "packaging", "nono", "vr-guard.json"));

        Assert.Equal(onDisk, NonoProfileEnsurer.EmbeddedContent);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class TempXdg : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "vr-nono-ensurer", Guid.NewGuid().ToString("N"));

        public DictionaryEnvironmentAccessor Env { get; }

        public string ExpectedProfilePath =>
            Path.Combine(_root, "visual-relay", "vr-guard.json");

        public TempXdg()
        {
            Env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = _root };
        }

        public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_root);
    }
}
