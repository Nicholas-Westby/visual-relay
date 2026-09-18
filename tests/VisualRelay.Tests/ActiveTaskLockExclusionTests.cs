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
        int other = 0, peak = 0, live = 0;

        var racers = Enumerable.Range(0, callers).Select(i => Task.Run(async () =>
        {
            ready.Release();
            await go.Task;
            try
            {
                await using var acquired = await ActiveTaskLock.AcquireAsync(root, $"task-{i}", CancellationToken.None);
                InterlockedMax(ref peak, Interlocked.Increment(ref live));
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

        var observed = Volatile.Read(ref peak);
        hold.SetResult();
        await Task.WhenAll(racers);
        return (observed, other);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, seen) != seen)
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
}
