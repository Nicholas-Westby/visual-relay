using System.Text.Json;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// Persists the user's manual task order per repo at
/// <c>.relay/task-order.json</c> (a plain ordered list of task ids) and applies
/// it to a freshly-listed queue. This is the single home for the ordering rule:
/// both the visible queue (the app's reload path) and the drain
/// (<c>RelayQueueController.RefreshAsync</c>) order through <see cref="Apply{T}"/>,
/// so the displayed order and the run order can never drift.
///
/// The file lives under <c>.relay/</c> (VR's local state dir, git-ignored by the
/// blanket rule in <c>RelayGitignoreWriter</c>) — manual order is per-machine
/// local state, not repo history.
/// </summary>
public sealed class TaskOrderStore(string rootPath)
{
    private string OrderFilePath => Path.Combine(rootPath, ".relay", "task-order.json");

    /// <summary>
    /// Reads the persisted id order. Returns an empty list when the file is
    /// missing, empty, or corrupt — never throws, so a damaged file degrades to
    /// the alphabetical fallback rather than breaking the queue.
    /// </summary>
    public IReadOnlyList<string> Read()
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return [];
        }

        var path = OrderFilePath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return ids?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Persists the given ids as the new manual order, overwriting any previous
    /// order. Always writes the full current id list, so removed/renamed tasks
    /// drop out and never linger as stale ranks. Best-effort: a write fault never
    /// throws into the caller (a failed persist must not crash a drag gesture).
    /// </summary>
    public void Save(IEnumerable<string> orderedIds)
    {
        // No project selected → nowhere to persist; never touch the working dir.
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        try
        {
            var relayDir = Path.Combine(rootPath, ".relay");
            Directory.CreateDirectory(relayDir);
            File.WriteAllText(
                OrderFilePath,
                JsonSerializer.Serialize(orderedIds.ToArray(), SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting the manual order is best-effort; the in-memory order
            // still reflects the user's intent for this session.
        }
    }

    /// <summary>
    /// Orders <paramref name="items"/> by the persisted rank first, falling back
    /// to alphabetical (ordinal-ignore-case) for ids with no saved rank — new
    /// tasks land after the ranked ones, alphabetically among themselves. Stable
    /// for tasks that share a rank bucket. Reads the persisted order each call so
    /// callers always see the latest saved order.
    /// </summary>
    public IReadOnlyList<T> Apply<T>(IEnumerable<T> items, Func<T, string> idSelector) =>
        ApplyOrder(items, idSelector, Read());

    private static IReadOnlyList<T> ApplyOrder<T>(
        IEnumerable<T> items,
        Func<T, string> idSelector,
        IReadOnlyList<string> savedOrder)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < savedOrder.Count; i++)
        {
            rank.TryAdd(savedOrder[i], i);
        }

        return items
            // Unranked ids sort after ranked ones (int.MaxValue), then alphabetically.
            .OrderBy(item => rank.GetValueOrDefault(idSelector(item), int.MaxValue))
            .ThenBy(idSelector, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
