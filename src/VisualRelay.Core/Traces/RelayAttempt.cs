using System.Text.RegularExpressions;

namespace VisualRelay.Core.Traces;

/// <summary>
/// Single source of truth for parsing the per-stage attempt index out of the
/// <c>stage{n}-attempt{k}</c> trace directories and <c>stage{n}-attempt{k}.report.json</c>
/// report files that each stage run produces.
/// </summary>
public static partial class RelayAttempt
{
    /// <summary>
    /// Parses the stage and attempt numbers from a <c>stage{n}-attempt{k}</c> directory or
    /// report file name. Returns <c>false</c> when the name does not match.
    /// </summary>
    public static bool TryParse(string name, out int stage, out int attempt)
    {
        stage = 0;
        attempt = 0;
        var match = AttemptRegex().Match(name);
        if (!match.Success)
        {
            return false;
        }

        stage = int.Parse(match.Groups[1].Value);
        attempt = int.Parse(match.Groups[2].Value);
        return true;
    }

    /// <summary>Attempt index for a name, or 0 when it does not match (sorts before any real attempt).</summary>
    public static int AttemptNumber(string name) => TryParse(name, out _, out var attempt) ? attempt : 0;

    /// <summary>Stage index for a name, or null when it does not match.</summary>
    public static int? StageNumber(string name) => TryParse(name, out var stage, out _) ? stage : null;

    /// <summary>
    /// Allocates the next attempt index for a stage by scanning <paramref name="taskDirectory"/> for
    /// existing <c>stage{n}-attempt{k}</c> trace dirs and report files. First run -> 1, each re-run ->
    /// max(k)+1, so re-running never overwrites a prior attempt's report or merges its trace sessions.
    /// </summary>
    public static int Next(string taskDirectory, int stageNumber)
    {
        if (!Directory.Exists(taskDirectory))
        {
            return 1;
        }

        var highest = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(taskDirectory, $"stage{stageNumber}-attempt*"))
        {
            if (TryParse(Path.GetFileName(path), out var stage, out var attempt) &&
                stage == stageNumber && attempt > highest)
            {
                highest = attempt;
            }
        }

        return highest + 1;
    }

    [GeneratedRegex(@"stage(\d+)-attempt(\d+)")]
    private static partial Regex AttemptRegex();
}
