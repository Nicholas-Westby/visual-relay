using System.Text.Json;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static string? ReadOptionalString(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task WritePreRunUntrackedAsync(string path, IReadOnlySet<string> paths, CancellationToken ct)
    {
        var sorted = paths.Order(StringComparer.Ordinal);
        await File.WriteAllTextAsync(
            path,
            string.Join(Environment.NewLine, sorted) + Environment.NewLine,
            ct);
    }

    private static async Task<IReadOnlySet<string>> ReadPreRunUntrackedAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);

        var lines = await File.ReadAllLinesAsync(path, ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        return set;
    }

    /// <summary>
    /// Captures a pre-run untracked snapshot. On resume reuses the persisted
    /// first-instance snapshot; on fresh runs captures current state.
    /// </summary>
    private async Task<IReadOnlySet<string>?> CapturePreRunUntrackedAsync(
        string rootPath,
        string taskDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? preRunUntracked = null;
        if (_options.CreateGitCommit)
        {
            var snapshotPath = Path.Combine(taskDirectory, "pre-run-untracked.txt");
            if (_options.Resume && File.Exists(snapshotPath))
            {
                preRunUntracked = await ReadPreRunUntrackedAsync(snapshotPath, cancellationToken);
            }
            else if (_options.Resume)
            {
                preRunUntracked = new HashSet<string>(StringComparer.Ordinal);
            }
            else
            {
                preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(rootPath, cancellationToken);
                await WritePreRunUntrackedAsync(snapshotPath, preRunUntracked, cancellationToken);
            }
        }
        return preRunUntracked;
    }
}
