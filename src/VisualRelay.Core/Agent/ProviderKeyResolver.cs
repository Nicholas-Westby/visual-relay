using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Finds provider keys the same way the rest of the app does: the process
/// environment first, then the user-level dotenv the key panel writes.
/// <para>
/// Reading only the process environment is not enough, and the failure is
/// silent in the worst way: every tier resolves to nothing and a stage reports
/// "no model is available" on a machine whose keys are perfectly well set, just
/// in a file. That is exactly what happened the first time the loop was pointed
/// at a real run.
/// </para>
/// </summary>
/// <param name="environment">The process environment seam.</param>
public sealed class ProviderKeyResolver(IEnvironmentAccessor environment)
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _fileKeys = new(() =>
    {
        try
        {
            var path = KeyEnvFile.ResolvePathForCurrentUser(environment);
            return File.Exists(path) ? KeyEnvFile.Read(path) : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No home directory, or the file is unreadable. The process
            // environment may still carry keys, so this is not fatal.
            return new Dictionary<string, string>();
        }
    });

    /// <summary>
    /// The value for a provider key, or <c>null</c> when it is set nowhere.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    public string? Resolve(string name)
    {
        // Process env wins, matching the documented precedence: an exported key
        // overrides the file.
        var fromEnvironment = environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

        return _fileKeys.Value.TryGetValue(name, out var fromFile)
            && !string.IsNullOrWhiteSpace(fromFile)
                ? fromFile
                : null;
    }

    /// <summary>Every provider key that is set, from either source.</summary>
    /// <returns>The names of the keys that resolve.</returns>
    public HashSet<string> PresentKeys() =>
        [.. ModelCatalog.ProviderKeyNames.Where(name => Resolve(name) is not null)];
}
