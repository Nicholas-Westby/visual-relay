using VisualRelay.Domain;

namespace VisualRelay.DrainQueue;

/// <summary>
/// Parses and validates command-line arguments for the drain-queue tool.
/// </summary>
public static class ArgParser
{
    /// <summary>
    /// Parsed arguments: the target repo root path and an optional ordered
    /// list of task IDs.  When <see cref="TaskIds"/> is null, the caller
    /// drains every pending (non-NEEDS-REVIEW) task in repository order.
    /// </summary>
    public sealed record Args(string RootPath, IReadOnlyList<string>? TaskIds);

    /// <summary>
    /// Parses <c>args</c> into an <see cref="Args"/> record.
    /// Returns <c>null</c> for <c>Result</c> when the arguments
    /// are malformed and <c>ErrorMessage</c> describes why.
    /// </summary>
    public static (Args? Result, string? ErrorMessage) Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return (null, "usage: VisualRelay.DrainQueue <root> [taskId ...]");
        }

        var rootPath = Path.GetFullPath(args[0]);
        IReadOnlyList<string>? taskIds = args.Length > 1
            ? args.Skip(1).ToArray()
            : null;

        return (new Args(rootPath, taskIds), null);
    }

    /// <summary>
    /// Validates that every id in <paramref name="requestedIds"/> exists in
    /// <paramref name="pendingTasks"/>.  Returns <c>null</c> when all ids
    /// are valid, otherwise an error message listing the unknown ids.
    /// </summary>
    public static string? ValidateTaskIds(
        IReadOnlyList<string> requestedIds,
        IEnumerable<RelayTaskItem> pendingTasks)
    {
        if (requestedIds.Count == 0)
            return null;

        var pendingSet = new HashSet<string>(
            pendingTasks.Select(t => t.Id),
            StringComparer.Ordinal);

        var unknown = requestedIds.Where(id => !pendingSet.Contains(id)).ToList();

        if (unknown.Count == 0)
            return null;

        var joined = string.Join(", ", unknown);
        return $"unknown task id(s): {joined}";
    }
}
