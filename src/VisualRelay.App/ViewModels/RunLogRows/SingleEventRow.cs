using System.Windows.Input;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels.RunLogRows;

/// <summary>
/// Wraps a single <see cref="RelayEvent"/> as a Run Log row.
/// Delegates <see cref="DisplayLine"/>, <see cref="DetailLine"/>,
/// and <see cref="IsAttention"/> straight through.
/// </summary>
public sealed class SingleEventRow(RelayEvent relayEvent) : IRunLogRow
{
    private static readonly ICommand NoOpCommand = new NoOpCommand(() => { });

    public string DisplayLine => Event.DisplayLine;
    public string DetailLine => Event.DetailLine;
    public bool IsAttention => Event.IsAttention;
    public bool IsGroup => false;
    public int Count => 1;
    public RelayEvent Event { get; } = relayEvent;
    public IReadOnlyList<RelayEvent> Members { get; } = [relayEvent];
    public bool IsExpanded
    {
        get => false;
        set { /* no-op */ }
    }
    public ICommand ToggleExpandCommand => NoOpCommand;
}

/// <summary>
/// Minimal <see cref="ICommand"/> whose <c>Execute</c> is a no-op.
/// Used by <see cref="SingleEventRow"/> for the required
/// <see cref="IRunLogRow.ToggleExpandCommand"/>.
/// </summary>
internal sealed class NoOpCommand(Action execute) : ICommand
{
#pragma warning disable CS0067 // event never used — required by ICommand
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
