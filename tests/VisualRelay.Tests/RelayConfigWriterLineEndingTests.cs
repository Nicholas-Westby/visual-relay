using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// <c>.relay/config.json</c> can be tracked: VR's own repository tracks it, and the
/// <c>.relay/.gitignore</c> VR writes into every target un-ignores it. So a rewrite has
/// to keep the line endings git checked it out with. Git reports a rewritten file as
/// modified when its size changes, even when the diff is empty, so either ending is
/// wrong somewhere: with <c>eol=lf</c> the checkout is LF and a CRLF rewrite shows as
/// modified, and with <c>core.autocrlf=true</c> and no attributes (the Git for Windows
/// default) the checkout is CRLF and an LF rewrite does. Both measured 2026-09-18.
/// <para>
/// The assertions are on bytes and every fixture states its endings, so a writer that
/// used the platform's newline fails here on every platform, not only on Windows.
/// </para>
/// </summary>
public sealed class RelayConfigWriterLineEndingTests
{
    // The format check rewrites the config only when the formatter fails on a clean
    // checkout, so the config names one whose check the scripted runner then fails.
    private const string LfConfig =
        "{\n  \"testCmd\": \"go test ./...\",\n  \"logSources\": [],\n  \"formatCmd\": \"gofmt -w .\"\n}\n";

    public static TheoryData<string> Writers => new(WriteThrough.Keys);

    private static readonly Dictionary<string, Func<string, Task>> WriteThrough = new()
    {
        ["UpsertResolvedToolchain"] = root => Sync(() => RelayConfigWriter.UpsertResolvedToolchain(root, "go test -count=1 ./...")),
        ["UpsertSubagentTimeout"] = root => Sync(() => RelayConfigWriter.UpsertSubagentTimeout(root, 1_800_000)),
        ["UpsertTestTimeout"] = root => Sync(() => RelayConfigWriter.UpsertTestTimeout(root, 600_000)),
        ["SetTurnBoost"] = root => Sync(() => RelayConfigWriter.SetTurnBoost(root, "some-task", true)),
        ["SetSkipTests"] = root => Sync(() => RelayConfigWriter.SetSkipTests(root, "some-task", true)),
        ["UpsertTierModelOverrides"] = root => Sync(() => RelayConfigWriter.UpsertTierModelOverrides(
            root, new Dictionary<string, string> { ["cheap"] = "some-model" })),
        ["UpsertAuthorTests"] = root => Sync(() => RelayConfigWriter.UpsertAuthorTests(
            root, new TestLayoutDetection(["go"], [], new Dictionary<string, int>(), 0))),
        ["UpsertTasksDir"] = root => Sync(() => RelayConfigWriter.UpsertTasksDir(root, "tasks")),
        ["FormatBaselineCheck"] = root => FormatBaselineCheck.ApplyAsync(
            root, new ScriptedTestRunner(new TestRunResult(1, "main.go")), TestContext.Current.CancellationToken),
    };

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task ARewrite_KeepsTheCrlfOfACrlfCheckout(string writer)
    {
        using var repo = TestRepository.Create();
        var path = SeedConfig(repo.Root, LfConfig.Replace("\n", "\r\n", StringComparison.Ordinal));

        await WriteThrough[writer](repo.Root);

        var bytes = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\r\n", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", bytes.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task ARewrite_KeepsTheLfOfAnLfCheckout(string writer)
    {
        using var repo = TestRepository.Create();
        var path = SeedConfig(repo.Root, LfConfig);

        await WriteThrough[writer](repo.Root);

        Assert.DoesNotContain("\r", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>A config that did not exist has no checkout to match, so it is written LF, as git stores it.</summary>
    [Fact]
    public async Task ANewConfig_IsLf()
    {
        using var repo = TestRepository.Create();

        var path = RelayConfigWriter.Write(repo.Root, "go test ./...");

        var bytes = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.EndsWith("}\n", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", bytes, StringComparison.Ordinal);
    }

    private static string SeedConfig(string root, string text)
    {
        var relay = Path.Combine(root, ".relay");
        Directory.CreateDirectory(relay);
        var path = Path.Combine(relay, "config.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static Task Sync(Action write)
    {
        write();
        return Task.CompletedTask;
    }
}
