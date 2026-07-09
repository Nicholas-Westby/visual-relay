using System.Text.RegularExpressions;

namespace VisualRelay.Tests;

/// <summary>
/// Convention guard (harness idiom): a headless UI test constructs the specific
/// panel/control under test, not the whole app. So <c>new MainWindow</c> in
/// tests/VisualRelay.Tests/** is flagged unless the class is on the justified
/// allowlist below — either a genuine whole-app-wiring fact (cog→modal plumbing,
/// the live control server, cross-panel affordances) or a pre-existing panel-local
/// test not yet scoped down (a follow-up candidate). New UI tests scope down: see
/// AGENTS.md testing notes and the in-tree patterns SettingsTestHelpers.
/// ShowScopedSettings and ActivityColumnTabsUiTests.ShowActivityColumn.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    /// <summary>Classes allowed to boot the whole MainWindow, each with a one-line reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> WholeAppBootAllowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── genuine whole-app wiring ──
            ["ChevronAffordanceRenderTests"] = "cross-panel chevron/focus affordances span the whole window",
            ["ControlApiTests"] = "drives the live in-process control API against a booted app",
            ["ControlApiConfirmGatedTests"] = "control API confirm-gating exercised against the live app",
            ["ControlApiTabSelectionTests"] = "control API tab selection exercised against the live app",
            ["ControlServerTests"] = "control server routes exercised against a booted app",
            ["ControlServerKestrelTests"] = "kestrel control server exercised against a booted app",
            ["ControlServerKestrelHandlerTests"] = "kestrel control handler exercised against a booted app",
            ["ControlIndexPageTests"] = "control index page served by the live app",
            ["SettingsPanelUiTests"] = "CogOpensSettingsPanel verifies the cog→modal open/close wiring",
            ["SettingsModalUiTests"] = "cog opens the owned settings modal; whole-app dialog plumbing",
            ["ActivityColumnTabsUiTests"] = "residual rail-sibling and measured-accordion facts need the window",
            ["ConfigInitEmptyStateUiTests"] = "guided-init empty-state flow spans the whole window shell",
            ["StatusFooterFlyoutTests"] = "status footer flyout lives in the app shell",
            ["RefreshButtonDuringRunTests"] = "top-bar refresh during an active run needs the whole shell",
            ["KeySetupPanelUiTests"] = "key setup reached through the top bar",
            // ── pre-existing panel-local tests; scope-down candidates (follow-up) ──
            ["ActivityColumnItemsPanelTests"] = "pre-existing panel-local test; scope-down candidate",
            ["ActivityColumnTitleLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["ActivitySplitterAffordanceTests"] = "pre-existing panel-local test; scope-down candidate",
            ["CollapseAffordanceTests"] = "pre-existing panel-local test; scope-down candidate",
            ["AttachmentImageDisplayTests"] = "pre-existing panel-local test; scope-down candidate",
            ["AttachmentImageDisplayPropertiesTests"] = "pre-existing panel-local test; scope-down candidate",
            ["HfGateHintLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["InitPanelButtonsLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["QueuePanelTitleLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["StageCardMetricsLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskActionBarLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskCardRenderTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskDetailAttachmentRevealButtonLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskDetailMarkdownTitleDeduplicationTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskDetailRemoveButtonLayoutTests"] = "pre-existing panel-local test; scope-down candidate",
            ["TaskDetailScrollBottomReachabilityTests"] = "pre-existing panel-local test; scope-down candidate",
        };

    // Matches `new MainWindow` but not `new MainWindowViewModel` — the word
    // boundary after the type name already excludes the longer identifier.
    private static readonly Regex NewMainWindowPattern =
        new(@"\bnew\s+MainWindow\b", RegexOptions.Compiled);

    /// <summary>Class key for a test file: its name up to the first '.' (partials share it).</summary>
    private static string WholeAppClassKey(string path)
    {
        var stem = Path.GetFileName(path);
        var dot = stem.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? stem : stem[..dot];
    }

    /// <summary>
    /// Pure matcher: given (path, source) pairs, returns offenders — files that
    /// construct <c>new MainWindow</c> in code whose class is not allowlisted.
    /// Line comments are stripped so prose mentioning the type never trips it.
    /// </summary>
    internal static IReadOnlyList<string> FindWholeAppBootOffenders(
        IEnumerable<(string Path, string Source)> files)
    {
        var offenders = new List<string>();
        foreach (var (path, source) in files)
        {
            // This guard's own fixtures contain the pattern as string literals.
            if (Path.GetFileName(path) == "SplitGuardVerificationTests.WholeAppBoot.cs") continue;

            var key = WholeAppClassKey(path);
            if (WholeAppBootAllowlist.ContainsKey(key)) continue;

            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var comment = lines[i].IndexOf("//", StringComparison.Ordinal);
                var code = comment >= 0 ? lines[i][..comment] : lines[i];
                if (NewMainWindowPattern.IsMatch(code))
                    offenders.Add($"{path}:{i + 1}: new MainWindow in {key} (not on the whole-app allowlist)");
            }
        }
        return offenders;
    }

    /// <summary>Bite proof: a non-allowlisted class constructing MainWindow is flagged.</summary>
    [Fact]
    public void WholeAppBootGuard_FlagsNonAllowlistedMainWindow()
    {
        var offenders = FindWholeAppBootOffenders(
            [("tests/VisualRelay.Tests/BrandNewPanelTests.cs",
              "class BrandNewPanelTests { void M() { var w = new MainWindow(); } }")]);
        Assert.NotEmpty(offenders);
    }

    /// <summary>An allowlisted class is not flagged.</summary>
    [Fact]
    public void WholeAppBootGuard_AllowsListedClass()
    {
        var offenders = FindWholeAppBootOffenders(
            [("tests/VisualRelay.Tests/ControlApiTests.cs",
              "class ControlApiTests { void M() { var w = new MainWindow(); } }")]);
        Assert.Empty(offenders);
    }

    /// <summary>No false positive on the view-model type or on a comment.</summary>
    [Fact]
    public void WholeAppBootGuard_IgnoresViewModelAndComments()
    {
        var offenders = FindWholeAppBootOffenders(
            [("tests/VisualRelay.Tests/FooTests.cs",
              "class FooTests { // boots a new MainWindow once\n void M() { var vm = new MainWindowViewModel(); } }")]);
        Assert.Empty(offenders);
    }

    /// <summary>Live gate: no test file constructs MainWindow outside the allowlist.</summary>
    [Fact]
    public void NoTestFile_BootsWholeAppOutsideAllowlist()
    {
        var files = Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

        var offenders = FindWholeAppBootOffenders(files);

        Assert.True(offenders.Count == 0,
            "new UI tests must instantiate the specific panel under test, not the whole app "
            + "(see AGENTS.md). Scope the fact down, or add the class to WholeAppBootAllowlist "
            + "with a one-line justification:\n" + string.Join("\n", offenders));
    }
}
