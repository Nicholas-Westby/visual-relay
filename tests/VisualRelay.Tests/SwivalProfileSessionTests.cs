using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalProfileSessionTests
{
    /// <summary>
    /// PrepareAsync must write a swival.toml containing a [profiles.fallback]
    /// block with model = "fallback", the centralized base_url, and the same
    /// max_context_tokens as qwen-coder (256000) since both resolve to the
    /// same HF Novita coder model.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_CreatesSwivalToml_WithFallbackProfile()
    {
        using var repo = TestRepository.Create();

        await using var session = await SwivalProfileSession.PrepareAsync(repo.Root, CancellationToken.None);

        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        Assert.True(File.Exists(tomlPath), "swival.toml should exist after PrepareAsync");

        var toml = await File.ReadAllTextAsync(tomlPath);

        // Extract the [profiles.fallback] section (up to the next [profiles. header).
        var fallbackSection = ExtractProfileSection(toml, "fallback");
        Assert.NotNull(fallbackSection);

        // model = "fallback" — the alias that LiteLLM resolves to the HF coder.
        Assert.Contains("model = \"fallback\"", fallbackSection);

        // base_url must use the centralized ModelBackend.BaseUrl.
        Assert.Contains($"base_url = \"{ModelBackend.BaseUrl}\"", fallbackSection);

        // max_context_tokens must match qwen-coder (256000), both resolving to
        // the same HF Novita model.
        Assert.Contains("max_context_tokens = 256000", fallbackSection);

        // provider is always "generic" for relay-managed profiles.
        Assert.Contains("provider = \"generic\"", fallbackSection);
    }

    /// <summary>
    /// When DisposeAsync completes, the swival.toml file must be removed
    /// (the session created it, so it must clean it up).
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WhenCreated_DeletesSwivalToml()
    {
        using var repo = TestRepository.Create();

        var session = await SwivalProfileSession.PrepareAsync(repo.Root, CancellationToken.None);
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        Assert.True(File.Exists(tomlPath));

        await session.DisposeAsync();

        Assert.False(File.Exists(tomlPath), "swival.toml should be deleted after disposal");
    }

    /// <summary>
    /// Extracts a TOML [profiles.{name}] section from the full file text.
    /// Returns the lines between the section header and the next [profiles.
    /// header (or end of string), or null if the section is not found.
    /// </summary>
    private static string? ExtractProfileSection(string toml, string profileName)
    {
        var header = $"[profiles.{profileName}]";
        var start = toml.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return null;

        var contentStart = start + header.Length;
        var remaining = toml[contentStart..];

        // Find the next [profiles. header or end of string.
        var nextHeader = remaining.IndexOf("\n[profiles.", StringComparison.Ordinal);
        return nextHeader >= 0 ? remaining[..nextHeader] : remaining;
    }

    /// <summary>
    /// When a swival.toml already exists (not created by this session),
    /// DisposeAsync must leave it in place.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WhenNotCreated_PreservesExistingSwivalToml()
    {
        using var repo = TestRepository.Create();

        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, "# pre-existing swival.toml");

        // PrepareAsync should detect the existing file and set created=false.
        await using var session = await SwivalProfileSession.PrepareAsync(repo.Root, CancellationToken.None);

        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Equal("# pre-existing swival.toml", content);
    }
}
