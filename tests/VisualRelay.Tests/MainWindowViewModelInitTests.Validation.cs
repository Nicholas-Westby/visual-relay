using System.Text.Json;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// The validation-timeout and status half of the Create-config tests, split out to keep
// each file under the 300-line guard. Same class, same helpers.
public sealed partial class MainWindowViewModelInitTests
{
    /// <summary>
    /// The manual action asks the same proposer bootstrap falls back to — an agent run
    /// that reads the project — rather than the old single prompt carrying a list of
    /// the root's entry NAMES, which could not show a nested test project or a script's
    /// text.
    /// </summary>
    [Fact]
    public async Task FindTestCommand_PopulatesInputFromTheProposer()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = RunnableHost,
            TestCommandProposerFor = (_, _) => Task.FromResult<string?>("go test ./..."),
        };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Equal("go test ./...", viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task FindTestCommand_WithNoProposerAvailable_SaysSoRatherThanFailing()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, IsHuggingFaceConfigured = false, SandboxHostResolver = RunnableHost };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Contains("enter the command manually", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task CreateConfig_UsesCreateConfigValidationTimeout()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        TimeSpan? capturedTimeout = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = RunnableHost,
            InitValidationRunnerFactory = timeout =>
            {
                capturedTimeout = timeout;
                return new ScriptedTestRunner(new TestRunResult(0, "green"));
            }
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.NotNull(capturedTimeout);
        Assert.Equal(ProjectBootstrapper.CreateConfigValidationTimeout, capturedTimeout!.Value);
        Assert.False(viewModel.NeedsInitialization);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CreateConfig_SetsValidatingStatusBeforeValidation()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        string? capturedStatusText = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = RunnableHost,
        };
        viewModel.InitValidationRunnerFactory = _ =>
            new StatusCaptureTestRunner(new TestRunResult(0, "green"),
                () => capturedStatusText = viewModel.StatusText);
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.NotNull(capturedStatusText);
        Assert.Contains("Validating", capturedStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.NeedsInitialization);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CreateConfig_RejectsTimeoutAndSurfacesReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = RunnableHost,
            InitValidationRunnerFactory = _ => new TimeoutSimulatingTestRunner()
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.Contains("timed out", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.NeedsInitialization);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }
}
