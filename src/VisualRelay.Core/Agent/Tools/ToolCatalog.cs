using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The set of tools one run advertises, and the lookup the loop uses to dispatch
/// a call by name.
///
/// It is additive on purpose: a catalog starts empty and each toolset registers
/// itself, so the file and inspection tools, the command tools and anything a
/// later stage adds never have to share a single hard-coded list. Registration
/// order is preserved, because it is the order the model reads the tools in.
/// </summary>
public sealed class ToolCatalog
{
    private readonly List<IAgentTool> _tools = [];
    private readonly Dictionary<string, IAgentTool> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered tool, in the order it was registered.</summary>
    public IReadOnlyList<IAgentTool> Tools => _tools;

    /// <summary>
    /// Registers one tool. A duplicate wire name is a programming error, not a
    /// runtime condition: two tools answering to one name means the model's call
    /// is ambiguous and silently resolving it would hide the mistake.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    /// <returns>This catalog, for chaining.</returns>
    /// <exception cref="ArgumentException">A tool with that name is registered.</exception>
    public ToolCatalog Add(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Definition.Name;
        if (!_byName.TryAdd(name, tool))
            throw new ArgumentException($"a tool named '{name}' is already registered", nameof(tool));

        _tools.Add(tool);
        return this;
    }

    /// <summary>Registers several tools in order.</summary>
    /// <param name="tools">The tools to register.</param>
    /// <returns>This catalog, for chaining.</returns>
    public ToolCatalog AddRange(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools) Add(tool);
        return this;
    }

    /// <summary>Finds the tool the model called.</summary>
    /// <param name="name">The wire name from the tool call.</param>
    /// <returns>The tool, or null when nothing answers to that name.</returns>
    public IAgentTool? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Renders every definition as an OpenAI-compatible <c>tools</c> entry, ready
    /// to hand to the request builder.
    /// </summary>
    /// <returns>One node per tool.</returns>
    public IReadOnlyList<JsonNode> Definitions() =>
        _tools.Select(tool => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Definition.Name,
                ["description"] = tool.Definition.Description,
                ["parameters"] = tool.Definition.ParametersSchema.DeepClone(),
            },
        }).ToList();
}
