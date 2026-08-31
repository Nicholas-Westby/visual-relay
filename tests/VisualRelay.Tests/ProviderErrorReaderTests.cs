using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="ProviderErrorReader"/> against error bodies captured from
/// the real providers on 2026-08-31. Each envelope below is the verbatim
/// response, so a provider changing shape breaks a test rather than silently
/// mis-classifying a failure.
/// </summary>
public sealed class ProviderErrorReaderTests
{
    /// <summary>
    /// Hugging Face reports an auth failure with <c>error</c> as a bare STRING,
    /// not an object. A reader that assumes an object loses the message.
    /// </summary>
    [Fact]
    public void HuggingFaceAuthFailure_ErrorIsAString()
    {
        var error = ProviderErrorReader.TryRead(401, """{"error":"Invalid username or password."}""");

        Assert.NotNull(error);
        Assert.Equal(401, error!.StatusCode);
        Assert.Equal("Invalid username or password.", error.Message);
        Assert.Equal(ProviderErrorKind.Auth, error.Kind);
        Assert.False(error.IsRetryable);
    }

    /// <summary>Hugging Face reports a bad model with <c>error</c> as an OBJECT.</summary>
    [Fact]
    public void HuggingFaceBadModel_ErrorIsAnObject()
    {
        var error = ProviderErrorReader.TryRead(400,
            """{"error":{"message":"The requested model 'not-a-real/model-name' does not exist.","type":"invalid_request_error","param":"model","code":"model_not_found"}}""");

        Assert.NotNull(error);
        Assert.Equal("model_not_found", error!.Code);
        Assert.Equal(ProviderErrorKind.ModelUnavailable, error.Kind);
        Assert.False(error.IsRetryable);
    }

    /// <summary>Z.AI sends its numeric error code as a JSON string.</summary>
    [Fact]
    public void ZaiBadModel_CodeArrivesAsAString()
    {
        var error = ProviderErrorReader.TryRead(400,
            """{"error":{"code":"1214","message":"modelCode: does not exist"}}""");

        Assert.NotNull(error);
        Assert.Equal("1214", error!.Code);
        Assert.Equal(ProviderErrorKind.ModelUnavailable, error.Kind);
    }

    /// <summary>The same code arriving as an integer reads identically.</summary>
    [Fact]
    public void CodeArrivingAsAnInteger_ReadsAsAString()
    {
        var error = ProviderErrorReader.TryRead(400,
            """{"error":{"code":1214,"message":"modelCode: does not exist"}}""");

        Assert.Equal("1214", error!.Code);
    }

    /// <summary>DeepSeek reports auth failure as an object with a masked key.</summary>
    [Fact]
    public void DeepSeekAuthFailure_IsClassifiedAsAuth()
    {
        var error = ProviderErrorReader.TryRead(401,
            """{"error":{"message":"Authentication Fails, Your api key: ****tall is invalid","type":"authentication_error","param":null,"code":"invalid_request_error"}}""");

        Assert.Equal(ProviderErrorKind.Auth, error!.Kind);
        Assert.False(error.IsRetryable);
    }

    /// <summary>
    /// Moonshot rejects <c>tool_choice: "required"</c> while thinking is enabled.
    /// It names no model, so it is a bad request rather than a dead route.
    /// </summary>
    [Fact]
    public void MoonshotIncompatibleParameter_IsABadRequest()
    {
        var error = ProviderErrorReader.TryRead(400,
            """{"error":{"message":"tool_choice 'required' is incompatible with thinking enabled","type":"invalid_request_error"}}""");

        Assert.Equal(ProviderErrorKind.BadRequest, error!.Kind);
        Assert.Null(error.Code);
        Assert.False(error.IsRetryable);
    }

    /// <summary>
    /// Z.AI can answer HTTP 200 with an error body, so a 2xx carrying an
    /// <c>error</c> key must still be read as a failure.
    /// </summary>
    [Fact]
    public void TwoHundredCarryingAnErrorKey_IsStillAnError()
    {
        var error = ProviderErrorReader.TryRead(200,
            """{"error":{"code":"1210","message":"thinking cannot be disabled"}}""");

        Assert.NotNull(error);
        Assert.Equal("1210", error!.Code);
    }

    /// <summary>
    /// The already-parsed overload agrees with the string one. The streaming
    /// fold uses it so a healthy chunk is not parsed twice on the hot path.
    /// </summary>
    [Fact]
    public void ParsedOverload_AgreesWithTheStringOverload()
    {
        const string body = """{"error":{"code":"1210","message":"thinking cannot be disabled"}}""";
        using var document = System.Text.Json.JsonDocument.Parse(body);

        var fromString = ProviderErrorReader.TryRead(200, body);
        var fromElement = ProviderErrorReader.TryRead(200, document.RootElement);

        Assert.Equal(fromString, fromElement);
        Assert.Equal("1210", fromElement!.Code);
    }

    /// <summary>
    /// A healthy content delta carries no error, and the parsed overload says so
    /// without serializing the chunk back to a string.
    /// </summary>
    [Fact]
    public void ParsedOverload_OnAHealthyDelta_ReturnsNull()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""");

        Assert.Null(ProviderErrorReader.TryRead(200, document.RootElement));
    }

    /// <summary>A genuine success reads as no error at all.</summary>
    [Fact]
    public void CleanSuccess_ReadsAsNoError()
    {
        Assert.Null(ProviderErrorReader.TryRead(200,
            """{"choices":[{"message":{"content":"hi"}}]}"""));
    }

    /// <summary>
    /// Quota exhaustion arrives as a 429 carrying Z.AI's code 1113, never a 402.
    /// Treating it as a rate limit would retry a request that cannot succeed.
    /// </summary>
    [Fact]
    public void RateLimitCarryingTheQuotaCode_IsNotRetryable()
    {
        var error = ProviderErrorReader.TryRead(429,
            """{"error":{"code":"1113","message":"balance not enough"}}""");

        Assert.Equal(ProviderErrorKind.QuotaExhausted, error!.Kind);
        Assert.False(error.IsRetryable);
    }

    /// <summary>An ordinary 429 is a genuine rate limit and is retryable.</summary>
    [Fact]
    public void PlainRateLimit_IsRetryable()
    {
        var error = ProviderErrorReader.TryRead(429,
            """{"error":{"message":"Too many requests, please slow down"}}""");

        Assert.Equal(ProviderErrorKind.RateLimit, error!.Kind);
        Assert.True(error.IsRetryable);
    }

    /// <summary>Quota wording without the code is also caught.</summary>
    [Fact]
    public void RateLimitMentioningQuota_IsNotRetryable()
    {
        var error = ProviderErrorReader.TryRead(429,
            """{"error":{"message":"You have exceeded your quota"}}""");

        Assert.Equal(ProviderErrorKind.QuotaExhausted, error!.Kind);
    }

    /// <summary>
    /// Hugging Face answers 410 when a provider drops a model. An OpenAI-shaped
    /// classifier that only knows 404 would miss it and keep retrying the route.
    /// </summary>
    [Fact]
    public void Gone_IsAModelUnavailable()
    {
        var error = ProviderErrorReader.TryRead(410, "");

        Assert.Equal(ProviderErrorKind.ModelUnavailable, error!.Kind);
        Assert.False(error.IsRetryable);
    }

    /// <summary>A 5xx is a provider fault and is retryable.</summary>
    [Fact]
    public void ServerError_IsRetryable()
    {
        var error = ProviderErrorReader.TryRead(503, "");

        Assert.Equal(ProviderErrorKind.Server, error!.Kind);
        Assert.True(error.IsRetryable);
    }

    /// <summary>
    /// A non-2xx body is not necessarily JSON: an edge proxy can answer with
    /// HTML. The reader must not throw, and must keep the body as the message.
    /// </summary>
    [Fact]
    public void NonJsonBody_IsNotAssumedToBeJson()
    {
        var error = ProviderErrorReader.TryRead(502, "<html><body>502 Bad Gateway</body></html>");

        Assert.NotNull(error);
        Assert.Contains("502 Bad Gateway", error!.Message, StringComparison.Ordinal);
        Assert.Equal(ProviderErrorKind.Server, error.Kind);
    }

    /// <summary>An empty error body still produces a usable message.</summary>
    [Fact]
    public void EmptyBody_StillDescribesTheFailure()
    {
        var error = ProviderErrorReader.TryRead(500, "");

        Assert.Contains("500", error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hugging Face passthrough failures carry no <c>error</c> key at all, only
    /// top-level text, so the reader falls back to that.
    /// </summary>
    [Fact]
    public void PassthroughWithNoErrorKey_FallsBackToTopLevelMessage()
    {
        var error = ProviderErrorReader.TryRead(500,
            """{"message":"upstream provider failed","reason":"timeout"}""");

        Assert.Equal("upstream provider failed", error!.Message);
    }
}
