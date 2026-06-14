using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class PlanCompletenessGateTests
{
    [Fact]
    public void CheckCoverage_NoDeliverableSection_ReturnsNull()
    {
        var result = PlanCompletenessGate.CheckCoverage("plan", ["src/app.cs"], "# Task\nNo checklist.");
        Assert.Null(result);
    }

    [Fact]
    public void CheckCoverage_AllDeliverablesCovered_ReturnsNull()
    {
        var md = "## Done when\n- Add status field\n- Create migration\n";
        var plan = "Add status field and create migration.";
        var result = PlanCompletenessGate.CheckCoverage(plan, ["src/Model.cs"], md);
        Assert.Null(result);
    }

    [Fact]
    public void CheckCoverage_PartialCoverage_ReturnsCorrectionMessage()
    {
        var md = "## Done when\n- Add status field\n- Create migration\n";
        var plan = "Add status field.";
        var result = PlanCompletenessGate.CheckCoverage(plan, ["src/Model.cs"], md);
        Assert.NotNull(result);
        Assert.Contains("migration", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckCoverage_EmptyManifest_UncoveredDeliverable_ReturnsCorrectionMessage()
    {
        var md = "## Done when\n- Implement authentication\n";
        var result = PlanCompletenessGate.CheckCoverage("Refactor logging.", [], md);
        Assert.NotNull(result);
        Assert.Contains("authentication", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckCoverage_CaseInsensitiveMatch_Passes()
    {
        var md = "## Done when\n- Implement AUTHENTICATION\n";
        var plan = "implement authentication.";
        var result = PlanCompletenessGate.CheckCoverage(plan, [], md);
        Assert.Null(result);
    }

    [Fact]
    public void CheckCoverage_ShortTokensIgnored_DoesNotFalseNegative()
    {
        // All tokens <5 chars ("red", "bug", "cat", "icon") → skip, no false positive.
        var md = "## Done when\n- Fix the red bug\n- Add cat icon\n";
        var result = PlanCompletenessGate.CheckCoverage("General improvements.", [], md);
        Assert.Null(result);
    }

    [Fact]
    public void CheckCoverage_DeliverablesHeadingVariant_Works()
    {
        var md = "## Deliverables\n- Write unit tests\n- Document mechanism\n";
        var plan = "Document mechanism.";
        var result = PlanCompletenessGate.CheckCoverage(plan, [], md);
        Assert.NotNull(result);
        Assert.Contains("unit tests", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckCoverage_SectionEndsAtNextHeading()
    {
        var md = "## Done when\n- Implement Alpha\n## Notes\n- Not a deliverable\n";
        var result = PlanCompletenessGate.CheckCoverage("General work.", [], md);
        Assert.NotNull(result);
        Assert.Contains("Alpha", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT a deliverable", result, StringComparison.OrdinalIgnoreCase);
    }
}
