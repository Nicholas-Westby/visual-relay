using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the init-time test-command guess now that it goes through the provider
/// transport seam.
/// <para>
/// Before this it POSTed to a local endpoint with an
/// <see cref="System.Net.Http.HttpClient"/> of its own — the only place left in
/// the project that called a model over HTTP by itself, and the only one with no
/// coverage whatsoever. Every case here was previously untestable offline.
/// </para>
/// </summary>
public sealed class ProviderTestCommandCompleterTests
{
    private static DictionaryEnvironmentAccessor Keys(params string[] names)
    {
        var env = new DictionaryEnvironmentAccessor();
        foreach (var name in names) env[name] = "sk-test-value";
        return env;
    }

    /// <summary>A model answer comes back as the completion text.</summary>
    [Fact]
    public async Task AModelAnswer_ComesBackAsText()
    {
        var transport = new ScriptedModelTransport().Answer("dotnet test");
        var completer = new ProviderTestCommandCompleter(
            transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"));

        Assert.Equal("dotnet test", await completer.CompleteAsync("what is the test command?"));
    }

    /// <summary>The prompt reaches the provider.</summary>
    [Fact]
    public async Task ThePrompt_ReachesTheProvider()
    {
        var transport = new ScriptedModelTransport().Answer("pytest");
        var completer = new ProviderTestCommandCompleter(
            transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"));

        await completer.CompleteAsync("Entries:\n- pyproject.toml");

        Assert.Contains("pyproject.toml", transport.Requests[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// With no provider key at all the completer asks nothing and returns empty,
    /// so init falls back to marker-based detection instead of failing. That is
    /// the path on every machine that has not set a key.
    /// </summary>
    [Fact]
    public async Task WithNoKey_ItAsksNothing()
    {
        var transport = new ScriptedModelTransport();
        var completer = new ProviderTestCommandCompleter(transport, new DictionaryEnvironmentAccessor());

        Assert.Equal(string.Empty, await completer.CompleteAsync("anything"));
        Assert.Empty(transport.Requests);
    }

    /// <summary>
    /// A provider failure yields empty rather than an exception: a guess that
    /// fails is not worth surfacing, and detection still has markers to fall
    /// back on.
    /// </summary>
    [Fact]
    public async Task AProviderFailure_YieldsEmptyRatherThanThrowing()
    {
        var transport = new ScriptedModelTransport()
            .Fails(401, """{"error":"Invalid username or password."}""");
        var completer = new ProviderTestCommandCompleter(
            transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"));

        Assert.Equal(string.Empty, await completer.CompleteAsync("anything"));
    }

    /// <summary>A buffered chat response is read down to its content.</summary>
    [Fact]
    public void ABufferedResponse_IsReadDownToItsContent()
    {
        var content = ProviderTestCommandCompleter.ReadContent(
            """{"choices":[{"message":{"role":"assistant","content":"bun test"}}]}""");

        Assert.Equal("bun test", content);
    }

    /// <summary>An unexpected response shape reads as empty, never as a throw.</summary>
    /// <param name="body">A body that is not a chat completion.</param>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{}}]}""")]
    public void AnUnexpectedShape_ReadsAsEmpty(string body)
    {
        Assert.Equal(string.Empty, ProviderTestCommandCompleter.ReadContent(body));
    }

    /// <summary>
    /// The finder with no completer injected asks nothing. It used to construct an
    /// HttpClient and POST from a default constructor, which is why nothing could
    /// test it.
    /// </summary>
    [Fact]
    public async Task TheFinderWithNoCompleter_AsksNothing()
    {
        Assert.Equal(string.Empty, await new LlmTestCommandFinder().FindAsync(RepoSetup.Root));
    }

    /// <summary>
    /// The finder still assembles the prompt and parses the answer exactly as
    /// before: those semantics were kept while the transport underneath changed.
    /// </summary>
    [Fact]
    public async Task TheFinder_KeepsItsPromptAndParsingSemantics()
    {
        string? seen = null;
        var finder = new LlmTestCommandFinder((prompt, _) =>
        {
            seen = prompt;
            return Task.FromResult("```\ndotnet test\n```");
        });

        var command = await finder.FindAsync(RepoSetup.Root);

        Assert.Contains("Entries:", seen!, StringComparison.Ordinal);
        Assert.Equal("dotnet test", command);
    }
}
