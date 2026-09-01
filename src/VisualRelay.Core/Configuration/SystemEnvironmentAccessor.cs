namespace VisualRelay.Core.Configuration;

/// <summary>
/// Reads the real process environment. The GUI has its own equivalent in the app
/// layer; this one exists so code in Core and in the console tools can resolve a
/// provider key without either reaching for <see cref="Environment"/> directly
/// or taking a dependency on the app.
/// </summary>
public sealed class SystemEnvironmentAccessor : IEnvironmentAccessor
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);
}
