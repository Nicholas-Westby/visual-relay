namespace VisualRelay.Core.Execution;

public static partial class SandboxPathInspector
{
    // Leading home tokens some producers emit; all are collapsed to "~".
    private static readonly string[] HomeTokens = ["$HOME", "${HOME}"];

    /// <summary>
    /// Normalizes a path for DISPLAY only: rewrites a leading <c>$HOME</c> or
    /// <c>${HOME}</c> segment to <c>~</c> so every producer (vr-guard literals vs
    /// nono group output) shows one convention. A leading <c>~</c> is already
    /// canonical and left as-is; absolute and non-home paths (<c>/</c>,
    /// <c>/usr/local/go</c>, <c>$TMPDIR</c>, <c>$XDG_CACHE_HOME</c>, …) are returned
    /// unchanged. Cosmetic only — never used to resolve sandbox access.
    /// </summary>
    internal static string NormalizeRawForDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        foreach (var token in HomeTokens)
        {
            if (!raw.StartsWith(token, StringComparison.Ordinal)) continue;
            // Only rewrite when the token is a whole leading segment, so
            // "$HOMEBREW" / "${HOME}_x" style paths are left untouched.
            if (raw.Length == token.Length) return "~";
            if (raw[token.Length] == '/') return string.Concat("~", raw.AsSpan(token.Length));
        }
        return raw;
    }
}
