using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Builds the JSON Schema fragments a <see cref="ToolDefinition"/> advertises.
/// The schema is the model's only documentation for an argument, so every
/// property here carries a description rather than a bare type.
/// </summary>
internal static class ToolSchema
{
    /// <summary>Builds the top-level arguments object.</summary>
    /// <param name="properties">The declared properties.</param>
    /// <param name="required">Names of the properties that must be supplied.</param>
    /// <returns>The schema node.</returns>
    internal static JsonNode Object(JsonObject properties, params string[] required)
    {
        var requiredNames = new JsonArray();
        foreach (var name in required) requiredNames.Add(name);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = requiredNames,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>A string property.</summary>
    /// <param name="description">What the model should put in it.</param>
    /// <returns>The property schema.</returns>
    internal static JsonObject Text(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    /// <summary>An integer property.</summary>
    /// <param name="description">What the model should put in it.</param>
    /// <returns>The property schema.</returns>
    internal static JsonObject Integer(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    /// <summary>A boolean property.</summary>
    /// <param name="description">What the model should put in it.</param>
    /// <returns>The property schema.</returns>
    internal static JsonObject Flag(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    /// <summary>An array-of-strings property.</summary>
    /// <param name="description">What the model should put in it.</param>
    /// <returns>The property schema.</returns>
    internal static JsonObject TextArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
    };
}
