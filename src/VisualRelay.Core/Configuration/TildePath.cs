namespace VisualRelay.Core.Configuration;

/// <summary>Expands a leading "~/" to the user's home directory. All other
/// forms (absolute, relative, bare "~", "~user/…") pass through verbatim.</summary>
public static class TildePath
{
    public static string Expand(string path) =>
        Expand(path, Environment.GetEnvironmentVariable("HOME")
            is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Expand(string path, string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return path;
        return path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
    }
}
