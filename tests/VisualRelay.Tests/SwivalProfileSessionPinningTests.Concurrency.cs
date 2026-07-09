using System.Collections.Concurrent;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class SwivalProfileSessionPinningTests
{
    // ──────────────────────────────────────────────────────────────
    // Concurrency: many pinned sessions sharing ONE root's swival.toml
    // (the production stage-7 review/triage/visual pattern).
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduces the stage-7 concurrency defect deterministically: N sibling
    /// invocations pin/restore the SAME swival.toml at once. Each pins identical
    /// content and none edits the file, so after every sibling disposes the tree
    /// must hold the original content — and a concurrent reader (swival reads
    /// this file at launch) must never observe an empty/torn file. Without the
    /// pin-once + atomic-write fix, non-atomic truncate-then-write windows tear
    /// reads and a sibling restores stale/empty content. A barrier releases the
    /// siblings simultaneously and a sized payload widens the window so the race
    /// fires reliably in isolation (not only under full-suite load).
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_ConcurrentSessionsSameRoot_NeverTearsOrStalesFile()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string original = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";

        // A sized payload widens any non-atomic truncate-then-write window so a
        // torn read is reliably observable; the pinned content is identical
        // across siblings, exactly as the review pair pins one frozen profile.
        var pinned = "# pinned profile\n" + new string('x', 64 * 1024) + "\n";

        const int siblings = 8;
        const int rounds = 15;
        var emptyReads = new ConcurrentQueue<string>();

        for (var round = 0; round < rounds; round++)
        {
            await File.WriteAllTextAsync(tomlPath, original);

            // Participants: the reader, every sibling, and this thread.
            using var gate = new Barrier(siblings + 2);
            using var stopReader = new CancellationTokenSource();

            // A concurrent reader mimics swival reading swival.toml at launch: it
            // must never catch the file in a 0-byte truncate window.
            var reader = Task.Run(async () =>
            {
                gate.SignalAndWait();
                while (!stopReader.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(tomlPath) && (await File.ReadAllTextAsync(tomlPath)).Length == 0)
                            emptyReads.Enqueue($"round {round}");
                    }
                    catch (IOException) { /* transient sharing race — tolerate */ }
                }
            });

            var siblingTasks = Enumerable.Range(0, siblings).Select(_ => Task.Run(async () =>
            {
                gate.SignalAndWait();
                await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
                    repo.Root, pinned, "run", "task", eventSink: null, CancellationToken.None);
                // Yield so sibling pin/restore windows overlap.
                await Task.Yield();
            })).ToArray();

            gate.SignalAndWait(); // release the reader and all siblings together
            try
            {
                await Task.WhenAll(siblingTasks);
            }
            finally
            {
                await stopReader.CancelAsync();
                await reader;
            }

            // Every sibling pinned identical content and none edited it, so the
            // original tree content must be restored intact — never empty, never
            // the pinned payload left behind by a torn restore.
            var finalContent = await File.ReadAllTextAsync(tomlPath);
            Assert.Equal(original, finalContent);
        }

        Assert.True(emptyReads.IsEmpty,
            $"a concurrent reader observed an empty swival.toml {emptyReads.Count} time(s)");
    }
}
