using System.Runtime.InteropServices;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

[Collection("Environment")]
public sealed class KeyEnvFileTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    public KeyEnvFileTests()
    {
        KeyEnvFile.EnvironmentAccessorOverride = _env;
    }

    public void Dispose()
    {
        KeyEnvFile.EnvironmentAccessorOverride = null;
        _env.Clear();
    }
    // ── Path resolution ────────────────────────────────────────────────

    [Fact]
    public void ResolvePath_WithXdgConfigHome_UsesXdgConfigHome()
    {
        var xdg = "/custom/xdg/config";
        var path = KeyEnvFile.ResolvePath(xdg, "/home/user");
        Assert.StartsWith(xdg + "/visual-relay/.env", path, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_WithoutXdgConfigHome_FallsBackToHomeDotConfig()
    {
        var home = "/home/user";
        var path = KeyEnvFile.ResolvePath(xdgConfigHome: null, home);
        Assert.StartsWith(home + "/.config/visual-relay/.env", path, StringComparison.Ordinal);
    }

    // ── Parse ──────────────────────────────────────────────────────────

    [Fact]
    public void Read_ParsesKeyEqualsValue()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "MOONSHOT_API_KEY=sk-abc\nDEEPSEEK_API_KEY=sk-def\n");

        var result = KeyEnvFile.Read(envPath);

        Assert.Equal(2, result.Count);
        Assert.Equal("sk-abc", result["MOONSHOT_API_KEY"]);
        Assert.Equal("sk-def", result["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void Read_SkipsCommentsAndBlankLines()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, """
            # header
            MOONSHOT_API_KEY=sk-abc

            # mid
              \t  
            DEEPSEEK_API_KEY=sk-def
            # footer
            """);

        var result = KeyEnvFile.Read(envPath);

        Assert.Equal(2, result.Count);
        Assert.Equal("sk-abc", result["MOONSHOT_API_KEY"]);
        Assert.Equal("sk-def", result["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void Read_TrimsWhitespaceAroundKeyAndValue()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "  MOONSHOT_API_KEY = sk-abc  \n");

        var result = KeyEnvFile.Read(envPath);

        Assert.Single(result);
        Assert.True(result.ContainsKey("MOONSHOT_API_KEY"));
        Assert.Equal("sk-abc", result["MOONSHOT_API_KEY"]);
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        var result = KeyEnvFile.Read(Path.Combine(repo.Root, "nonexistent.env"));
        Assert.Empty(result);
    }

    [Fact]
    public void Read_ValueContainsEquals_ReturnsRestOfLineAsValue()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "KEY=val=with=equals\n");

        var result = KeyEnvFile.Read(envPath);

        Assert.Equal("val=with=equals", result["KEY"]);
    }

    [Fact]
    public void Read_EmptyOrCommentOnlyFile_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "# just a comment\n# another comment\n");

        var result = KeyEnvFile.Read(envPath);
        Assert.Empty(result);

        File.WriteAllText(envPath, "");
        Assert.Empty(KeyEnvFile.Read(envPath));
    }

    // ── Upsert ─────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_AddsNewKey_PreservingExistingLinesByteForByte()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        var original = "# Keys\nMOONSHOT_API_KEY=sk-abc\nDEEPSEEK_API_KEY=sk-def\n";
        File.WriteAllText(envPath, original);

        KeyEnvFile.Upsert(envPath, "HF_TOKEN", "hf-xyz");

        var result = File.ReadAllText(envPath);
        // The original content must appear verbatim as a prefix; the new key is
        // appended. This asserts byte-for-byte preservation of unrelated lines.
        Assert.StartsWith(original, result, StringComparison.Ordinal);
        Assert.EndsWith("HF_TOKEN=hf-xyz\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_UpdatesExistingKey_PreservingUnrelatedLines()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        var original = "# header\n\nMOONSHOT_API_KEY=sk-old\nDEEPSEEK_API_KEY=sk-def\n\n# footer\n";
        File.WriteAllText(envPath, original);

        KeyEnvFile.Upsert(envPath, "MOONSHOT_API_KEY", "sk-new");

        var result = File.ReadAllText(envPath);
        // Only the target key's value changed; everything else is byte-for-byte
        // identical to the original.
        var expected = "# header\n\nMOONSHOT_API_KEY=sk-new\nDEEPSEEK_API_KEY=sk-def\n\n# footer\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Upsert_ToEmptyOrNonexistentFile_CreatesFileAndDirectoryWithKeyValue()
    {
        using var repo = TestRepository.Create();

        // Nonexistent file — must create dir and file.
        var envDir = Path.Combine(repo.Root, "visual-relay");
        var envPath = Path.Combine(envDir, ".env");
        KeyEnvFile.Upsert(envPath, "MOONSHOT_API_KEY", "sk-new");
        Assert.True(File.Exists(envPath));
        Assert.Contains("MOONSHOT_API_KEY=sk-new", File.ReadAllText(envPath), StringComparison.Ordinal);

        // Empty existing file — must add the key.
        var emptyPath = Path.Combine(repo.Root, "empty.env");
        File.WriteAllText(emptyPath, "");
        KeyEnvFile.Upsert(emptyPath, "HF_TOKEN", "hf-xyz");
        Assert.Contains("HF_TOKEN=hf-xyz", File.ReadAllText(emptyPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_CreatesDirectory0700AndFile0600()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var repo = TestRepository.Create();
        var envDir = Path.Combine(repo.Root, "new-dir");
        var envPath = Path.Combine(envDir, ".env");

        KeyEnvFile.Upsert(envPath, "KEY", "val");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(envDir));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(envPath));
    }

    [Fact]
    public void Upsert_ValueWithSpecialCharacters_IsRoundTripped()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "MOONSHOT_API_KEY=simple\n");

        var specialValue = "sk-!@#$%^&*()_+{}|:\"<>?";
        KeyEnvFile.Upsert(envPath, "SPECIAL_KEY", specialValue);

        Assert.Equal(specialValue, KeyEnvFile.Read(envPath)["SPECIAL_KEY"]);
    }

    [Fact]
    public void Upsert_PreservesOriginalLineEndings()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        var original = "# Keys\r\nMOONSHOT_API_KEY=sk-abc\r\nDEEPSEEK_API_KEY=sk-def\r\n";
        File.WriteAllText(envPath, original);

        KeyEnvFile.Upsert(envPath, "HF_TOKEN", "hf-xyz");

        var result = File.ReadAllText(envPath);
        // Original CRLF lines must remain CRLF; only the appended line uses the
        // platform default (Environment.NewLine).
        Assert.StartsWith("# Keys\r\nMOONSHOT_API_KEY=sk-abc\r\nDEEPSEEK_API_KEY=sk-def\r\n",
            result, StringComparison.Ordinal);
        Assert.Contains("HF_TOKEN=hf-xyz", result, StringComparison.Ordinal);
    }

    // ── GetUnsetKeys ────────────────────────────────────────────────────

    [Fact]
    public void GetUnsetKeys_ReturnsKeysNotInEnvironment()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "MOONSHOT_API_KEY=sk-abc\nDEEPSEEK_API_KEY=sk-def\n");

        // Neither key is set in the fake accessor — both should be returned.
        var result = KeyEnvFile.GetUnsetKeys(envPath);

        Assert.Equal(2, result.Count);
        Assert.Equal("sk-abc", result["MOONSHOT_API_KEY"]);
        Assert.Equal("sk-def", result["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void GetUnsetKeys_ExcludesKeysAlreadyInEnvironment()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");
        File.WriteAllText(envPath, "MOONSHOT_API_KEY=sk-file\nDEEPSEEK_API_KEY=sk-def\n");

        // MOONSHOT_API_KEY is set in the fake accessor — it must be excluded.
        _env["MOONSHOT_API_KEY"] = "sk-env";

        var result = KeyEnvFile.GetUnsetKeys(envPath);

        Assert.False(result.ContainsKey("MOONSHOT_API_KEY"));
        Assert.True(result.ContainsKey("DEEPSEEK_API_KEY"));
        Assert.Equal("sk-def", result["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void GetUnsetKeys_EmptyFileOrAllKeysSet_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        var envPath = Path.Combine(repo.Root, "test.env");

        // Empty file.
        File.WriteAllText(envPath, "");
        Assert.Empty(KeyEnvFile.GetUnsetKeys(envPath));

        // All keys are already set in the fake accessor — nothing to return.
        File.WriteAllText(envPath, "MOONSHOT_API_KEY=sk-file\n");
        _env["MOONSHOT_API_KEY"] = "sk-env";
        Assert.Empty(KeyEnvFile.GetUnsetKeys(envPath));
    }
}
