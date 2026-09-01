using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// Reads and refreshes the goldened request bodies under
/// <c>tests/VisualRelay.Tests/Goldens/request/&lt;model&gt;/&lt;stage&gt;.json</c>.
/// <para>
/// A golden here is the exact bytes a provider will be sent. That matters
/// because the layer this replaced ran with <c>drop_params: true</c>, silently
/// stripping parameters providers reject — so a body could look right in the
/// config and never reach the wire in that shape. Goldening the serialized body
/// makes a change to it visible in review instead of at runtime.
/// </para>
/// </summary>
internal static class RequestGolden
{
    /// <summary>Set to <c>1</c> to rewrite goldens instead of asserting them.</summary>
    internal const string UpdateEnvVar = "VR_UPDATE_GOLDENS";

    /// <summary>Whether this run rewrites goldens rather than checking them.</summary>
    private static bool Updating =>
        string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal);

    /// <summary>The goldens directory in the source tree, not the output copy.</summary>
    private static string Root =>
        Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests", "Goldens", "request");

    /// <summary>The path a model-and-stage golden lives at.</summary>
    /// <param name="model">The catalog alias.</param>
    /// <param name="stage">The stage name, lower-cased.</param>
    /// <returns>The absolute path.</returns>
    public static string PathFor(string model, string stage) =>
        Path.Combine(Root, model, stage + ".json");

    /// <summary>
    /// Asserts a body matches its golden, or rewrites it when updating.
    /// </summary>
    /// <param name="model">The catalog alias.</param>
    /// <param name="stage">The stage name.</param>
    /// <param name="body">The serialized request body.</param>
    public static void AssertOrUpdate(string model, string stage, string body)
    {
        var path = PathFor(model, stage);
        var pretty = Prettify(body);

        if (Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pretty);
            return;
        }

        Assert.True(File.Exists(path),
            $"no golden at {path}. Run with {UpdateEnvVar}=1 to write it, and pair it with a "
            + "live run proving the provider accepts this body.");

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), pretty.ReplaceLineEndings("\n"));
    }

    /// <summary>Every golden currently on disk, as model-and-stage pairs.</summary>
    /// <returns>One entry per golden file.</returns>
    public static IEnumerable<(string Model, string Stage, string Body)> All()
    {
        if (!Directory.Exists(Root)) yield break;

        foreach (var modelDir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
            foreach (var file in Directory.EnumerateFiles(modelDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return (
                    Path.GetFileName(modelDir),
                    Path.GetFileNameWithoutExtension(file),
                    File.ReadAllText(file));
    }

    private static string Prettify(string json) =>
        JsonSerializer.Serialize(
            JsonDocument.Parse(json).RootElement,
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
