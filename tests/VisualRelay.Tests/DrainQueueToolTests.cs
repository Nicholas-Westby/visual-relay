using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;
using VisualRelay.DrainQueue;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the DrainQueue console tool's testable units:
/// ArgParser, DrainOutcome, and the end-to-end drain behavior with
/// the two-phase RelayQueueController.
/// </summary>
public sealed partial class DrainQueueToolTests
{
    // ═══════════════════════════════════════════════════════════════
    // ArgParser.Parse
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_EmptyArgs_ReturnsError()
    {
        var (result, error) = ArgParser.Parse([]);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("usage", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_OnlyRoot_ReturnsTaskIdsNull()
    {
        var (result, error) = ArgParser.Parse(["/tmp/test-repo"]);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath("/tmp/test-repo"), result.RootPath);
        Assert.Null(result.TaskIds);
    }

    [Fact]
    public void Parse_RootWithTaskIds_ReturnsTaskIdsInOrder()
    {
        var (result, error) = ArgParser.Parse(["/tmp/repo", "alpha", "beta", "gamma"]);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath("/tmp/repo"), result.RootPath);
        Assert.NotNull(result.TaskIds);
        Assert.Equal(["alpha", "beta", "gamma"], result.TaskIds);
    }

    [Fact]
    public void Parse_RelativeRoot_ResolvesToFullPath()
    {
        var cwd = Environment.CurrentDirectory;
        var (result, error) = ArgParser.Parse(["relay-root"]);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath("relay-root"), result.RootPath);
        Assert.Null(result.TaskIds);
    }

    // ═══════════════════════════════════════════════════════════════
    // ArgParser.ValidateTaskIds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateTaskIds_AllKnown_ReturnsNull()
    {
        var pending = new List<RelayTaskItem>
        {
            new("alpha", "/t/alpha.md", "/t/alpha", false, []),
            new("beta", "/t/beta.md", "/t/beta", false, []),
            new("gamma", "/t/gamma.md", "/t/gamma", false, []),
        };

        var error = ArgParser.ValidateTaskIds(["alpha", "gamma"], pending);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateTaskIds_SingleUnknown_ReturnsErrorMessage()
    {
        var pending = new List<RelayTaskItem>
        {
            new("alpha", "/t/alpha.md", "/t/alpha", false, []),
        };

        var error = ArgParser.ValidateTaskIds(["beta"], pending);
        Assert.NotNull(error);
        Assert.Contains("beta", error, StringComparison.Ordinal);
        Assert.Contains("unknown", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTaskIds_MultipleUnknown_ReturnsAllInErrorMessage()
    {
        var pending = new List<RelayTaskItem>
        {
            new("alpha", "/t/alpha.md", "/t/alpha", false, []),
        };

        var error = ArgParser.ValidateTaskIds(["beta", "gamma", "delta"], pending);
        Assert.NotNull(error);
        Assert.Contains("beta", error, StringComparison.Ordinal);
        Assert.Contains("gamma", error, StringComparison.Ordinal);
        Assert.Contains("delta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTaskIds_EmptyRequestedIds_ReturnsNull()
    {
        var pending = new List<RelayTaskItem>
        {
            new("alpha", "/t/alpha.md", "/t/alpha", false, []),
        };

        var error = ArgParser.ValidateTaskIds([], pending);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateTaskIds_EmptyPendingSet_AnyRequestedIsUnknown()
    {
        var pending = Array.Empty<RelayTaskItem>();

        var error = ArgParser.ValidateTaskIds(["alpha"], pending);
        Assert.NotNull(error);
        Assert.Contains("alpha", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTaskIds_NeedsReviewTask_IsStillPendingAndRecognized()
    {
        // NEEDS-REVIEW tasks are excluded from the queue during RefreshAsync
        // but could still be in the Tasks collection.  They should be treated
        // as known — ValidateTaskIds only checks membership.
        var pending = new List<RelayTaskItem>
        {
            new("alpha", "/t/alpha.md", "/t/alpha", false, [], ReviewReason: "some reason"),
        };

        var error = ArgParser.ValidateTaskIds(["alpha"], pending);
        Assert.Null(error);
    }
}
