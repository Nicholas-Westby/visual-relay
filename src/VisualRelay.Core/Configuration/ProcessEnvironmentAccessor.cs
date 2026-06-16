namespace VisualRelay.Core.Configuration;

/// <summary>
/// Production <see cref="IEnvironmentAccessor"/> that reads the real process
/// environment. Tests inject a <c>DictionaryEnvironmentAccessor</c> instead,
/// passing it directly as a parameter — never through a process-global static.
/// </summary>
internal sealed class ProcessEnvironmentAccessor : IEnvironmentAccessor
{
    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);
}
