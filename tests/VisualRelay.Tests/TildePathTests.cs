using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class TildePathTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Expand_TildeSlashPrefix_ReplacesWithHome()
    {
        var result = TildePath.Expand("~/Documents/project");

        Assert.Equal(Path.Combine(Home, "Documents/project"), result);
    }

    [Fact]
    public void Expand_AbsolutePath_ReturnsUnchanged()
    {
        var result = TildePath.Expand("/usr/local/bin");

        Assert.Equal("/usr/local/bin", result);
    }

    [Fact]
    public void Expand_PlainRelative_ReturnsUnchanged()
    {
        var result = TildePath.Expand("subdir/file.txt");

        Assert.Equal("subdir/file.txt", result);
    }

    [Fact]
    public void Expand_BareTilde_ReturnsUnchanged()
    {
        // Bare "~" is NOT expanded — only "~/" prefix triggers expansion.
        var result = TildePath.Expand("~");

        Assert.Equal("~", result);
    }

    [Fact]
    public void Expand_TildeUserPrefix_ReturnsUnchanged()
    {
        // "~user/..." is NOT expanded — only "~/" is supported.
        var result = TildePath.Expand("~otheruser/documents");

        Assert.Equal("~otheruser/documents", result);
    }

    [Fact]
    public void Expand_TwoArg_NullHome_ReturnsInputVerbatim()
    {
        var result = TildePath.Expand("~/some/path", home: null);

        Assert.Equal("~/some/path", result);
    }

    [Fact]
    public void Expand_TwoArg_EmptyHome_ReturnsUnchanged()
    {
        var result = TildePath.Expand("~/some/path", home: "");

        // Empty/whitespace home → no expansion, returns the original string.
        Assert.Equal("~/some/path", result);
    }

    [Fact]
    public void Expand_ParameterlessOverload_UsesHOMEEnv()
    {
        // The parameterless overload resolves HOME from the environment.
        var result = TildePath.Expand("~/projects/test");

        Assert.Equal(Path.Combine(Home, "projects/test"), result);
    }
}
