using System.Text.Json;

namespace VisualRelay.Core.Llm;

/// <summary>
/// Normalizes the four different error envelopes measured on 2026-08-31 into one
/// <see cref="ProviderError"/>. Nothing here trusts the HTTP status on its own:
/// Z.AI can answer 200 with an error body, so every response is probed for an
/// <c>error</c> key regardless of how it was labelled.
/// <para>Measured shapes:</para>
/// <list type="bullet">
/// <item>Hugging Face auth failure: <c>{"error": "Invalid username or password."}</c>
/// — a bare string.</item>
/// <item>Hugging Face bad model: <c>{"error": {message, type, param, code}}</c>
/// — an object.</item>
/// <item>Z.AI bad model: <c>{"error": {"code": "1214", "message": "..."}}</c>
/// — code as a string; elsewhere it arrives as an integer.</item>
/// <item>DeepSeek and Moonshot: an object with <c>message</c> and <c>type</c>,
/// sometimes without any code at all.</item>
/// </list>
/// </summary>
public static class ProviderErrorReader
{
    /// <summary>Z.AI reports quota exhaustion as a 429 carrying this code, not a 402.</summary>
    private const string ZaiQuotaExhaustedCode = "1113";

    private const int MaxNonJsonMessage = 400;

    /// <summary>
    /// Reads an error from a response, or returns <c>null</c> when the response
    /// is a genuine success.
    /// </summary>
    /// <param name="statusCode">The HTTP status of the response.</param>
    /// <param name="body">The raw body. Not assumed to be JSON.</param>
    /// <returns>The normalized error, or <c>null</c> for a clean 2xx.</returns>
    public static ProviderError? TryRead(int statusCode, string body)
    {
        var ok = statusCode is >= 200 and < 300;
        var (code, message, sawErrorKey) = ReadEnvelope(body);

        // A clean 2xx with no error key is a success. A 2xx WITH one is not:
        // that is how Z.AI reports failure without changing the status.
        if (ok && !sawErrorKey) return null;

        if (string.IsNullOrWhiteSpace(message))
            message = string.IsNullOrWhiteSpace(body)
                ? $"provider returned HTTP {statusCode} with an empty body"
                : Truncate(body);

        return new ProviderError(statusCode, code, message, Classify(statusCode, code, message));
    }

    private static ProviderErrorKind Classify(int statusCode, string? code, string message)
    {
        if (statusCode is 401 or 403) return ProviderErrorKind.Auth;

        if (statusCode == 429)
            // 429 means both "slow down" and "out of money" on every provider, so
            // the body code is the only thing that separates them.
            return code == ZaiQuotaExhaustedCode || MentionsExhaustedQuota(message)
                ? ProviderErrorKind.QuotaExhausted
                : ProviderErrorKind.RateLimit;

        if (statusCode >= 500) return ProviderErrorKind.Server;

        // 410 is Hugging Face's way of saying a provider dropped the model; 404
        // and 400 carry the same meaning when the text names the model.
        if (statusCode is 400 or 404 or 410 && MentionsModel(code, message))
            return ProviderErrorKind.ModelUnavailable;

        if (statusCode == 410) return ProviderErrorKind.ModelUnavailable;

        return statusCode is >= 400 and < 500 ? ProviderErrorKind.BadRequest : ProviderErrorKind.Unknown;
    }

    private static bool MentionsModel(string? code, string message) =>
        (code is not null && code.Contains("model", StringComparison.OrdinalIgnoreCase))
        || message.Contains("model", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsExhaustedQuota(string message) =>
        message.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || message.Contains("insufficient balance", StringComparison.OrdinalIgnoreCase)
        || message.Contains("out of credit", StringComparison.OrdinalIgnoreCase);

    private static (string? Code, string? Message, bool SawErrorKey) ReadEnvelope(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null, false);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A non-2xx body is not necessarily JSON: an edge proxy can answer
            // with HTML. The body itself becomes the message.
            return (null, null, false);
        }

        if (root.ValueKind != JsonValueKind.Object) return (null, null, false);

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return (null, error.GetString(), true);

            if (error.ValueKind == JsonValueKind.Object)
                return (ReadCode(error), ReadText(error), true);

            // Present but null or some other kind: still an error signal.
            if (error.ValueKind != JsonValueKind.Undefined)
                return (null, ReadText(root), true);
        }

        // Some passthrough failures carry no error key at all, only top-level text.
        var fallback = ReadText(root);
        return (ReadCode(root), fallback, false);
    }

    /// <summary>Accepts a code as either a string or an integer.</summary>
    private static string? ReadCode(JsonElement element)
    {
        if (!element.TryGetProperty("code", out var code)) return null;
        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.ToString(),
            _ => null,
        };
    }

    private static string? ReadText(JsonElement element)
    {
        foreach (var name in (string[])["message", "reason", "detail"])
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string Truncate(string body) =>
        body.Length <= MaxNonJsonMessage ? body : body[..MaxNonJsonMessage] + "…";
}
