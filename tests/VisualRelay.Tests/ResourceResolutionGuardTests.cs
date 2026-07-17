using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless;

namespace VisualRelay.Tests;

/// <summary>
/// Guards against unresolvable {DynamicResource …} keys that produce invisible
/// text.  Scans every .axaml file under src/VisualRelay.App, collects every
/// DynamicResource key, and asserts each resolves (non-null) against the
/// running app's merged resources.  Fails today on the three
/// ThemeForegroundBrush references in TopBar.axaml.
/// </summary>
[Collection("Headless")]
public sealed class ResourceResolutionGuardTests
{
    private static readonly Regex DynamicResourcePattern = new(
        @"\{DynamicResource\s+(\w+)\}",
        RegexOptions.Compiled);

    [AvaloniaFact]
    public void EveryDynamicResourceKey_Resolves()
    {
        var axamlDir = Path.Combine(RepoSetup.Root, "src", "VisualRelay.App");
        Assert.True(Directory.Exists(axamlDir),
            $"App source directory not found: {axamlDir}");

        var unresolved = new List<(string File, int Line, string Key)>();
        var totalKeys = 0;

        foreach (var file in Directory.EnumerateFiles(
            axamlDir, "*.axaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = DynamicResourcePattern.Match(lines[i]);
                if (!m.Success) continue;

                totalKeys++;
                var key = m.Groups[1].Value;
                if (!Application.Current!.TryGetResource(key, null, out _))
                    unresolved.Add((file, i + 1, key));
            }
        }

        // Guard: the scan must actually find DynamicResource references.
        // An empty scan (zero keys) means the pattern or file layout changed
        // and the guard is no longer testing anything real.
        Assert.True(totalKeys > 0,
            "No DynamicResource references found in any .axaml file — " +
            "the guard is not exercising any keys. Has the pattern or " +
            "source layout changed?");

        if (unresolved.Count > 0)
        {
            Assert.Fail(
                $"Unresolvable DynamicResource keys found:\n" +
                string.Join("\n", unresolved.Select(u =>
                    $"  {Path.GetFileName(u.File)}:{u.Line} → '{u.Key}'")));
        }
    }
}
