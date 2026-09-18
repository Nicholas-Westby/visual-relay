using System.Text.Json;

namespace VisualRelay.Domain;

/// <summary>
/// A single entry in the per-stage status record.
/// Written by the driver at each lifecycle point and read by the UI as the
/// single source of truth for stage status. It is working-tree bookkeeping under
/// the target's .relay directory, never staged into a commit.
/// </summary>
public sealed record StageStatusEntry(
    int Stage,
    string Name,
    string Status,
    string? Check = null,
    double? DurationSeconds = null,
    double? CostUsd = null,
    int? Turns = null,
    string? Model = null,
    string? Error = null,
    string? TaskInputHash = null,
    double? TestDurationSeconds = null,
    // Why a check reads the way it does, when the check alone does not say: the
    // Author-tests gate records "unproven" here with the reason it could prove
    // nothing. Null whenever the check speaks for itself.
    string? Reason = null);

/// <summary>
/// Serializer / deserializer for the per-stage status record.
/// </summary>
public static class StageStatusRecord
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Atomically writes the status record to disk.
    /// </summary>
    public static async Task WriteAsync(string taskDirectory, IReadOnlyList<StageStatusEntry> entries, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(taskDirectory, "status.json");
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(
            tmp,
            JsonSerializer.Serialize(entries, Options),
            cancellationToken);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the status record from disk. Returns an empty list when the file is
    /// missing or unreadable, as the name says and as every caller relies on.
    /// <para>
    /// The sharing flags are the point, and they are a Windows correctness matter.
    /// <see cref="WriteAsync"/> replaces this file with a move, and a move cannot
    /// replace a destination that someone holds open without <c>FileShare.Delete</c>;
    /// equally a reader cannot open a file being replaced unless it tolerates the
    /// writer. <c>File.ReadAllText</c> asks for neither, so on Windows a plan that read
    /// a SIBLING task's status while that task's driver was writing its own threw
    /// ERROR_SHARING_VIOLATION and failed the planning. Measured twice on real Windows,
    /// an hour and two builds apart: task-06 died on task-05's status.json, task-09 on
    /// task-07's. POSIX permits both opens, so macOS and Linux never see it and the
    /// suite there is a poor witness.
    /// </para>
    /// <para>
    /// The catch is broadened for the same reason: it claimed to handle "unreadable"
    /// while only catching malformed JSON, so an IO error propagated out of a method
    /// documented not to fail and became a task's flagged reason.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StageStatusEntry> Read(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, "status.json");
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<IReadOnlyList<StageStatusEntry>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
