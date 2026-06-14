using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class ReviewEscalationPolicyTests
{
    // ── Model-signal tests ────────────────────────────────────────────

    [Fact]
    public void ShouldEscalate_VerdictChanges_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"changes","issues":[]}""");
        var json = doc.RootElement;

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest: [], rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_VerdictNotPass_CustomString_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"blocked","issues":[]}""");
        var json = doc.RootElement;

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest: [], rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_NonEmptyIssues_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":["unused-import"]}""");
        var json = doc.RootElement;

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest: [], rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_VerdictPassEmptyIssuesSmallManifest_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
        var json = doc.RootElement;

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest: ["src/a.cs", "src/b.cs"], rootPath: "/tmp",
            fileThreshold: 10, lineThreshold: 500);

        Assert.False(result);
    }

    // ── Manifest file-count heuristic ─────────────────────────────────

    [Fact]
    public void ShouldEscalate_ManifestExceedsFileThreshold_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
        var json = doc.RootElement;
        var manifest = Enumerable.Range(1, 11).Select(i => $"src/file{i}.cs").ToArray();

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest, rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_ManifestExactlyAtFileThreshold_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
        var json = doc.RootElement;
        var manifest = Enumerable.Range(1, 10).Select(i => $"src/file{i}.cs").ToArray();

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest, rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        Assert.False(result);
    }

    // ── Thresholds-disabled guard ─────────────────────────────────────

    [Fact]
    public void ShouldEscalate_ThresholdsDisabled_DoesNotEscalateOnSize()
    {
        using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
        var json = doc.RootElement;
        var manifest = Enumerable.Range(1, 50).Select(i => $"src/file{i}.cs").ToArray();

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest, rootPath: "/tmp", fileThreshold: 0, lineThreshold: 0);

        Assert.False(result);
    }

    // ── Missing/malformed verdict field ───────────────────────────────

    [Fact]
    public void ShouldEscalate_MissingVerdictField_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("""{"issues":[]}""");
        var json = doc.RootElement;

        var result = ReviewEscalationPolicy.ShouldEscalate(
            json, manifest: [], rootPath: "/tmp", fileThreshold: 10, lineThreshold: 500);

        // Missing verdict is treated as non-pass → escalate.
        Assert.True(result);
    }

    // ── Manifest line-count heuristic ─────────────────────────────────

    [Fact]
    public void ShouldEscalate_ManifestExceedsLineThreshold_ReturnsTrue()
    {
        using var repo = TestRepository.Create();
        try
        {
            // Create a single file with many lines.
            var manifestDir = Path.Combine(repo.Root, "src");
            Directory.CreateDirectory(manifestDir);
            var filePath = Path.Combine(manifestDir, "big.cs");
            File.WriteAllText(filePath, string.Join("\n", Enumerable.Range(1, 501).Select(i => $"// line {i}")));

            using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
            var json = doc.RootElement;

            var result = ReviewEscalationPolicy.ShouldEscalate(
                json, manifest: ["src/big.cs"], rootPath: repo.Root,
                fileThreshold: 10, lineThreshold: 500);

            Assert.True(result);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [Fact]
    public void ShouldEscalate_ManifestWithinLineThreshold_ReturnsFalse()
    {
        using var repo = TestRepository.Create();
        try
        {
            var manifestDir = Path.Combine(repo.Root, "src");
            Directory.CreateDirectory(manifestDir);
            var filePath = Path.Combine(manifestDir, "small.cs");
            File.WriteAllText(filePath, string.Join("\n", Enumerable.Range(1, 500).Select(i => $"// line {i}")));

            using var doc = JsonDocument.Parse("""{"verdict":"pass","issues":[]}""");
            var json = doc.RootElement;

            var result = ReviewEscalationPolicy.ShouldEscalate(
                json, manifest: ["src/small.cs"], rootPath: repo.Root,
                fileThreshold: 10, lineThreshold: 500);

            Assert.False(result);
        }
        finally
        {
            repo.Dispose();
        }
    }
}
