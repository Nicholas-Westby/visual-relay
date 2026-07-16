using System.Text.Json;
using System.Text.Json.Serialization;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

/// <summary>
/// Sidecar record written to <c>.relay/restart-handoff.json</c> when a
/// RestartBetweenTasks drain stops after a committed task. The relaunched
/// instance reads it to resume where it left off.
/// </summary>
public sealed record RestartHandoff(
    string RootPath,
    string DrainId,
    DateTimeOffset Timestamp,
    int PendingCount,
    string CommitSha,
    string[]? RelaunchCommand,
    RunAllMode Mode)
{
    private const string FileName = "restart-handoff.json";

    private static string HandoffPath(string rootPath) =>
        Path.Combine(rootPath, ".relay", FileName);

    /// <summary>
    /// Writes the handoff sidecar and returns the record. Never throws on
    /// I/O failures — a missing handoff is safer than a corrupted drain.
    /// </summary>
    public static RestartHandoff Write(
        string rootPath,
        RelayTaskOutcome outcome,
        string drainId,
        int pendingCount,
        string[]? relaunchCommand = null)
    {
        var handoff = new RestartHandoff(
            RootPath: rootPath,
            DrainId: drainId,
            Timestamp: DateTimeOffset.UtcNow,
            PendingCount: pendingCount,
            CommitSha: outcome.CommitSha ?? "unknown",
            RelaunchCommand: relaunchCommand,
            Mode: RunAllMode.RestartBetweenTasks);

        try
        {
            var dir = Path.Combine(rootPath, ".relay");
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(handoff, RestartHandoffJsonContext.Default.RestartHandoff);
            File.WriteAllText(HandoffPath(rootPath), json + Environment.NewLine);
        }
        catch { /* best-effort */ }

        return handoff;
    }

    /// <summary>
    /// Reads the handoff sidecar or returns null when absent or unreadable.
    /// </summary>
    public static RestartHandoff? Read(string rootPath)
    {
        var path = HandoffPath(rootPath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, RestartHandoffJsonContext.Default.RestartHandoff);
        }
        catch { return null; }
    }

    /// <summary>Deletes the handoff sidecar (best-effort).</summary>
    public static void Delete(string rootPath)
    {
        try { File.Delete(HandoffPath(rootPath)); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Renames the handoff to <c>.consumed</c> so it's removed from the
    /// auto-resume path but still available for post-mortem diagnosis.
    /// </summary>
    public static void MarkConsumed(string rootPath)
    {
        try
        {
            var path = HandoffPath(rootPath);
            if (File.Exists(path)) File.Move(path, path + ".consumed", overwrite: true);
        }
        catch { Delete(rootPath); }
    }

    /// <summary>
    /// True when the handoff is too old (&gt; 5 min) or the recorded root
    /// path no longer exists — discard loudly, never auto-run.
    /// </summary>
    public static bool IsStale(RestartHandoff handoff, DateTimeOffset now) =>
        (now - handoff.Timestamp).TotalMinutes > 5 ||
        !Directory.Exists(handoff.RootPath);
}

[JsonSerializable(typeof(RestartHandoff))]
internal sealed partial class RestartHandoffJsonContext : JsonSerializerContext
{
}
