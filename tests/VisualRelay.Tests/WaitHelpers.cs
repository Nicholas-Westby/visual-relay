using Avalonia.Threading;

namespace VisualRelay.Tests;

/// <summary>
/// Shared condition-polling helpers for tests.  Uses 50×20ms polling (up to
/// 1 s total wait) driven by <c>Task.Delay</c> — no fixed sleeps.
/// Call sites that need the Avalonia dispatcher flushed between checks use
/// <see cref="WaitUntilWithDispatcherAsync"/>; plain async tests use
/// <see cref="WaitUntilAsync"/>.
/// </summary>
public static class WaitHelpers
{
    /// <summary>
    /// Polls <paramref name="condition"/> every 20 ms up to 50 times
    /// (1 000 ms total).  Returns when the condition is true; fails the
    /// test via <see cref="Assert.True(bool)"/> if it never becomes true.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// Like <see cref="WaitUntilAsync"/> but calls
    /// <c>Dispatcher.UIThread.RunJobs()</c> before each condition
    /// evaluation and after the final check, so Avalonia-bound state
    /// (bindings, layout, command can-execute) has a chance to settle.
    /// </summary>
    public static async Task WaitUntilWithDispatcherAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }
}
