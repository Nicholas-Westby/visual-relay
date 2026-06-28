using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Receiver-shape facts for <see cref="SyncOverAsyncGuard"/>: the <c>.Result</c> /
/// <c>.Wait()</c> rules must fire ONLY on a plausibly-Task receiver, so legitimate
/// non-Task members (<c>record.Result</c>, <c>SemaphoreSlim.Wait()</c>,
/// <c>Match.Result(...)</c>, <c>Task.Run(() =&gt; x.Result)</c>) never trip the
/// guard — a false positive would break the build the moment such code lands —
/// while real UI-thread sync-over-async (<c>SomethingAsync().Result</c>/<c>.Wait()</c>,
/// <c>Task.WaitAll(...)</c>) IS still caught.
/// </summary>
public sealed class SyncOverAsyncGuardReceiverTests
{
    // ── False positives that must NOT be flagged ────────────────────────

    /// <summary><c>record.Result</c> — a property on a non-Task value — is not flagged.</summary>
    [Fact]
    public void RecordResultProperty_NotFlagged()
    {
        const string source = """
            using Xunit;
            record Outcome(int Result);
            class C { [Fact] void M() { var outcome = new Outcome(7); _ = outcome.Result; } }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/Record.cs", source)]));
    }

    /// <summary><c>semaphore.Wait()</c> / <c>SemaphoreSlim.Wait()</c> is not flagged.</summary>
    [Fact]
    public void SemaphoreWait_NotFlagged()
    {
        const string source = """
            using Xunit;
            using System.Threading;
            class C { [Fact] void M() { var gate = new SemaphoreSlim(1); gate.Wait(); } }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/Semaphore.cs", source)]));
    }

    /// <summary><c>CountdownEvent.Wait()</c> is not flagged.</summary>
    [Fact]
    public void CountdownEventWait_NotFlagged()
    {
        const string source = """
            using Xunit;
            using System.Threading;
            class C { [Fact] void M() { var done = new CountdownEvent(1); done.Wait(); } }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/Countdown.cs", source)]));
    }

    /// <summary><c>Match.Result("$1")</c> — <c>.Result</c> is a METHOD CALL, not the
    /// Task property — is not flagged.</summary>
    [Fact]
    public void InvokedResultMethod_NotFlagged()
    {
        const string source = """
            using Xunit;
            using System.Text.RegularExpressions;
            class C { [Fact] void M() { var m = Regex.Match("a", "(.)"); var s = m.Result("$1"); } }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/Match.cs", source)]));
    }

    /// <summary><c>.Result</c> lexically inside a <c>Task.Run(...)</c> lambda runs on a
    /// pool thread (no UI deadlock) and is not flagged — even on a Task receiver.</summary>
    [Fact]
    public void ResultInsideTaskRunLambda_NotFlagged()
    {
        // InnerAsync().Result IS a Task-ish receiver — it would be flagged at top
        // level; here it is excused only because it runs inside the Task.Run lambda.
        const string source = """
            using Xunit;
            using System.Threading.Tasks;
            class C { [Fact] void M() { Task.Run(() => { _ = InnerAsync().Result; }); } static Task<int> InnerAsync() => Task.FromResult(1); }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/Offload.cs", source)]));
    }

    /// <summary><c>.Wait()</c> inside a <c>Task.Factory.StartNew(...)</c> lambda is not flagged.</summary>
    [Fact]
    public void WaitInsideStartNewLambda_NotFlagged()
    {
        const string source = """
            using Xunit;
            using System.Threading.Tasks;
            class C { [Fact] void M() { Task.Factory.StartNew(() => InnerAsync().Wait()); } static Task InnerAsync() => Task.CompletedTask; }
            """;

        Assert.Empty(SyncOverAsyncGuard.FindViolations([("Fixtures/StartNew.cs", source)]));
    }

    // ── Real sync-over-async that MUST be flagged ───────────────────────

    /// <summary><c>SomethingAsync().Result</c> on the UI thread IS flagged.</summary>
    [Fact]
    public void AsyncInvocationResult_IsFlagged()
    {
        const string source = """
            using Xunit;
            class C { [Fact] void M() { _ = FetchAsync().Result; } static Task<int> FetchAsync() => Task.FromResult(1); }
            """;

        var v = Assert.Single(SyncOverAsyncGuard.FindViolations([("Fixtures/AsyncResult.cs", source)]));
        Assert.Contains(".Result", v.Reason);
    }

    /// <summary><c>SomethingAsync().Wait()</c> on the UI thread IS flagged.</summary>
    [Fact]
    public void AsyncInvocationWait_IsFlagged()
    {
        const string source = """
            using Xunit;
            class C { [Fact] void M() { FetchAsync().Wait(); } static Task FetchAsync() => Task.CompletedTask; }
            """;

        var v = Assert.Single(SyncOverAsyncGuard.FindViolations([("Fixtures/AsyncWait.cs", source)]));
        Assert.Contains(".Wait()", v.Reason);
    }

    /// <summary>A receiver whose NAME ends in "Task" (<c>downloadTask.Result</c>) IS flagged.</summary>
    [Fact]
    public void NamedTaskReceiverResult_IsFlagged()
    {
        const string source = """
            using Xunit;
            class C { [Fact] void M() { var downloadTask = Run(); _ = downloadTask.Result; } static Task<int> Run() => Task.FromResult(1); }
            """;

        Assert.Single(SyncOverAsyncGuard.FindViolations([("Fixtures/NamedTask.cs", source)]));
    }

    /// <summary>Static <c>Task.WaitAll(...)</c> blocking join IS flagged.</summary>
    [Fact]
    public void TaskWaitAll_IsFlagged()
    {
        const string source = """
            using Xunit;
            using System.Threading.Tasks;
            class C { [Fact] void M() { Task.WaitAll(A(), B()); } static Task A() => Task.CompletedTask; static Task B() => Task.CompletedTask; }
            """;

        var v = Assert.Single(SyncOverAsyncGuard.FindViolations([("Fixtures/WaitAll.cs", source)]));
        Assert.Contains(".WaitAll()", v.Reason);
    }

    /// <summary>The Xunit.StaFact family is recognized: <c>[StaFact]</c> bodies are scanned.</summary>
    [Fact]
    public void StaFactAttribute_IsRecognized()
    {
        const string source = """
            using Xunit;
            class C { [StaFact] void M() { _ = FetchAsync().Result; } static Task<int> FetchAsync() => Task.FromResult(1); }
            """;

        Assert.Single(SyncOverAsyncGuard.FindViolations([("Fixtures/Sta.cs", source)]));
    }
}
