using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class TaskTemplatesTests
{
    [Fact]
    public void Parse_Frontmatter_ExtractsNameTitleAndBody()
    {
        var template = TaskTemplates.Parse("test", "---\nname: Fancy\ntitle: Do it\n---\nBody line.\n", TaskTemplateSource.BuiltIn);

        Assert.Equal("Fancy", template.Name);
        Assert.Equal("Do it", template.Title);
        Assert.Equal("Body line.\n", template.Body);
    }

    [Fact]
    public void Parse_NoFrontmatter_WholeContentIsBody_NameFallsBackToId()
    {
        var template = TaskTemplates.Parse("my-id", "Just some content.\n", TaskTemplateSource.User);

        Assert.Equal("my-id", template.Name);
        Assert.Equal(string.Empty, template.Title);
        Assert.Equal("Just some content.\n", template.Body);
    }

    [Fact]
    public void Parse_CrLf_NormalizedBeforeParsing()
    {
        var template = TaskTemplates.Parse("test", "---\r\nname: Fancy\r\ntitle: Do it\r\n---\r\nBody line.\r\n", TaskTemplateSource.Repo);

        Assert.Equal("Fancy", template.Name);
        Assert.Equal("Do it", template.Title);
        Assert.Equal("Body line.\n", template.Body);
    }

    [Fact]
    public void Parse_UnknownFrontmatterKeys_Ignored()
    {
        var template = TaskTemplates.Parse("x", "---\nname: Hi\ndescription: ignored\n---\nB\n", TaskTemplateSource.BuiltIn);

        Assert.Equal("Hi", template.Name);
        Assert.Equal(string.Empty, template.Title);
        Assert.Equal("B\n", template.Body);
    }

    [Fact]
    public void Parse_UnclosedFrontmatter_TreatedAsBody()
    {
        var template = TaskTemplates.Parse("foo", "---\nname: X\nno close", TaskTemplateSource.User);

        Assert.Equal("foo", template.Name);
        Assert.Equal(string.Empty, template.Title);
        Assert.Equal("---\nname: X\nno close", template.Body);
    }

    [Fact]
    public void Load_BuiltIns_BlankFirstThenSpeedUp()
    {
        var templates = TaskTemplates.Load("/nonexistent/user", "/nonexistent/repo");

        Assert.Equal(2, templates.Count);
        Assert.Equal("blank", templates[0].Id);
        Assert.Equal("Blank", templates[0].Name);
        Assert.Equal(string.Empty, templates[0].Title);
        Assert.Equal(string.Empty, templates[0].Body);
        Assert.Equal(TaskTemplateSource.BuiltIn, templates[0].Source);

        Assert.Equal("speed-up-automated-tests", templates[1].Id);
        Assert.Equal("Create Tasks to Speed Up Automated Tests", templates[1].Name);
        Assert.Equal("Speed up automated tests", templates[1].Title);
        Assert.True(templates[1].Body.Length > 100, "Speed-up template body must be substantial");
    }

    [Fact]
    public void Load_UserOverridesBuiltIn_RepoOverridesUser()
    {
        using var userDir = new TempDir();
        using var repoDir = new TempDir();

        File.WriteAllText(Path.Combine(userDir.Dir, "blank.md"), "USER body");
        File.WriteAllText(Path.Combine(repoDir.Dir, "blank.md"), "REPO body");

        var templates = TaskTemplates.Load(userDir.Dir, repoDir.Dir);
        var blank = templates[0];
        Assert.Equal("blank", blank.Id);
        Assert.Equal(TaskTemplateSource.Repo, blank.Source);
        Assert.StartsWith("REPO", blank.Body, StringComparison.Ordinal);

        // Delete repo copy — user layer wins.
        File.Delete(Path.Combine(repoDir.Dir, "blank.md"));
        templates = TaskTemplates.Load(userDir.Dir, repoDir.Dir);
        blank = templates[0];
        Assert.Equal(TaskTemplateSource.User, blank.Source);
        Assert.StartsWith("USER", blank.Body, StringComparison.Ordinal);

        // Blank must still be index 0 in both cases.
        Assert.Equal("blank", templates[0].Id);
    }

    [Fact]
    public void Load_NewIdsFromBothLayers_AppearSortedByName()
    {
        using var userDir = new TempDir();
        using var repoDir = new TempDir();

        File.WriteAllText(Path.Combine(userDir.Dir, "zebra.md"), "---\nname: Zebra\n---\nZ");
        File.WriteAllText(Path.Combine(repoDir.Dir, "alpha-repo.md"), "---\nname: Alpha Repo\n---\nA");

        var templates = TaskTemplates.Load(userDir.Dir, repoDir.Dir);

        // blank first, then alphabetically by Name
        Assert.True(templates.Count >= 4);
        Assert.Equal("blank", templates[0].Id);

        var names = templates.Skip(1).Select(t => t.Name).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void Load_UnreadableEntrySkipped()
    {
        using var userDir = new TempDir();

        // Create a directory named broken.md — attempting to read it as a file will throw.
        Directory.CreateDirectory(Path.Combine(userDir.Dir, "broken.md"));
        // Also a good template.
        File.WriteAllText(Path.Combine(userDir.Dir, "good.md"), "---\nname: Good\n---\nContent");

        var templates = TaskTemplates.Load(userDir.Dir, "/nonexistent/repo");

        Assert.Contains(templates, t => t.Id == "good");
        // No exception thrown — the broken one was skipped.
        Assert.DoesNotContain(templates, t => t.Id == "broken");
    }

    [Fact]
    public void SpeedUpTemplate_PinsLoadBearingContent()
    {
        var templates = TaskTemplates.Load("/nonexistent/user", "/nonexistent/repo");
        var body = templates[1].Body;

        Assert.Contains("- test time dropped from 80s to 60s, saving 20s", body, StringComparison.Ordinal);
        Assert.Contains("Never delete, disable, skip, or weaken a test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", body, StringComparison.Ordinal);
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
