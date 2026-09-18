using System.Diagnostics;
using System.Text.Json;

namespace VisualRelay.Core.Execution;

internal sealed class ActiveTaskLock : IAsyncDisposable
{
    private readonly string _directory;
    private bool _released;

    private ActiveTaskLock(string directory, string nonce)
    {
        _directory = directory;
        Nonce = nonce;
    }

    public string Nonce { get; }

    public static async Task<ActiveTaskLock> AcquireAsync(string rootPath, string taskId, CancellationToken cancellationToken)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);
        var activeDir = Path.Combine(relayDir, "ACTIVE");
        TryReclaimStaleLock(activeDir);
        Directory.CreateDirectory(activeDir);

        var nonce = Guid.NewGuid().ToString("N");
        var info = new
        {
            task = taskId,
            pid = Environment.ProcessId,
            nonce
        };
        await File.WriteAllTextAsync(
            Path.Combine(activeDir, "info.json"),
            JsonSerializer.Serialize(info),
            cancellationToken);
        return new ActiveTaskLock(activeDir, nonce);
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    private void Release()
    {
        if (_released)
        {
            return;
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        _released = true;
    }

    private static void TryReclaimStaleLock(string activeDir)
    {
        if (!Directory.Exists(activeDir))
        {
            return;
        }

        var infoPath = Path.Combine(activeDir, "info.json");
        if (!File.Exists(infoPath))
        {
            Directory.Delete(activeDir, recursive: true);
            return;
        }

        // NOT widened to IOException, deliberately. Every catch below RECLAIMS the lock
        // by deleting it, and an unreadable info.json is not evidence that the holder is
        // gone — it is equally the holder writing it right now, which on Windows is a
        // sharing violation rather than a parse failure. Reclaiming on that would break
        // mutual exclusion, and crashing on it (today's behaviour) merely fails the run.
        // The safe answer is to treat unreadable as HELD, but that is a policy change to
        // mutual exclusion and it should be made deliberately with a test, not folded
        // into a commit about something else.
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
            var pid = doc.RootElement.GetProperty("pid").GetInt32();
            _ = Process.GetProcessById(pid);
            throw new InvalidOperationException("relay: another task is already active");
        }
        catch (ArgumentException)
        {
            Directory.Delete(activeDir, recursive: true);
        }
        catch (JsonException)
        {
            Directory.Delete(activeDir, recursive: true);
        }
        catch (KeyNotFoundException)
        {
            Directory.Delete(activeDir, recursive: true);
        }
    }
}

