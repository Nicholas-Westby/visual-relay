using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The lock's one promise: while a live process holds it, nobody else does.
/// Benched on Windows with six callers over four seconds — 10 acquisitions, peak 3
/// simultaneous, and 6 of the 10 were holding a lock whose info.json had already been
/// taken over by somebody else.
/// </summary>
public sealed class ActiveTaskLockExclusionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vr-lock-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Runs <paramref name="callers"/> acquires at once and returns the peak number that
    /// were inside the lock together. No sleeping: a holder parks on a gate the test opens
    /// only after every caller has finished trying, so the overlap is created by the gate
    /// rather than hoped for from a delay.
    /// </summary>
    private static async Task<(int Peak, int Other)> RaceAsync(string root, int callers)
    {
        var ready = new SemaphoreSlim(0, callers);
        var attempted = new SemaphoreSlim(0, callers);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A single-element array rather than a plain local: peak is read back through
        // Volatile.Read outside the racers below, and a captured local ReSharper can see
        // mutated from two scopes reads as a closure bug even though the Interlocked
        // traffic here is exactly what keeps it safe.
        var peak = new int[1];
        int other = 0, live = 0;

        var racers = Enumerable.Range(0, callers).Select(i => Task.Run(async () =>
        {
            ready.Release();
            await go.Task;
            try
            {
                await using var acquired = await ActiveTaskLock.AcquireAsync(root, $"task-{i}", CancellationToken.None);
                InterlockedMax(peak, Interlocked.Increment(ref live));
                attempted.Release();
                await hold.Task;
                Interlocked.Decrement(ref live);
            }
            catch (InvalidOperationException) { attempted.Release(); }
            catch { Interlocked.Increment(ref other); attempted.Release(); }
        })).ToArray();

        for (var i = 0; i < callers; i++)
            await ready.WaitAsync();
        go.SetResult();
        for (var i = 0; i < callers; i++)
            await attempted.WaitAsync();

        var observed = Volatile.Read(ref peak[0]);
        hold.SetResult();
        await Task.WhenAll(racers);
        return (observed, other);
    }

    private static void InterlockedMax(int[] target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target[0])) < value
               && Interlocked.CompareExchange(ref target[0], value, seen) != seen)
        {
        }
    }

    /// <summary>Six callers on one repository: at most one may be inside the lock at a time.</summary>
    [Fact]
    public async Task SixCallersOnOneRepository_NeverHoldItAtTheSameTime()
    {
        var (peak, other) = await RaceAsync(_root, 6);

        Assert.Equal(0, other);
        // Exactly one, not at most one: a lock that refused every caller would satisfy
        // "never two at once" while being useless, and this test is the only thing
        // standing between that and a green suite.
        Assert.Equal(1, peak);
    }

    /// <summary>
    /// A holder creates its claim and then writes it, so for a moment the claim exists
    /// and is empty. A racer reading it then found it unparseable, deleted it as
    /// abandoned and took the lock beside its live holder: 39 overlapping rounds in 1000
    /// of the six-caller race run alone, peak 3. POSIX lets the racer read and delete a
    /// file the holder has open; on Windows the holder's handle refuses the read.
    /// </summary>
    [Fact]
    public async Task AClaimStillBeingWritten_IsHeld()
    {
        var activeDir = Path.Combine(_root, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        await using var writing = new FileStream(Path.Combine(activeDir, "info.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ActiveTaskLock.AcquireAsync(_root, "racer", CancellationToken.None));
    }

    /// <summary>
    /// A caller that is cancelled must not leave its claim behind half written: an empty
    /// claim reads as held, so it would lock the repository in the name of a caller that
    /// never got the lock.
    /// </summary>
    [Fact]
    public async Task ACancelledCaller_LeavesNoClaimBehind()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ActiveTaskLock.AcquireAsync(_root, "cancelled", cancelled.Token));

        Assert.False(File.Exists(Path.Combine(_root, ".relay", "ACTIVE", "info.json")), "the cancelled caller left its claim");
        await using var next = await ActiveTaskLock.AcquireAsync(_root, "next", CancellationToken.None);
    }

    /// <summary>
    /// A claim that has stayed unparseable for far longer than any holder takes to write
    /// one was abandoned between the two steps, by a process that died there, and is
    /// cleared rather than blocking the repository for good.
    /// </summary>
    [Fact]
    public async Task AClaimLeftHalfWrittenLongAgo_IsReclaimed()
    {
        var activeDir = Path.Combine(_root, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        var info = Path.Combine(activeDir, "info.json");
        await File.WriteAllTextAsync(info, "", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(info, DateTime.UtcNow - TimeSpan.FromHours(1));

        await using var acquired = await ActiveTaskLock.AcquireAsync(_root, "next", CancellationToken.None);

        Assert.Contains("\"next\"", await File.ReadAllTextAsync(info, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The contract is that acquiring either returns a lock or REFUSES; a raw filesystem
    /// exception is neither. Measured on Windows: <c>FileMode.CreateNew</c> reports an
    /// existing target as IOException normally but as UnauthorizedAccessException when
    /// that target is delete-pending, which is the state the reclaim path leaves it in,
    /// and 28 callers in four seconds crashed out where a refusal was intended.
    /// <para>
    /// Delete-pending does not exist on POSIX, so this reaches the same catch by the
    /// other route available here — a claim directory that cannot be written to. It pins
    /// the contract rather than reproducing their case; only Windows can do that.
    /// </para>
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task WhenTheClaimCannotBeCreatedAtAll_ItRefusesRatherThanThrowingRaw()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mode bits are the POSIX route to an unwritable claim");
        var activeDir = Path.Combine(_root, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        File.SetUnixFileMode(activeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ActiveTaskLock.AcquireAsync(_root, "task", CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(activeDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// The shape that produced the escapes: callers acquiring and releasing repeatedly,
    /// so one caller's release is deleting the claim directory while another is creating
    /// its claim inside it. Measured on Windows, that race is where the crashes came
    /// from — creating against a deleting parent gave 2485 DirectoryNotFound, 158
    /// IOException and 97 UnauthorizedAccess in 2.5 s, against 3 for racing the file's
    /// own deletion. Nothing may escape acquiring, and two callers may never be in.
    /// <para>
    /// It does not isolate the vanished-directory branch from the refusing one: both
    /// leave the caller outside the lock, and only the message differs. It guards the
    /// class, which is that a contended lock answers rather than throws.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CallersChurningTheLock_NeverEscapeAndNeverOverlap()
    {
        var escaped = new System.Collections.Concurrent.ConcurrentBag<string>();
        var peak = new int[1];
        int live = 0, acquired = 0;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            for (var round = 0; round < 25; round++)
            {
                try
                {
                    await using var held = await ActiveTaskLock.AcquireAsync(
                        _root, $"task-{i}", CancellationToken.None);
                    Interlocked.Increment(ref acquired);
                    InterlockedMax(peak, Interlocked.Increment(ref live));
                    Interlocked.Decrement(ref live);
                }
                catch (InvalidOperationException) { }
                // Named, not counted: a bare tally tells you something escaped and not
                // what, which is a failure message that needs a second run to act on.
                catch (Exception ex) { escaped.Add($"{ex.GetType().Name}: {ex.Message}"); }
            }
        })));

        Assert.True(escaped.IsEmpty, "acquiring threw: " + string.Join(" | ", escaped.Distinct()));
        Assert.Equal(1, peak[0]);
        // The churn has to actually reach the lock, or the test proves nothing.
        Assert.True(acquired > 0, "no caller ever acquired, so nothing was exercised");
    }
}
