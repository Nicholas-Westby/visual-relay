using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;
using VisualRelay.App.Views.Controls.Buttons;

namespace VisualRelay.Tests;

/// <summary>
/// Rendering-level guards for the Obsidian-bridge row in the Settings dialog.
/// Constructs <see cref="ObsidianSettings"/> directly (no <see cref="MainWindow"/>)
/// so the facts stay scoped to the control under test.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianSettingsRenderTests
{
    /// <summary>
    /// Browse and Reveal must render as visually identical siblings — same
    /// <see cref="ButtonAppearance.Default"/> appearance, no Path variant
    /// on Reveal. The Path variant (dark bg, fatter padding) was built for
    /// the top-bar's repo-path button, not for a plain row action.
    /// </summary>
    [AvaloniaFact]
    public void BrowseAndRevealButtons_HaveEqualDefaultAppearance()
    {
        var (settings, _) = ShowObsidianSettings();

        var buttons = settings.GetVisualDescendants()
            .OfType<CommonButton>()
            .Where(b => b.Content?.ToString() is "Browse" or "Reveal")
            .ToList();

        Assert.Equal(2, buttons.Count);

        var browse = buttons.First(b => b.Content?.ToString() == "Browse");
        var reveal = buttons.First(b => b.Content?.ToString() == "Reveal");

        Assert.Equal(ButtonAppearance.Default, browse.Appearance);
        Assert.Equal(ButtonAppearance.Default, reveal.Appearance);
        Assert.Equal(browse.Appearance, reveal.Appearance);
    }

    /// <summary>
    /// The poll-seconds field label must be static descriptive text, never a
    /// bare number — regression pin on the doubled-60 defect where a hardcoded
    /// <c>Text="60"</c> sat next to the editable TextBox.
    /// </summary>
    [AvaloniaFact]
    public void PollSecondsLabel_IsNonNumericDescriptiveText()
    {
        var (settings, _) = ShowObsidianSettings();

        // The label TextBlock is the first child of the horizontal StackPanel
        // in column 3, alongside the 50px-wide poll-seconds TextBox.
        var pollStackPanel = settings.GetVisualDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(sp =>
                sp.Orientation == Avalonia.Layout.Orientation.Horizontal
                && sp.Children.OfType<TextBox>().Any(tb => tb.Width == 50));

        Assert.NotNull(pollStackPanel);

        var label = pollStackPanel!.Children.OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(label);
        var labelText = label!.Text;
        Assert.NotNull(labelText);

        // The label must NOT parse as an integer — if it does, the doubled-60
        // defect has returned.
        Assert.False(int.TryParse(labelText, out _),
            $"Poll label text '{labelText}' must not be a bare number");

        // Must be descriptive (not empty).
        Assert.NotEmpty(labelText);
    }

    /// <summary>
    /// The poll-seconds <see cref="TextBox"/> must still two-way bind
    /// <c>ObsidianPollSeconds</c> — the VM-level coverage exists in
    /// <see cref="ObsidianBridgeVmPropertiesTests"/>, so this fact only
    /// pins the binding at the view layer.
    /// </summary>
    [AvaloniaFact]
    public void PollSecondsTextBox_BindsTwoWayToObsidianPollSeconds()
    {
        var vm = SandboxedViewModel();
        vm.ObsidianPollSeconds = 90;

        var settings = new ObsidianSettings { DataContext = vm };
        var host = new Window { Content = settings, Width = 600, Height = 200 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        // The poll TextBox is the one with Width=50 inside the horizontal StackPanel.
        var textBox = settings.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.Width == 50);

        Assert.NotNull(textBox);
        Assert.Equal("90", textBox!.Text);

        // Drive a change from the VM side and verify the view updates.
        vm.ObsidianPollSeconds = 15;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("15", textBox.Text);

        // Drive a change from the view side and verify the VM sees it.
        textBox.Text = "30";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(30, vm.ObsidianPollSeconds);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Hosts a standalone <see cref="ObsidianSettings"/> bound to a sandboxed
    /// VM in a bare window. Enables <see cref="MainWindowViewModel.ObsidianEnabled"/>
    /// so the grid row renders.
    /// </summary>
    private static (ObsidianSettings settings, Window host) ShowObsidianSettings()
    {
        var vm = SandboxedViewModel();
        vm.ObsidianEnabled = true;
        var settings = new ObsidianSettings { DataContext = vm };
        var host = new Window { Content = settings, Width = 600, Height = 200 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return (settings, host);
    }

    private static MainWindowViewModel SandboxedViewModel()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["XDG_CONFIG_HOME"] = Path.GetTempPath()
        };
        return new MainWindowViewModel(env);
    }
}
