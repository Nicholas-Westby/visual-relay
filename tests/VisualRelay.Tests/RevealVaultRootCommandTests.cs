using System.Runtime.InteropServices;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RevealVaultRootCommandTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(),
        "vr-reveal-cmd", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_scratch);

    /// <summary>
    /// Create a sandboxed accessor (HOME + XDG_CONFIG_HOME in scratch) so
    /// constructing the VM and toggling properties never touches the user's
    /// real ~/.config/visual-relay/.env.
    /// </summary>
    private DictionaryEnvironmentAccessor SandboxedEnv()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["HOME"] = Path.Combine(_scratch, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_scratch, "xdg")
        };
        Directory.CreateDirectory(env["HOME"]!);
        return env;
    }

    [Fact]
    public void RevealVaultRootCommand_CanExecute_WhenVaultRootIsSet()
    {
        var env = SandboxedEnv();
        var viewModel = new MainWindowViewModel(environmentAccessor: env)
        {
            ObsidianVaultRoot = "/Users/dev/obsidian-vault"
        };

        Assert.True(viewModel.RevealVaultRootCommand.CanExecute(null));
    }

    [Fact]
    public void RevealVaultRootCommand_CanExecute_EvenWhenVaultRootIsEmpty()
    {
        // The guard is in the method body (null/whitespace check), not in CanExecute.
        // The button is always clickable — it just no-ops when the root is empty.
        var env = SandboxedEnv();
        var viewModel = new MainWindowViewModel(environmentAccessor: env)
        {
            ObsidianVaultRoot = string.Empty
        };

        Assert.True(viewModel.RevealVaultRootCommand.CanExecute(null));
    }

    [Fact]
    public void VaultRoot_BuildCommand_OnMacOs_UsesOpenDashR()
    {
        const string path = "/Users/dev/obsidian-vault";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.OSX);

        Assert.Equal("open", fileName);
        Assert.Equal(new[] { "-R", path }, arguments);
    }

    [Fact]
    public void VaultRoot_BuildCommand_OnWindows_UsesExplorerSelect()
    {
        const string path = @"C:\Users\dev\obsidian-vault";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.Windows);

        Assert.Equal("explorer", fileName);
        Assert.Equal(new[] { $"/select,{path}" }, arguments);
    }

    [Fact]
    public void VaultRoot_BuildCommand_OnLinux_UsesXdgOpen()
    {
        const string path = "/home/dev/obsidian-vault";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.Linux);

        Assert.Equal("xdg-open", fileName);
        // xdg-open cannot select a file; BuildCommand opens the parent directory.
        Assert.Equal(new[] { "/home/dev" }, arguments);
    }
}
