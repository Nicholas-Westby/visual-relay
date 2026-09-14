using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>
/// Keeps a detected formatter only when the clean checkout already satisfies it. Visual Relay runs
/// formatCmd over the working tree before every guard, so a formatter the baseline fails rewrites
/// the whole project in every task's commit. Measured with litedb-org/LiteDB: bootstrap set
/// <c>dotnet format LiteDB.sln</c>, whose check exits 2 on the untouched checkout with 1257
/// whitespace findings, and set the same check as the guard the baseline gate refuses runs over.
/// </summary>
internal static class FormatBaselineCheck
{
    /// <summary>The check form of a formatter bootstrap detects, or null for one with no known check.</summary>
    internal static string? CheckFormOf(string formatCmd) => formatCmd switch
    {
        _ when formatCmd.StartsWith("dotnet format", StringComparison.Ordinal) => formatCmd + " --verify-no-changes",
        "cargo fmt" => "cargo fmt --check",
        "prettier --write ." => "prettier --check .",
        "gofmt -w ." => "test -z \"$(gofmt -l .)\"",
        "swiftformat ." => "swiftformat --lint .",
        _ => null,
    };

    /// <summary>
    /// Runs the check form of the config's formatCmd on the checkout at <paramref name="rootPath"/>.
    /// When it does not pass, removes formatCmd, and that check from guardCmd, and returns a note
    /// saying so; returns null when the formatter is kept or has no check to run.
    /// </summary>
    internal static async Task<string?> ApplyAsync(string rootPath, ITestRunner runner, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, ".relay", "config.json");
        if (!File.Exists(path)
            || JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) is not JsonObject config
            || config["formatCmd"]?.GetValueKind() != JsonValueKind.String)
            return null;

        var formatCmd = config["formatCmd"]!.GetValue<string>();
        if (CheckFormOf(formatCmd) is not { } check)
            return null;

        var result = await runner.RunAsync(rootPath, check, cancellationToken);
        if (result is { ExitCode: 0, TimedOut: false })
            return null;

        config.Remove("formatCmd");
        if (config["guardCmd"]?.GetValueKind() == JsonValueKind.String)
        {
            var remaining = config["guardCmd"]!.GetValue<string>()
                .Split(" && ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(part, check, StringComparison.Ordinal))
                .ToList();
            if (remaining.Count == 0)
                config.Remove("guardCmd");
            else
                config["guardCmd"] = string.Join(" && ", remaining);
        }

        await File.WriteAllTextAsync(path,
            config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken);
        return result.TimedOut
            ? $"formatCmd `{formatCmd}` left out: `{check}` did not finish within the setup check's time limit, "
              + "so the clean checkout was not shown to pass it; add it back once it does."
            : $"formatCmd `{formatCmd}` left out: the clean checkout does not pass `{check}` "
              + $"(exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}), so every task would reformat the whole project.";
    }
}
