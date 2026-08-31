namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The command half of the tool set: everything that reaches a process, plus the two
/// that reach nothing at all. It registers into a <see cref="ToolCatalog"/> rather
/// than owning one, so it composes with the file and inspection half without either
/// knowing the other exists.
/// </summary>
public static class CommandToolset
{
    /// <summary>
    /// Builds one instance of each command-family tool over a shared
    /// <see cref="SandboxedCommandExecutor"/>, so every one of them reaches a
    /// process through the same sandbox, the same in-process command guard and the
    /// same timeout policy.
    /// </summary>
    /// <param name="executor">The shared sandboxed-command path.</param>
    /// <returns>The five tools, in the order the model should read them.</returns>
    public static IReadOnlyList<IAgentTool> Create(SandboxedCommandExecutor executor) =>
    [
        new RunCommandTool(executor),
        new RunShellCommandTool(executor),
        new SnapshotTool(executor),
        new ThinkTool(),

        // The one tool that carries state across calls, so a run gets its own.
        new TodoTool(),
    ];

    /// <summary>Registers the command tools into a catalog.</summary>
    /// <param name="catalog">The catalog to add to.</param>
    /// <param name="executor">The shared sandboxed-command path.</param>
    /// <returns>The same catalog, for chaining.</returns>
    public static ToolCatalog RegisterInto(ToolCatalog catalog, SandboxedCommandExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.AddRange(Create(executor));
    }
}
