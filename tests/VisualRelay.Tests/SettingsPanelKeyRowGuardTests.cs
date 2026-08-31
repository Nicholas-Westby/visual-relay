using System.Text.RegularExpressions;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test proving the settings panel renders exactly one row per entry in
/// <see cref="MainWindowViewModel.AllProviderKeys"/>. The panel hardcodes its rows
/// rather than templating them, so each row binds <c>KeyStates[i]</c> at a literal
/// index and nothing structural ties the two together.
/// <para>
/// This gap shipped a real defect: a provider was once inserted into
/// <c>AllProviderKeys</c> without a matching row, leaving the last key with no UI
/// to paste it into and every row comment naming the wrong provider. The existing
/// <c>ProviderKeyNames_MatchTheSettingsPanelRows</c> compares two C# lists and
/// cannot see the markup, so it stayed green throughout.
/// </para>
/// </summary>
public sealed partial class SettingsPanelKeyRowGuardTests
{
    private static string PanelPath => Path.Combine(
        RepoSetup.Root, "src", "VisualRelay.App", "Views", "Controls", "SettingsPanel.axaml");

    [GeneratedRegex(@"KeyStates\[(\d+)\]")]
    private static partial Regex KeyStateIndex();

    /// <summary>
    /// Pure matcher: returns one problem string per index that the markup binds
    /// but the view-model does not supply, or that the view-model supplies but
    /// the markup never binds. Empty means the two are in step.
    /// </summary>
    private static IReadOnlyList<string> FindKeyRowProblems(string axaml, int providerCount)
    {
        var bound = KeyStateIndex().Matches(axaml)
            .Select(m => int.Parse(m.Groups[1].ValueSpan))
            .ToHashSet();

        var problems = new List<string>();
        foreach (var i in bound.Where(i => i >= providerCount).Order())
            problems.Add($"markup binds KeyStates[{i}] but only {providerCount} provider key(s) exist");
        for (var i = 0; i < providerCount; i++)
            if (!bound.Contains(i))
                problems.Add($"provider key at index {i} has no row in the markup");
        return problems;
    }

    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>
    /// Teeth: a provider added to the view-model without a matching row is
    /// flagged. This is the exact shape of the defect that shipped.
    /// </summary>
    [Fact]
    public void ProviderWithoutRow_IsFlagged()
    {
        const string axaml = """
            <TextBlock Text="{Binding KeyStates[0].DisplayValue}"/>
            <TextBlock Text="{Binding KeyStates[1].DisplayValue}"/>
            """;

        var problems = FindKeyRowProblems(axaml, providerCount: 3);

        var problem = Assert.Single(problems);
        Assert.Equal("provider key at index 2 has no row in the markup", problem);
    }

    /// <summary>
    /// Teeth the other way: a row left behind after its provider was removed
    /// binds past the end of the collection and is flagged.
    /// </summary>
    [Fact]
    public void RowWithoutProvider_IsFlagged()
    {
        const string axaml = """
            <TextBlock Text="{Binding KeyStates[0].DisplayValue}"/>
            <TextBlock Text="{Binding KeyStates[1].DisplayValue}"/>
            <TextBlock Text="{Binding KeyStates[2].DisplayValue}"/>
            """;

        var problems = FindKeyRowProblems(axaml, providerCount: 2);

        var problem = Assert.Single(problems);
        Assert.Equal("markup binds KeyStates[2] but only 2 provider key(s) exist", problem);
    }

    /// <summary>
    /// A gap in the middle is flagged even though the row count matches, which a
    /// bare count comparison would miss. It reports from both ends: the row that
    /// binds past the collection, and the provider key left with no row.
    /// </summary>
    [Fact]
    public void GapInIndices_IsFlagged()
    {
        const string axaml = """
            <TextBlock Text="{Binding KeyStates[0].DisplayValue}"/>
            <TextBlock Text="{Binding KeyStates[2].DisplayValue}"/>
            """;

        var problems = FindKeyRowProblems(axaml, providerCount: 2);

        Assert.Equal(
            [
                "markup binds KeyStates[2] but only 2 provider key(s) exist",
                "provider key at index 1 has no row in the markup",
            ],
            problems);
    }

    /// <summary>
    /// No false positive: one row per provider, and a row binding the same index
    /// many times (each row binds it eight times) reports nothing.
    /// </summary>
    [Fact]
    public void OneRowPerProvider_ReportsZero()
    {
        const string axaml = """
            <Ellipse IsVisible="{Binding KeyStates[0].IsSet}"/>
            <TextBlock Text="{Binding KeyStates[0].Row.DisplayName}"/>
            <TextBox Text="{Binding KeyStates[1].PendingValue, Mode=TwoWay}"/>
            <Button CommandParameter="{Binding KeyStates[1]}"/>
            """;

        Assert.Empty(FindKeyRowProblems(axaml, providerCount: 2));
    }

    // ── Live-tree test ─────────────────────────────────────────────────────

    /// <summary>
    /// The live enforcing gate: the shipped panel must bind exactly one row per
    /// entry in <see cref="MainWindowViewModel.AllProviderKeys"/>.
    /// </summary>
    [Fact]
    public void LivePanel_HasOneRowPerProviderKey()
    {
        var axaml = File.ReadAllText(PanelPath);

        var problems = FindKeyRowProblems(axaml, MainWindowViewModel.AllProviderKeys.Count);

        Assert.True(problems.Count == 0,
            "SettingsPanel.axaml and MainWindowViewModel.AllProviderKeys are out of step " +
            "(add or remove a hardcoded row so every provider key has exactly one):\n" +
            string.Join("\n", problems));
    }
}
