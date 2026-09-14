using System.Text.Json;

namespace VisualRelay.Core.Init;

public static partial class TestCommandDetector
{
    private static readonly string[] PhpunitConfigs = ["phpunit.xml", "phpunit.xml.dist", "phpunit.dist.xml"];

    /// <summary>
    /// PHPUnit candidates for a Composer project: its <c>test</c> script, then its <c>phpunit</c>
    /// script, then <c>vendor/bin/phpunit</c> when a PHPUnit config names the suite. Found on the
    /// Windows arm with FreshRSS: no candidate at all, so bootstrap left the placeholder.
    /// </summary>
    private static void AddPhpCandidates(string rootPath, List<string> candidates)
    {
        if (!File.Exists(Path.Combine(rootPath, "composer.json")))
            return;

        var scripts = ReadComposerScriptNames(rootPath);
        if (scripts.Contains("test"))
            candidates.Add("composer test");
        if (scripts.Contains("phpunit"))
            candidates.Add("composer run-script phpunit");
        if (PhpunitConfigs.Any(name => File.Exists(Path.Combine(rootPath, name))))
            candidates.Add("vendor/bin/phpunit");
    }

    private static HashSet<string> ReadComposerScriptNames(string rootPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath, "composer.json")));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("scripts", out var scripts)
                   && scripts.ValueKind == JsonValueKind.Object
                ? scripts.EnumerateObject().Select(script => script.Name).ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
