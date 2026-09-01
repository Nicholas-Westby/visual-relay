using System.Text.RegularExpressions;
using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Guards every <c>./visual-relay &lt;verb&gt;</c> the repository writes down
/// against the launcher's own verb list.
/// <para>
/// The generated <c>scripts/reset-sample.sh</c> called <c>sample-reset</c> for
/// months after that verb was removed. Nothing failed, because nothing checked:
/// the script is written by a tool, shipped into another repository, and only
/// run by hand. A caller that names a verb the launcher will reject is a broken
/// caller, and this is the cheapest place to notice.
/// </para>
/// </summary>
public sealed partial class LauncherVerbReferenceTests
{
    // Anchored to a command position, so prose that merely mentions the
    // launcher ("run through ./visual-relay so the devshell provides it") is
    // not read as an invocation.
    [GeneratedRegex(@"(?:^|[;&|(]|\$\()[ \t]*\./visual-relay\s+([a-z][a-z0-9-]*)",
        RegexOptions.Multiline)]
    private static partial Regex Invocation();

    /// <summary>Files that invoke the launcher and are executed, not narrated.</summary>
    private static IEnumerable<string> Sources()
    {
        var root = RepoSetup.Root;
        foreach (var directory in new[] { "scripts", "tools", "src" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                    continue;

                var extension = Path.GetExtension(file);
                if (extension is ".sh" or ".cs") yield return file;
            }
        }
    }

    /// <summary>
    /// Every verb named in an executable invocation is one the launcher accepts.
    /// </summary>
    [Fact]
    public void EveryInvokedVerb_IsKnownToTheLauncher()
    {
        var problems = new List<string>();

        foreach (var file in Sources())
        foreach (Match match in Invocation().Matches(File.ReadAllText(file)))
        {
            var verb = match.Groups[1].Value;
            if (!CommandRouter.IsKnown(verb))
                problems.Add($"{Path.GetFileName(file)} invokes './visual-relay {verb}'");
        }

        Assert.True(problems.Count == 0,
            "these callers name a verb the launcher rejects:\n" + string.Join("\n", problems));
    }
}
