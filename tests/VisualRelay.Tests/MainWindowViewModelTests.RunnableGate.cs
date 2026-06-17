using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

// EnsureRunnableAsync tool-presence gate: when a required launch tool (swival,
// plus nono when the sandbox is on) is not on PATH, the GUI must refuse up front
// with a clear StatusText that names the real cause — and the message must match
// the runner's MissingToolsMessage so both surfaces stay identical.
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task EnsureRunnableAsync_RequiredToolMissing_SetsClearStatusAndRefuses()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        // An injected accessor with an empty PATH drives MissingRequiredTools to
        // see no tools — deterministic, and no process-global env mutation (the
        // convention guard forbids mutating the real process environment in tests).
        var env = new DictionaryEnvironmentAccessor { ["PATH"] = string.Empty };
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = env };
        await viewModel.LoadInitialAsync();

        // Satisfy the HF gate so we reach the tool-presence gate.
        viewModel.IsHuggingFaceConfigured = true;

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.False(runnable);
        // Names the real cause and points at installing swival.
        Assert.Contains("swival", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        // The drifted hand-copy used to omit this sentence; both surfaces must now
        // carry the unified runner message (MissingToolsMessage).
        Assert.Contains("It's set up on the VM, not this host.", viewModel.StatusText, StringComparison.Ordinal);
        // No nono advisory red herrings leak into the gate message.
        Assert.DoesNotContain("deny_shell_configs", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", viewModel.StatusText, StringComparison.Ordinal);
    }
}
