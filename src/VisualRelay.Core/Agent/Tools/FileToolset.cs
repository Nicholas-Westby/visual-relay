namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The file and inspection half of the tool set: everything that reads or
/// changes the working tree, and nothing that runs a process.
///
/// It registers into a <see cref="ToolCatalog"/> rather than owning one, so the
/// command tools register themselves the same way and neither half has to know
/// the other exists.
/// </summary>
public static class FileToolset
{
    /// <summary>
    /// Builds one instance of each tool. They hold no per-run state — everything
    /// they need arrives in the <see cref="ToolContext"/> — so a set can be
    /// shared across stages.
    /// </summary>
    /// <returns>The nine tools, in the order the model should read them.</returns>
    public static IReadOnlyList<IAgentTool> Create() =>
    [
        new ReadFileTool(),
        new ReadMultipleFilesTool(),
        new ListFilesTool(),
        new GrepTool(),
        new OutlineTool(),
        new ViewImageTool(),
        new WriteFileTool(),
        new EditFileTool(),
        new DeleteFileTool(),
    ];

    /// <summary>Registers the file and inspection tools into a catalog.</summary>
    /// <param name="catalog">The catalog to add to.</param>
    /// <returns>The same catalog, for chaining.</returns>
    public static ToolCatalog RegisterInto(ToolCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.AddRange(Create());
    }
}
