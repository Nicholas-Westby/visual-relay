using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// What the Browse button does with the folder the operator picked. A folder
/// inside a WSL distro (<c>\\wsl$</c> or <c>\\wsl.localhost</c>) is kept as the
/// app's root in its UNC form, which .NET IO and the git routing both understand;
/// a Windows drive folder picked on Windows would be a DrvFs workspace inside the
/// distro, so it gets the workspace policy's answer. The decision is a pure helper
/// the view model calls; only its wiring needs a view model.
/// </summary>
public sealed class FolderPickerWslTranslationTests
{
    [Theory]
    [InlineData(@"\\wsl$\VrNoSuchDistro\home\alice\my repo")]
    [InlineData(@"\\wsl.localhost\VrNoSuchDistro\home\alice\répo")]
    public void Decide_UncPick_IsKeptAsTheUncRoot(string picked)
    {
        var pick = FolderPickerWslTranslation.Decide(picked, isWindows: true);

        Assert.Equal(picked, pick.Root);
        Assert.Null(pick.Message);
    }

    [Fact]
    public void Decide_DrivePick_OnWindows_IsRefusedWithTheMntPolicyMessage()
    {
        var pick = FolderPickerWslTranslation.Decide(@"C:\Users\alice\repo", isWindows: true);

        Assert.Null(pick.Root);
        var (_, policyMessage) = WslWorkspacePolicy.Decide("/mnt/c/Users/alice/repo");
        Assert.Equal(policyMessage, pick.Message);
        Assert.Contains("/mnt/c/Users/alice/repo", pick.Message);
    }

    [Fact]
    public void Decide_UncPickUnderADrvFsMount_IsRefusedToo()
    {
        var pick = FolderPickerWslTranslation.Decide(@"\\wsl.localhost\VrNoSuchDistro\mnt\c\Users\alice\repo", isWindows: true);

        Assert.Null(pick.Root);
        Assert.Contains("DrvFs", pick.Message);
    }

    [Theory]
    [InlineData("/Users/alice/repo", false)]
    [InlineData(@"C:\Users\alice\repo", false)]
    [InlineData(@"\\server\share\repo", false)]
    public void Decide_AnyOtherPick_IsAcceptedUnchanged(string picked, bool isWindows)
    {
        // A drive-looking path or a share name off Windows is just a name: there is
        // no distro to translate it for, and the local sandbox reads it directly.
        var pick = FolderPickerWslTranslation.Decide(picked, isWindows);

        Assert.Equal(picked, pick.Root);
        Assert.Null(pick.Message);
    }

    /// <summary>
    /// On Windows every sandboxed run goes through the distro, so a share that is
    /// not one cannot be a workspace. Refusing it at the pick is the only place the
    /// operator learns why; accepting it deferred the same failure to the first run.
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"\\127.0.0.1\c$\repo")]
    public void Decide_AForeignSharePick_OnWindows_IsRefused(string picked)
    {
        var pick = FolderPickerWslTranslation.Decide(picked, isWindows: true);

        Assert.Null(pick.Root);
        Assert.Contains(@"\\wsl.localhost", pick.Message!, StringComparison.Ordinal);
        Assert.Contains(@"\\wsl$", pick.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_RefusedPick_KeepsTheRootAndExplainsInTheStatus()
    {
        var viewModel = new MainWindowViewModel();
        var rootBefore = viewModel.RootPath;
        viewModel.UseFolderPicker(new FixedFolderPicker(@"\\wsl.localhost\VrNoSuchDistro\mnt\c\Users\alice\repo"));

        await viewModel.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(rootBefore, viewModel.RootPath);
        Assert.Contains("DrvFs", viewModel.StatusText);
    }

    private sealed class FixedFolderPicker(string path) : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(path);
    }
}
