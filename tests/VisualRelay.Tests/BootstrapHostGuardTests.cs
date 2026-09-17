namespace VisualRelay.Tests;

/// <summary>
/// Bootstrap's host parameter defaults to the LOCAL host, not this machine's, so a
/// test that says nothing about where it runs behaves the same on every platform.
/// That default is only safe while every production caller passes the resolved host:
/// without it, a Windows box with no usable distro would check the operator's own
/// test command on the Windows host, unsandboxed, and persist a command the pipeline
/// could never run. This guard is what makes the default safe.
/// </summary>
public sealed class BootstrapHostGuardTests
{
    private static string RepoRoot => RepoSetup.Root;

    [Theory]
    [InlineData("src/VisualRelay.App/ViewModels/MainWindowViewModel.Bootstrap.cs", "BootstrapAsync(")]
    [InlineData("tools/VisualRelay.Init/Program.cs", "BootstrapAsync(")]
    [InlineData("src/VisualRelay.App/ViewModels/MainWindowViewModel.RunnableGate.cs", "TryUpgradePlaceholderTestCommandAsync(")]
    [InlineData("src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs", "CreateValidationRunner(")]
    public void EveryProductionCaller_PassesTheResolvedHost(string relativePath, string call)
    {
        var path = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing: {path}");
        var source = File.ReadAllText(path);

        var at = source.IndexOf(call, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{relativePath} no longer calls {call}");

        // The argument list, to its matching close paren: the host has to be IN the
        // call, not merely somewhere in the file.
        var arguments = ArgumentsOf(source, at + call.Length);
        Assert.Contains("host", arguments, StringComparison.Ordinal);
    }

    private static string ArgumentsOf(string source, int start)
    {
        var depth = 1;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[start..i];
        }

        return source[start..];
    }
}
