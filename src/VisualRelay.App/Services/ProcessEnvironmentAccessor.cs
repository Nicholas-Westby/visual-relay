using VisualRelay.Core.Configuration;

namespace VisualRelay.App.Services;

/// <summary>
/// Production <see cref="IEnvironmentAccessor"/> that reads the real process
/// environment. Tests inject a fake instead (see DictionaryEnvironmentAccessor).
/// </summary>
public sealed class ProcessEnvironmentAccessor : IEnvironmentAccessor
{
    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);
}
