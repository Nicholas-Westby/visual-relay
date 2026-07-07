using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.App.Views.Controls.Buttons;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class InitPanelButtonsLayoutTests
{
    [AvaloniaFact]
    public async Task InitEmptyState_ActionButtonsAreFullWidthAndVerticallyStacked()
    {
        // ── Arrange: config-less repo ──
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        // ── Show the real window ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Resolve QueuePanel from the visual tree
        var queuePanel = window.GetVisualDescendants().OfType<QueuePanel>().Single();

        // ── Find the two action buttons ──
        var createConfigButton = queuePanel.FindControl<CommonButton>("CreateConfigButton");
        Assert.NotNull(createConfigButton);

        var allButtons = queuePanel.GetVisualDescendants().OfType<CommonButton>().ToList();
        var findItButton = allButtons.FirstOrDefault(b => b.Content?.ToString() == "Find it for me");
        Assert.NotNull(findItButton);

        // ── Assert: buttons are vertically stacked ──
        // On today's *,* Grid they sit side-by-side (different X) — this fails.
        Assert.True(
            Math.Abs(createConfigButton.Bounds.X - findItButton.Bounds.X) < 1.0,
            $"Buttons should be in the same column (vertically stacked). "
            + $"CreateConfig X={createConfigButton.Bounds.X:F1}, FindIt X={findItButton.Bounds.X:F1}");

        Assert.True(
            createConfigButton.Bounds.Y < findItButton.Bounds.Y,
            "CreateConfigButton should be above FindItButton");

        // ── Assert: each button is wide enough for its label ──
        AssertButtonWidthSufficient(createConfigButton, "Create config");
        AssertButtonWidthSufficient(findItButton, "Find it for me");
    }

    private static void AssertButtonWidthSufficient(CommonButton button, string expectedLabel)
    {
        // Find the actual Button inside the composed component (inner template Button).
        var innerButton = button.GetVisualDescendants().OfType<Button>().FirstOrDefault()
            ?? throw new InvalidOperationException("Composed button must contain an inner Button.");

        var textBlock = innerButton.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Text == expectedLabel);
        Assert.NotNull(textBlock);

        // Measure with infinite width to learn the label's unconstrained desired width.
        textBlock.Measure(Size.Infinity);
        var labelDesiredWidth = textBlock.DesiredSize.Width;

        // Use the inner Button's padding (from the theme), not the outer CommonButton's.
        var horizontalPadding = innerButton.Padding.Left + innerButton.Padding.Right;
        var requiredWidth = labelDesiredWidth + horizontalPadding;

        Assert.True(
            button.Bounds.Width >= requiredWidth - 1.0,
            $"Button '{expectedLabel}' arranged width {button.Bounds.Width:F1} px "
            + $"should be ≥ label desired width {labelDesiredWidth:F1} px + "
            + $"horizontal padding {horizontalPadding:F1} px = {requiredWidth:F1} px "
            + $"(label is clipped when the button content area is too narrow)");
    }
}
