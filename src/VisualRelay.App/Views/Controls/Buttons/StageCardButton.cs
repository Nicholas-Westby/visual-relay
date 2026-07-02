using Avalonia.Controls;

namespace VisualRelay.App.Views.Controls.Buttons;

/// <summary>
/// A stage card in the stage board.  Automatically applies the
/// <c>"stageButton"</c> style class so the theme selectors
/// (<c>Button.stageButton</c>) match without theme changes.
/// Rich child content is provided via the existing
/// <c>DataTemplate</c> in <c>StageBoard.axaml</c>.
/// </summary>
public partial class StageCardButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public StageCardButton()
    {
        Classes.Add("stageButton");
    }
}
