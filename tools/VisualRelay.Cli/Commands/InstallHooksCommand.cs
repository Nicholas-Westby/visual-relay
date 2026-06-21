using VisualRelay.Core.Execution;

namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>install-hooks</c>: wires <c>core.hooksPath</c> to <c>.githooks</c>, makes the
/// hooks executable, and PUBLISHES the commit-message validator into
/// <c>check-commit-message/</c> so the active strict commit-msg hook execs a
/// prebuilt binary (never a build at commit time, important under the sandbox).
/// Git is routed through <see cref="IGitInvoker"/>.
/// </summary>
public static class InstallHooksCommand
{
    public static async Task<int> RunAsync(RepoPaths paths, IGitInvoker git)
    {
        var (exit, output, _) = await git.RunAsync(
            paths.Root, ["config", "core.hooksPath", paths.GitHooksDir], CancellationToken.None);
        if (exit != 0)
        {
            Console.Error.WriteLine($"install-hooks: git config core.hooksPath failed:\n{output}");
            return exit;
        }

        MakeExecutable(Path.Combine(paths.GitHooksDir, "commit-msg"));
        MakeExecutable(Path.Combine(paths.GitHooksDir, "pre-commit"));
        MakeExecutable(Path.Combine(paths.Root, "visual-relay"));

        // Publish the commit-message validator so the commit-msg hook execs a
        // prebuilt self-contained binary. Skip gracefully if dotnet is absent —
        // the hook then falls back to `dotnet run` on dev machines.
        if (ProcessLauncher.OnPath("dotnet"))
        {
            Console.Error.WriteLine("Publishing the commit-message validator…");
            var rc = ProcessLauncher.Run(ProcessLauncher.Dotnet,
                [
                    "publish", paths.ToolProject("VisualRelay.CheckCommitMessage"),
                    "-c", "Release", "--self-contained", "-p:PublishSingleFile=true",
                    "-o", paths.CheckCommitMessageOut, "-m:1", "-p:UseSharedCompilation=false",
                ],
                paths.Root);
            if (rc != 0)
                return rc;
            MakeExecutable(Path.Combine(paths.CheckCommitMessageOut, "VisualRelay.CheckCommitMessage"));
        }
        else
        {
            Console.Error.WriteLine(
                "dotnet not found — skipping validator publish; commit-msg will use 'dotnet run'.");
        }

        return 0;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // best-effort chmod
        }
    }
}
