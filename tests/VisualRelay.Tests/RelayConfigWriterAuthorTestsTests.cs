using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Bootstrap always writes the <c>authorTests</c> object so an operator can see
/// and edit it, but a re-bootstrap must refresh only what detection owns.
/// </summary>
public sealed class RelayConfigWriterAuthorTestsTests
{
    private static TestLayoutDetection Detection(string[] languages, string[] inlineExtensions) =>
        new(languages, inlineExtensions, new Dictionary<string, int>(), 0);

    private static JsonElement Read(TestRepository repo)
    {
        var raw = File.ReadAllText(Path.Combine(repo.Root, ".relay", "config.json"));
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    [Fact]
    public void Upsert_on_a_fresh_config_writes_the_object_and_empty_test_paths()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "cargo test");

        RelayConfigWriter.UpsertAuthorTests(repo.Root, Detection(["rust"], [".rs"]));

        var root = Read(repo);
        var authorTests = root.GetProperty("authorTests");
        Assert.Equal(["rust"], authorTests.GetProperty("detectedLanguages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([".rs"], authorTests.GetProperty("inlineTestExtensions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("auto", authorTests.GetProperty("diffAudit").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("testPaths").ValueKind);
        Assert.Empty(root.GetProperty("testPaths").EnumerateArray());
    }

    [Fact]
    public void Second_upsert_overwrites_detection_but_keeps_the_operator_settings()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "cargo test");
        RelayConfigWriter.UpsertAuthorTests(repo.Root, Detection(["go"], []));

        // The operator turns the audit off and names their own test globs.
        var path = Path.Combine(repo.Root, ".relay", "config.json");
        var edited = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edited["authorTests"]!["diffAudit"] = "off";
        edited["testPaths"] = new System.Text.Json.Nodes.JsonArray("spec/**");
        File.WriteAllText(path, edited.ToJsonString());

        RelayConfigWriter.UpsertAuthorTests(repo.Root, Detection(["rust", "python"], [".rs"]));

        var root = Read(repo);
        var authorTests = root.GetProperty("authorTests");
        Assert.Equal(["rust", "python"], authorTests.GetProperty("detectedLanguages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([".rs"], authorTests.GetProperty("inlineTestExtensions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("off", authorTests.GetProperty("diffAudit").GetString());
        Assert.Equal(["spec/**"], root.GetProperty("testPaths").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task What_the_writer_writes_the_loader_reads_back_equal()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "cargo test");

        RelayConfigWriter.UpsertAuthorTests(repo.Root, Detection(["rust", "python"], [".rs"]));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(
            new AuthorTestsConfig(["rust", "python"], [".rs"], AuthorTestsConfig.DiffAuditAuto),
            result.Config.AuthorTests);
        Assert.Empty(result.Config.TestPaths ?? []);
    }

    [Fact]
    public void Upsert_without_a_config_file_still_produces_the_object()
    {
        using var repo = TestRepository.Create();

        RelayConfigWriter.UpsertAuthorTests(repo.Root, Detection([], []));

        var authorTests = Read(repo).GetProperty("authorTests");
        Assert.Empty(authorTests.GetProperty("detectedLanguages").EnumerateArray());
        Assert.Empty(authorTests.GetProperty("inlineTestExtensions").EnumerateArray());
        Assert.Equal("auto", authorTests.GetProperty("diffAudit").GetString());
    }
}
