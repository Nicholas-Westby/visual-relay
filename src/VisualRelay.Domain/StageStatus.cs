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

    /// <summary>How many times the replace is attempted before the update is dropped.</summary>
    private const int MoveAttempts = 4;

    /// <summary>Long enough for a reader to finish, short enough that four is bounded.</summary>
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Atomically writes the status record to disk: a temporary file, then a replace.
    /// <para>
    /// The replace is retried because it loses the same race <see cref="Read"/> does,
    /// from the other end. Measured on Windows against concurrent readers: it failed
    /// about 1% of the time (44 of 5130 at a lifelike cadence, 81 of 4830 at a brisk
    /// one) and 0 of 5018 with no readers at all, so the contention is entirely with
    /// readers. It was uncaught, so every one of those threw out of the driver.
    /// </para>
    /// <para>
    /// It is a SHORT bounded retry and deliberately not a spin. Benched at 20 attempts
    /// under a zero-delay hammer, retrying managed 33 writes in three seconds against
    /// 694 unprotected: it converts a fast failure into near-total starvation. Four
    /// attempts caps the added wait at about 30 ms.
    /// </para>
    /// <para>
    /// Four is enough for THIS contention, not for any. Verified on Windows at a
    /// plan-phase cadence, one writer against a handful of readers: 10 runs of the
    /// concurrency test, 0 writer failures, against 10 of 10 red before the retry
    /// existed. Under the zero-delay hammer four attempts WOULD start dropping updates,
    /// and that is the designed behaviour at that load rather than a fault to chase.
    /// Raise the count on measured numbers if a real workload shows residual failures;
    /// do not raise it on reasoning, because the cost of being wrong that way is
    /// starvation rather than staleness.
    /// </para>
    /// <para>
    /// A run that still cannot replace drops the update rather than failing. This file
    /// is bookkeeping; one stage of staleness until the next write beats killing the
    /// task that produced it.
    /// </para>
    /// </summary>
    public static async Task WriteAsync(string taskDirectory, IReadOnlyList<StageStatusEntry> entries, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(taskDirectory, "status.json");
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(
            tmp,
            JsonSerializer.Serialize(entries, Options),
            cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                await Task.Delay(MoveRetryDelay, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                return;
            }
        }
    }

    /// <summary>
    /// Reads the status record from disk. Returns an empty list when the file is
    /// missing or unreadable, as the name says and as every caller relies on.
    /// <para>
    /// The sharing flags are the point, and they are a Windows correctness matter.
    /// <see cref="WriteAsync"/> replaces this file, and a replace cannot rename over a
    /// destination that someone holds open unless that handle permits deletion.
    /// <c>File.ReadAllText</c> asks for <c>FileShare.Read</c>, which does not, so the
    /// two cannot coexist and one of them is refused.
    /// </para>
    /// <para>
    /// It is the DELETE bit that earns this, and it was worth measuring rather than
    /// reasoning about. Benched on Windows, four readers against one replacing writer,
    /// failures at a lifelike cadence: <c>Read</c> 66, <c>ReadWrite</c> 79,
    /// <c>Read|Delete</c> 4, <c>ReadWrite|Delete</c> 0. ReadWrite alone is no better
    /// than the baseline, so the Write bit buys nothing here; adding Delete is what
    /// collapses it. Note the bit is decisive for the READER's open and does nothing
    /// for the writer's own move, which fails against every reader share mode alike —
    /// same bit, opposite sides of one race, and easy to collapse into one claim.
    /// </para>
    /// <para>
    /// Rates: at a lifelike cadence an unshared read failed about 10% of the time and a
    /// shared one 0 of 372; at a brisk cadence 10.8% against 0. On the real machine it
    /// presented twice, an hour and two builds apart, as a plan reading a SIBLING
    /// task's status while that task's driver replaced its own — task-06 died on
    /// task-05's file, task-09 on task-07's. POSIX permits both opens, so macOS and
    /// Linux never see it and the suite there is a poor witness.
    /// </para>
    /// <para>
    /// The catch is the backstop, and it must name
    /// <see cref="UnauthorizedAccessException"/> explicitly: that is what a refused
    /// replace actually throws, and it does NOT derive from <see cref="IOException"/>,
    /// so widening to IOException alone would still miss it. Even shared, the read
    /// failed once in 42,212 under a zero-delay hammer, so the fix is not airtight and
    /// the backstop is load-bearing rather than decorative.
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
