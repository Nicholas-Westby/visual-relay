using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class RootFolderDisplayTests
{
    [Fact]
    public void NameAndParent_KeepTheProjectNameVisible()
    {
        var root = Path.Combine(
            Path.DirectorySeparatorChar.ToString(),
            "Users",
            "admin",
            "Dev",
            "sample-tasks");

        Assert.Equal("sample-tasks", RootFolderDisplay.Name(root));
        Assert.Equal(Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "admin", "Dev"), RootFolderDisplay.Parent(root));
    }

    [Fact]
    public void NameAndParent_ProvideEmptyStateLabels()
    {
        Assert.Equal("Choose project", RootFolderDisplay.Name(string.Empty));
        Assert.Equal("Folder", RootFolderDisplay.Parent(string.Empty));
    }
}
