using VisualRelay.App.DesignTime;

namespace VisualRelay.Tests;

/// <summary>
/// Plain view-model assertions that <see cref="DesignData"/> covers every
/// card state the previewer needs. No Avalonia session — pure [Fact]s.
/// </summary>
public sealed class DesignDataTests
{
    /// <summary>
    /// The five tasks in <see cref="DesignData.Main"/> must cover every
    /// card state: exactly one running, at least one needs-review, at least
    /// one archived, at least one with no run history, at least one
    /// non-empty DayHeader, and exactly one selected.
    /// </summary>
    [Fact]
    public void Main_CoversEveryCardState()
    {
        var tasks = DesignData.Main.Tasks;

        Assert.Single(tasks, t => t.IsRunning);
        Assert.Contains(tasks, t => t.NeedsReview && !t.IsRunning);
        Assert.Contains(tasks, t => t.IsArchived);
        Assert.Contains(tasks, t => t.Task.CompletedStageCount == 0 && !t.IsRunning);
        Assert.Contains(tasks, t => !string.IsNullOrEmpty(t.DayHeader));
        Assert.Single(tasks, t => t.IsSelected);
    }

    /// <summary>
    /// <see cref="DesignData.Main.SelectedTask"/> must stay null so the
    /// previewer never triggers <c>SelectTaskAsync</c>, which reads the
    /// fabricated markdown path from disk and writes the failure into
    /// <see cref="MainWindowViewModel.StatusText"/>, polluting every preview.
    /// </summary>
    [Fact]
    public void Main_LeavesSelectedTaskNull_SoPreviewsDoNoDiskIo()
    {
        // SelectTaskAsync (MainWindowViewModel.Commands.cs) fires when
        // SelectedTask changes. The DesignData markdown paths don't exist
        // on disk, so a non-null SelectedTask would surface an I/O failure
        // into StatusText and corrupt every preview.
        Assert.Null(DesignData.Main.SelectedTask);
    }

    /// <summary>
    /// <see cref="DesignData.Card"/> must be running, selected, mid-progress
    /// (8 of 12 stages complete), and its <c>MetricsLine</c> must contain the
    /// running-stage label so the previewer shows a live-stage card.
    /// </summary>
    [Fact]
    public void Card_IsRunningSelectedMidProgress()
    {
        var card = DesignData.Card;

        Assert.True(card.IsRunning);
        Assert.True(card.IsSelected);
        Assert.InRange(card.ProgressFraction, 0.01, 0.99);
        Assert.Contains("Stage 09 · Fix", card.MetricsLine);
    }
}
