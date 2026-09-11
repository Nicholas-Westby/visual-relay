namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The environment every wsl.exe child gets. <c>WSL_UTF8=1</c> makes wsl.exe's own
/// output (the distro listing, its error text) UTF-8 instead of UTF-16, so it can
/// be captured through the same UTF-8 decoding as the Linux command's bytes;
/// <c>WSL_DISABLE_WARNINGS=1</c> keeps its advisory lines (proxy detection and the
/// like) out of the captured output.
/// </summary>
public static class WslExeEnvironment
{
    public const string Utf8 = "WSL_UTF8";
    public const string DisableWarnings = "WSL_DISABLE_WARNINGS";

    public static IReadOnlyDictionary<string, string> Variables { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { [Utf8] = "1", [DisableWarnings] = "1" };
}
