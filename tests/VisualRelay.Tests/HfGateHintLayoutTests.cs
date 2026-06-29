using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class HfGateHintLayoutTests
{
    /// <summary>
    /// The HF-gate banner hint "or open Settings ⚙ in the top bar" must not be
    /// clipped at the QueuePanel's fixed 280 px width. Before the fix the hint
    /// was laid out horizontally beside the "Get a free token →" button, and the
    /// combined width (~326 px) exceeded the ~250 px content area, causing the
    /// outer Border's ClipToBounds to cut ~61 px (35%) of the hint.
    ///
    /// After the fix the hint is on its own line with TextWrapping="Wrap", so it
    /// stays within the 280 px panel boundary.
    /// </summary>
    [AvaloniaFact]
    public async Task HfGateHint_IsNotClipped_AtFixed280PxPanelWidth()
    {
        // ── Arrange: config + task, no HF token ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var env = new DictionaryEnvironmentAccessor();
        using var _ = SettingsTestHelpers.SeedUserEnv(env, repo, "");

        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            EnvironmentAccessor = env
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.ShowHfGate,
            "ShowHfGate must be true when no HF_TOKEN is configured");

        // ── Show the real window ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Resolve QueuePanel and the hint TextBlock ──
        var queuePanel = window.GetVisualDescendants().OfType<QueuePanel>().Single();

        var hintBlock = queuePanel.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => (tb.Text ?? "").StartsWith("or open Settings"));
        Assert.NotNull(hintBlock);

        // ── Assert: the hint's right edge, translated into QueuePanel
        //    coordinates, does not exceed the panel's 280 px width. ──
        var hintRightInPanel = hintBlock.TranslatePoint(
            new Point(hintBlock.Bounds.Width, 0), queuePanel);

        Assert.NotNull(hintRightInPanel);
        Assert.True(
            hintRightInPanel.Value.X <= 280.0,
            $"HF-gate hint right edge {hintRightInPanel.Value.X:F1} px exceeds "
            + "QueuePanel width 280 px — hint text is clipped. "
            + $"Hint Bounds={hintBlock.Bounds}, "
            + $"DesiredSize={hintBlock.DesiredSize}");

        // ── Additionally: the hint must be wide enough to show its text
        //    (i.e. not collapsed to zero or trimmed to an ellipsis). ──
        Assert.True(
            hintBlock.Bounds.Width > 20,
            $"Hint TextBlock arranged width {hintBlock.Bounds.Width:F1} px is "
            + "suspiciously narrow — hint may be collapsed or truncated");
    }
}
