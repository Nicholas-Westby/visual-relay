namespace VisualRelay.Core.Configuration;

/// <summary>
/// Injectable accessor for process environment variables.  Production code
/// routes through <see cref="KeyEnvFile.GetEnv"/> (which delegates to this
/// interface or the real process env) so tests can inject a fake and
/// eliminate process-global mutation races.
/// </summary>
public interface IEnvironmentAccessor
{
    /// <summary>
    /// Returns the value of the environment variable <paramref name="name"/>,
    /// or <c>null</c> when it is not set.
    /// </summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>
    /// Returns all process environment variable name/value pairs.
    /// </summary>
    System.Collections.Generic.IReadOnlyDictionary<string, string> GetEnvironmentVariables();
}
