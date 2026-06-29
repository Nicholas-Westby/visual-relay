namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Interactive confirmation responder (title, message, confirm-label → true
    /// when the user confirms). The desktop app wires this to a modal dialog
    /// (App.axaml.cs); headless/tests leave it null (auto-proceed). EVERY
    /// confirm-gated command routes through <see cref="ConfirmAsync"/> rather than
    /// calling this directly, so the seam behaves consistently across commands.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ShowConfirmationAsync { get; set; }

    // Scopes "pre-confirmed" to a single async invocation flow. Set only by the
    // control API around a loopback-driven command (see InvokePreConfirmedAsync),
    // it is invisible to a concurrent human button click (a different async
    // context), so the GUI still opens the modal even while an API command runs.
    private static readonly AsyncLocal<bool> ApiAutoConfirm = new();

    /// <summary>
    /// Runs <paramref name="invoke"/> with confirmation auto-resolved — the
    /// control API's pre-confirmed path. Any confirm-gated command invoked within
    /// proceeds WITHOUT opening the interactive modal. The scope is confined to
    /// this async flow (via <see cref="AsyncLocal{T}"/>), so it never affects a
    /// concurrent human-driven invocation.
    /// </summary>
    internal async Task InvokePreConfirmedAsync(Func<Task> invoke)
    {
        var previous = ApiAutoConfirm.Value;
        ApiAutoConfirm.Value = true;
        try
        {
            await invoke();
        }
        finally
        {
            ApiAutoConfirm.Value = previous;
        }
    }

    /// <summary>
    /// Single confirmation seam shared by every confirm-gated command. Returns
    /// true when the action may proceed: auto-confirmed inside a pre-confirmed API
    /// invocation, auto-proceed when no interactive responder is wired
    /// (headless/tests), otherwise the GUI modal result (which a human can Cancel).
    /// </summary>
    private Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (ApiAutoConfirm.Value)
        {
            return Task.FromResult(true);
        }

        if (ShowConfirmationAsync is null)
        {
            return Task.FromResult(true);
        }

        return ShowConfirmationAsync(title, message, confirmLabel);
    }
}
