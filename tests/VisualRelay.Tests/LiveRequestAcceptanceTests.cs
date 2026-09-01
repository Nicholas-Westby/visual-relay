using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// The live half of the request goldens: every goldened body is POSTed to the
/// provider that will actually serve it, and must not be rejected.
/// <para>
/// This is the assertion the goldens cannot make on their own. The layer this
/// replaced ran with <c>drop_params: true</c>, silently stripping parameters
/// providers reject — so a body can be goldened, reviewed and committed while
/// being one no provider will take. A golden without a passing live run is a
/// record of a guess.
/// </para>
/// <para>
/// Opt-in via <c>VR_RUN_LIVE_GOLDENS=1</c> because it spends money and needs
/// keys. Each request is abandoned after its first chunk, so it pays for a
/// handful of tokens rather than a full generation.
/// </para>
/// </summary>
public sealed class LiveRequestAcceptanceTests
{
    private const string EnvVar = "VR_RUN_LIVE_GOLDENS";

    private static void SkipUnlessOptedIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal))
            Assert.Skip($"{EnvVar}=1 required: this suite calls the real providers and costs money.");
    }

    /// <summary>Every goldened body, with the route that will serve it.</summary>
    /// <returns>One row per golden.</returns>
    public static TheoryData<string, string> Goldens()
    {
        var data = new TheoryData<string, string>();
        foreach (var (model, stage, _) in RequestGolden.All()) data.Add(model, stage);
        return data;
    }

    /// <summary>
    /// The named provider accepts the goldened body. A 4xx that names a
    /// parameter is the failure this exists to catch.
    /// </summary>
    /// <param name="alias">The catalog alias the golden belongs to.</param>
    /// <param name="stage">The goldened stage shape.</param>
    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task TheProvider_AcceptsTheGoldenedBody(string alias, string stage)
    {
        SkipUnlessOptedIn();

        var route = ProviderRoutes.For(alias);
        Assert.NotNull(route);

        // Read the key from the PROCESS environment only, never the user's key
        // file: the test assembly redirects XDG_CONFIG_HOME to a temp directory
        // precisely so no test can reach it. A live run is a deliberate act, so
        // the operator exports the keys for it.
        var apiKey = Environment.GetEnvironmentVariable(route!.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
            Assert.Skip(
                $"{route.ApiKeyEnvVar} is not exported; cannot prove {alias} accepts its body.");

        var body = File.ReadAllText(RequestGolden.PathFor(alias, stage));
        using var transport = LiveProviderTransport.CreateDefault();

        await using var response = await transport.StreamAsync(
            new ProviderRequest(HttpMethod.Post, route.Endpoint, route.Headers(apiKey!), body),
            TestContext.Current.CancellationToken);

        // Read only enough to know the request was taken, then abandon it.
        var firstChunk = string.Empty;
        await foreach (var chunk in response.ReadChunksAsync(TestContext.Current.CancellationToken))
        {
            firstChunk = System.Text.Encoding.UTF8.GetString(chunk.Span);
            break;
        }

        Assert.True(response.StatusCode is >= 200 and < 300,
            $"{alias}/{stage}: {route.ProviderName} answered {response.StatusCode} to its own "
            + $"goldened body. First bytes: {Trim(firstChunk)}");

        // A 200 carrying an error body is how one provider signals a rejected
        // parameter, so the status alone is not enough.
        var error = ProviderErrorReader.TryRead(response.StatusCode, firstChunk);
        Assert.True(error is null,
            $"{alias}/{stage}: {route.ProviderName} took the request and then rejected it: "
            + error?.Message);
    }

    private static string Trim(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}
