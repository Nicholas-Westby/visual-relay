using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Bootstrap leaves the detected test layout in <c>.relay/config.json</c>, so the
/// operator can see why Stage 5 will gate their files the way it does.
/// </summary>
public sealed class ProjectBootstrapperTestLayoutTests
{
    private static async Task<ProjectBootstrapResult> BootstrapAsync(
        TestRepository repo, GitSimEngine sim, params (string Path, string Content)[] tracked)
    {
        sim.InitRepo(repo.Root);
        foreach (var (path, content) in tracked)
            sim.Seed(repo.Root, path, content);
        sim.Commit(repo.Root, "chore: seed repo");

        return await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new ScriptedTestRunner(new TestRunResult(0, "ok")));
    }

    private static JsonElement AuthorTests(TestRepository repo)
    {
        var raw = File.ReadAllText(Path.Combine(repo.Root, ".relay", "config.json"));
        return JsonDocument.Parse(raw).RootElement.GetProperty("authorTests").Clone();
    }

    [Fact]
    public async Task A_rust_repo_is_bootstrapped_inline_capable()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();

        var result = await BootstrapAsync(repo, sim,
            ("Cargo.toml", "[package]\nname = \"colored\"\n"),
            ("src/lib.rs", "pub fn f() {}\n"));

        var authorTests = AuthorTests(repo);
        Assert.Equal([".rs"], authorTests.GetProperty("inlineTestExtensions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["rust"], authorTests.GetProperty("detectedLanguages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([".rs"], result.TestLayout?.InlineTestExtensions);
    }

    [Fact]
    public async Task A_go_repo_keeps_the_path_gate()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();

        await BootstrapAsync(repo, sim,
            ("go.mod", "module example.com/mux\n"),
            ("mux.go", "package mux\n"),
            ("route.go", "package mux\n"));

        var authorTests = AuthorTests(repo);
        Assert.Empty(authorTests.GetProperty("inlineTestExtensions").EnumerateArray());
        Assert.Equal(["go"], authorTests.GetProperty("detectedLanguages").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task An_empty_folder_still_gets_the_object_and_loads()
    {
        using var repo = TestRepository.Create();

        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: new GitSimEngine());

        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        Assert.Equal(AuthorTestsConfig.Default, loaded.Config.AuthorTests);
        Assert.Equal("auto", AuthorTests(repo).GetProperty("diffAudit").GetString());
    }
}
