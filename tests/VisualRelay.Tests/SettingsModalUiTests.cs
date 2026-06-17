using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Behavioural tests for settings as a modal dialog. The cog used to open a
/// flyout whose FlyoutPresenter added a second scrollbar over the panel's own
/// and clipped the bottom ("Live Tiers") at its MaxHeight; it now opens a
/// resizable <see cref="SettingsWindow"/> sized so the whole panel fits with no
/// scrolling at the default size.
/// </summary>
[Collection("Headless")]
public sealed class SettingsModalUiTests
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    [AvaloniaFact]
    public async Task SettingsButtonOpensModalWindow_WithVmDataContext_SingleScrollRegion_AndBoundControl()
    {
        SettingsTestHelpers.EnsureNoUserEnv(_env);
        using var repo = TestRepository.Create();
        SettingsTestHelpers.WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SettingsTestHelpers.SeedUserEnv(_env, repo, "HF_TOKEN=hf-modal-test\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The cog opens a modal SettingsWindow (not a flyout): it is an owned,
        // resizable window carrying the same MainWindowViewModel DataContext.
        var dialog = SettingsTestHelpers.OpenSettings(window);
        Assert.True(dialog.CanResize);
        Assert.Same(vm, dialog.DataContext);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().Single();
        Assert.Same(vm, panel.DataContext);

        // Exactly one layout scroll region in the whole dialog — never the nested
        // pair (flyout + panel) that clipped "Live Tiers" before. (TextBox
        // templates carry their own internal ScrollViewer; those are excluded.)
        var layoutScrolls = SettingsTestHelpers.LayoutScrollViewers(dialog);
        Assert.Single(layoutScrolls);
        Assert.Equal("SettingsScrollViewer", layoutScrolls[0].Name);

        // A representative bound control reflects the VM: the HF key field is
        // present and the seeded token shows as set.
        var hfInput = panel.FindControl<TextBox>("HfTokenInput");
        Assert.NotNull(hfInput);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN").IsSet);

        // Closing via the window mirrors back into the VM open-state.
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsSettingsOpen);
        Assert.Empty(window.OwnedWindows);
    }

    [AvaloniaFact]
    public async Task SettingsModal_AtDefaultSize_FitsWithoutScrolling_AndLiveTiersIsVisible()
    {
        SettingsTestHelpers.EnsureNoUserEnv(_env);
        using var repo = TestRepository.Create();
        SettingsTestHelpers.WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SettingsTestHelpers.SeedUserEnv(_env, repo, "HF_TOKEN=hf-test\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        await vm.OpenSettingsAsync();

        // Show the dialog at its own declared default size (no owner needed).
        var dialog = new SettingsWindow { DataContext = vm };
        dialog.Show();
        dialog.Measure(new Size(dialog.Width, dialog.Height));
        dialog.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
        Dispatcher.UIThread.RunJobs();

        // The single scroll region must not actually need to scroll at the
        // default size — the whole panel (including "Live Tiers") fits. This is
        // the regression for the old flyout that clipped content at MaxHeight.
        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Name == "SettingsScrollViewer");
        Assert.True(
            scroll.Extent.Height <= scroll.Viewport.Height + 0.5,
            $"settings content overflows at default size: extent={scroll.Extent.Height} viewport={scroll.Viewport.Height}");

        var liveTiers = dialog.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Live Tiers");
        Assert.NotNull(liveTiers);
        Assert.True(liveTiers.Bounds.Height > 0, "Live Tiers should be laid out (not clipped)");

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
