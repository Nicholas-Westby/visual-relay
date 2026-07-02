using System.Text.Json;

namespace VisualRelay.Core.Configuration;

public static partial class RelayConfigLoader
{
    /// <summary>
    /// Parses the <c>tierModelOverrides</c> JSON object, validating each
    /// entry's model name against <c>BackendConfigGenerator.SelectableModelsByTier</c>.
    /// Invalid entries are silently dropped. Returns null when the element is
    /// missing, empty, or all entries are invalid.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? TryParseTierModelOverrides(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string>();
        foreach (var prop in element.EnumerateObject())
        {
            var tier = prop.Name;
            var model = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(model))
                continue;

            // Validate against the tier's selectable models.
            if (!BackendConfigGenerator.SelectableModelsByTier.TryGetValue(tier, out var selectable))
                continue;
            if (!selectable.Contains(model, StringComparer.Ordinal))
                continue;

            result[tier] = model;
        }

        return result.Count > 0 ? result : null;
    }
}
