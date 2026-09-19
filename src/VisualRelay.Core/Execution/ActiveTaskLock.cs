using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisualRelay.Core.Execution;

/// <summary>
/// One running task per repository, claimed on disk so a second process refuses rather
/// than interleaving with the first.
/// <para>
/// The claim is creating <c>info.json</c> with <see cref="FileMode.CreateNew"/>, which is
/// the only create-if-absent a filesystem offers atomically. It used to be
/// <c>Directory.CreateDirectory</c> followed by a write, and neither step can fail when
/// the thing already exists, so nothing ever actually claimed: every racer created the
/// same directory, every racer wrote the same file, and every racer returned a lock.
/// Measured with six callers on one repository — on Windows, 10 acquisitions with 3
/// holding at once and 6 of the 10 holding a claim another caller had already
/// overwritten; on macOS, all six inside the lock at the same time. The exclusion was
/// not weak, it was absent.
/// </para>
/// <para>
/// Reaching it needs two acquires on the SAME repository within a few milliseconds. The
/// only caller passes a worktree path per task, so concurrent planning never contends
/// and this was latent rather than active; two app instances on one repository, or a
/// double start, is what would have found it.
/// </para>
/// </summary>
internal sealed class ActiveTaskLock : IAsyncDisposable
{
    /// <summary>Tries to clear a stale claim this many times before refusing, and to release as often.</summary>
    private const int Attempts = 4;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How long a claim may stay unparseable before it counts as abandoned. Writing one
    /// takes a buffered write and a flush straight after creating it, so this is thousands
    /// of times longer than any live holder needs, and short enough that a holder which
    /// died between the two steps blocks the repository only briefly.
    /// </summary>
    private static readonly TimeSpan HalfWrittenGrace = TimeSpan.FromSeconds(10);

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
        var activeDir = Path.Combine(relayDir, "ACTIVE");
        EnsureClaimDirectory(activeDir);
        var infoPath = Path.Combine(activeDir, "info.json");

        var nonce = Guid.NewGuid().ToString("N");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            task = taskId,
            pid = Environment.ProcessId,
            nonce
        }));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var claim = new FileStream(
                    infoPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read | FileShare.Delete);
                await claim.WriteAsync(payload, cancellationToken);
                return new ActiveTaskLock(activeDir, nonce);
            }
            // The previous holder's Release is deleting this directory out from under
            // the claim. A directory marked for deletion still satisfies
            // Directory.CreateDirectory — it exists — while refusing file creation
            // inside it, which is why creating the directory above does not shield this
            // line. The lock is FREE in that case rather than held, so recreate and race
            // for it again instead of answering "already active", which would be the
            // opposite of the truth. Must precede the arms below: this derives from
            // IOException.
            catch (DirectoryNotFoundException) when (attempt < Attempts)
            {
                EnsureClaimDirectory(activeDir);
            }
            // Both arms name UnauthorizedAccessException, which does NOT derive from
            // IOException. Racing that same parent deletion is what produces it, measured
            // on Windows over 2.5 s: creating against a deleting PARENT gave 2485
            // DirectoryNotFound, 158 IOException and 97 UnauthorizedAccess, while racing
            // the FILE's own deletion gave 3 in twice as many attempts — about thirty
            // times less per attempt. So the source is the release path, not the reclaim
            // path, and a claim that cannot be created for any of those reasons is one
            // this caller does not hold. The other two methods in this file already named
            // UnauthorizedAccessException; only the claim did not. Measured effect of
            // naming it: 28 escapes in four seconds became 0.
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && attempt < Attempts && ReclaimedStaleClaim(infoPath))
            {
                // The holder was provably gone and its claim is cleared; race for it again.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("relay: another task is already active");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates the claim directory, tolerating the previous holder's recursive delete
    /// running at the same time. Creating a directory that is being removed reports
    /// EEXIST transiently — "The file ... ACTIVE already exists" — even though it is on
    /// its way out, and that escaped acquiring in 3 of 6 churn runs before this existed.
    /// Giving up silently is correct: the claim below then fails on its own terms and is
    /// answered by the loop, rather than this method inventing a verdict about a lock it
    /// knows nothing about.
    /// </summary>
    private static void EnsureClaimDirectory(string activeDir)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(activeDir);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Drops the claim. Nothing here may throw: an unguarded recursive delete used to
    /// escape, leaving <c>_released</c> false and the claim on disk, and because the
    /// recorded pid is this still-live process nothing could ever reclaim it. Measured on
    /// Windows, it leaked in 5 runs of 5, and one leaked run managed a single acquisition
    /// against 52,407 refusals for the rest of its life. A stuck lock outlives the task
    /// that failed to release it, so failing quietly here is better than failing loudly.
    /// </summary>
    private void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                    return;
                Thread.Sleep(RetryDelay);
            }
        }
    }

    /// <summary>
    /// Whether an existing claim belonged to a process that is gone, in which case it has
    /// been cleared. False means the claim stands and the caller must refuse.
    /// <para>
    /// An UNREADABLE claim counts as HELD. It is not evidence the holder left — it is
    /// equally the holder writing it right now — and measured under six-way contention
    /// that is what it always was: 37 IO-family failures in roughly 46,000 attempts,
    /// about 0.08%, every one of them while a holder was genuinely live. Refusing on an
    /// unreadable claim therefore costs almost nothing and never costs the wrong thing,
    /// whereas reclaiming on one hands the repository to two tasks at once.
    /// </para>
    /// <para>
    /// An UNPARSEABLE claim is the same case by another route, and used to be treated as
    /// abandoned. A holder creates its claim and then writes it, so for a moment every
    /// claim is empty; POSIX lets a racer read that and delete it while the holder has it
    /// open, and the racer then took the lock beside the holder. Measured on macOS, six
    /// callers racing: 39 rounds in 1000 had two or more inside at once, peak 3. So a
    /// claim that does not parse stands until it has stayed that way far longer than a
    /// write takes, which only a holder that died between the two steps leaves behind.
    /// </para>
    /// </summary>
    private static bool ReclaimedStaleClaim(string infoPath)
    {
        int pid;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
            pid = doc.RootElement.GetProperty("pid").GetInt32();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(infoPath) > HalfWrittenGrace && TryDelete(infoPath);
        }

        try
        {
            using var holder = Process.GetProcessById(pid);
            return holder.HasExited && TryDelete(infoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return TryDelete(infoPath);
        }
    }

    private static bool TryDelete(string infoPath)
    {
        try
        {
            File.Delete(infoPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
