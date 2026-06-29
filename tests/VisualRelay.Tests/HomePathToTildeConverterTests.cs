using System.Globalization;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed class HomePathToTildeConverterTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConvertPath(string? path) =>
        (string?)HomePathToTildeConverter.Instance.Convert(
            path, typeof(string), null, CultureInfo.InvariantCulture) ?? string.Empty;

    [Fact]
    public void Convert_PathUnderHome_ReplacesPrefixWithTilde()
    {
        var subPath = Path.Combine(Home, "Dev", "visual-relay");
        var result = ConvertPath(subPath);
        Assert.Equal("~/Dev/visual-relay", result);
    }

    [Fact]
    public void Convert_PathEqualsHomeExactly_ReturnsTilde()
    {
        var result = ConvertPath(Home);
        Assert.Equal("~", result);
    }

    [Fact]
    public void Convert_PathUnderDifferentUserHome_ReturnsUnchanged()
    {
        // Construct a path under a different user's home to ensure the converter
        // uses the actual user profile, not a hard-coded prefix.
        var otherUserHome = Path.Combine(
            Path.GetDirectoryName(Home) ?? "/Users",
            "otheruser");
        var subPath = Path.Combine(otherUserHome, "projects", "foo");
        var result = ConvertPath(subPath);
        Assert.Equal(subPath, result);
    }

    [Fact]
    public void Convert_PathOutsideHome_ReturnsUnchanged()
    {
        var path = Path.Combine(
            Path.GetPathRoot(Home) ?? "/",
            "var", "log", "app.log");
        var result = ConvertPath(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void Convert_Null_ReturnsNull()
    {
        var result = HomePathToTildeConverter.Instance.Convert(
            null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_EmptyString_ReturnsEmptyString()
    {
        var result = ConvertPath(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_PathWithTrailingSeparator_StillReplacesPrefixWithTilde()
    {
        var subPath = Home + Path.DirectorySeparatorChar + "Documents" + Path.DirectorySeparatorChar;
        var result = ConvertPath(subPath);
        Assert.Equal("~/Documents/", result);
    }

    [Fact]
    public void Convert_PathNotStartingWithHome_ReturnsUnchanged()
    {
        var path = "/tmp/some-file.txt";
        var result = ConvertPath(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void Convert_PathCaseDiffersFromHome_ReturnsUnchanged()
    {
        // UserProfile is case-sensitive on Unix; a path that differs only in case
        // should not be treated as under home.
        if (Home.Equals(Home, StringComparison.Ordinal))
        {
            var caseDiff = Home.ToUpperInvariant() == Home
                ? Home.ToLowerInvariant()
                : Home.ToUpperInvariant();
            var subPath = Path.Combine(caseDiff, "foo");
            var result = ConvertPath(subPath);
            Assert.Equal(subPath, result);
        }
    }

    [Fact]
    public void ConvertBack_ReturnsValueUnchanged()
    {
        var input = "~/Dev/visual-relay";
        var result = HomePathToTildeConverter.Instance.ConvertBack(
            input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ConvertBack_Null_ReturnsNull()
    {
        var result = HomePathToTildeConverter.Instance.ConvertBack(
            null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_RelativePath_ReturnsUnchanged()
    {
        var path = "relative/path";
        var result = ConvertPath(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void Convert_PathThatStartsWithHomeButIsLongerDirectoryName_ReturnsUnchanged()
    {
        // e.g. /Users/nicholaswestby-extra/foo should NOT match /Users/nicholaswestby
        var lookalike = Home + "-extra" + Path.DirectorySeparatorChar + "foo";
        // Only test if the path is plausible (won't collide with an actual existing home)
        if (!Directory.Exists(Path.GetDirectoryName(lookalike)) && !Directory.Exists(lookalike))
        {
            var result = ConvertPath(lookalike);
            Assert.Equal(lookalike, result);
        }
    }

    [Fact]
    public void Convert_SingleDirectoryUnderHome_ReplacesPrefixWithTilde()
    {
        var subPath = Path.Combine(Home, "Documents");
        var result = ConvertPath(subPath);
        Assert.Equal("~/Documents", result);
    }

    [Fact]
    public void Convert_Instance_IsSingleton()
    {
        var a = HomePathToTildeConverter.Instance;
        var b = HomePathToTildeConverter.Instance;
        Assert.Same(a, b);
    }
}
