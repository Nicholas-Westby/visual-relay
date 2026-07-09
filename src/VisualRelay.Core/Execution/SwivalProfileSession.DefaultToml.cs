using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

internal sealed partial class SwivalProfileSession
{
    // Interpolated raw string so every base_url reads from the centralized
    // ModelBackend (one source of truth). static readonly because interpolation
    // is not a compile-time constant; the generated TOML is byte-identical.
    internal static readonly string DefaultToml =
        $"""
        [profiles.frontier]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "frontier"
        max_context_tokens = 200000

        [profiles.balanced]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "balanced"
        max_context_tokens = 128000

        [profiles.cheap]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "cheap"
        max_context_tokens = 128000

        [profiles.vision]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "vision"
        max_context_tokens = 128000

        [profiles.claude]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude"
        max_context_tokens = 200000

        [profiles.opus]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude-opus-1m"
        max_context_tokens = 1000000

        [profiles.sonnet]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude-sonnet"
        max_context_tokens = 200000

        [profiles.gpt5]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "gpt-5"
        max_context_tokens = 400000

        [profiles.qwen-coder]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "hf-qwen3-coder-next"
        max_context_tokens = 256000

        [profiles.fallback]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "fallback"
        max_context_tokens = 256000

        [profiles.glm]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "glm-5.2"
        max_context_tokens = 200000

        [profiles.kimi]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "kimi-k2"
        max_context_tokens = 256000
        """;
}
