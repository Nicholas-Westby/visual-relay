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
    public void Load_RepoTemplate_AttachmentsComeFromSiblingIdDirectory()
    {
        using var repoDir = new TempDir();
        File.WriteAllText(Path.Combine(repoDir.Dir, "kit.md"), "---\nname: Kit\n---\nBody\n");
        var attachmentsDir = Path.Combine(repoDir.Dir, "kit");
        Directory.CreateDirectory(attachmentsDir);
        File.WriteAllText(Path.Combine(attachmentsDir, "b-notes.md"), "notes body");
        File.WriteAllText(Path.Combine(attachmentsDir, "a-data.txt"), "raw data");
        File.WriteAllText(Path.Combine(attachmentsDir, ".DS_Store"), "junk");
        // Nested directories are not attachments — only top-level files are.
        Directory.CreateDirectory(Path.Combine(attachmentsDir, "nested"));
        File.WriteAllText(Path.Combine(attachmentsDir, "nested", "deep.md"), "deep");

        var kit = TaskTemplates.Load("/nonexistent/user", repoDir.Dir).Single(t => t.Id == "kit");

        Assert.Equal(2, kit.Attachments.Count);
        Assert.Equal("a-data.txt", kit.Attachments[0].FileName);
        Assert.Equal("raw data", System.Text.Encoding.UTF8.GetString(kit.Attachments[0].Content));
        Assert.Equal("b-notes.md", kit.Attachments[1].FileName);
        Assert.Equal("notes body", System.Text.Encoding.UTF8.GetString(kit.Attachments[1].Content));
    }

    [Fact]
    public void Load_TemplateWithoutAttachmentDirectory_HasNoAttachments()
    {
        using var repoDir = new TempDir();
        File.WriteAllText(Path.Combine(repoDir.Dir, "kit.md"), "Body\n");

        var kit = TaskTemplates.Load("/nonexistent/user", repoDir.Dir).Single(t => t.Id == "kit");

        Assert.Empty(kit.Attachments);
    }

    [Fact]
    public void Load_OverridingLayerReplacesAttachments_NoCrossLayerMerge()
    {
        using var userDir = new TempDir();
        using var repoDir = new TempDir();
        File.WriteAllText(Path.Combine(userDir.Dir, "kit.md"), "USER body");
        Directory.CreateDirectory(Path.Combine(userDir.Dir, "kit"));
        File.WriteAllText(Path.Combine(userDir.Dir, "kit", "user-only.md"), "from user");
        // Repo overrides the template but ships no attachment directory.
        File.WriteAllText(Path.Combine(repoDir.Dir, "kit.md"), "REPO body");

        var kit = TaskTemplates.Load(userDir.Dir, repoDir.Dir).Single(t => t.Id == "kit");
        Assert.Equal(TaskTemplateSource.Repo, kit.Source);
        Assert.Empty(kit.Attachments);

        // User layer alone keeps its attachment.
        File.Delete(Path.Combine(repoDir.Dir, "kit.md"));
        kit = TaskTemplates.Load(userDir.Dir, repoDir.Dir).Single(t => t.Id == "kit");
        Assert.Equal(TaskTemplateSource.User, kit.Source);
        Assert.Equal(["user-only.md"], kit.Attachments.Select(a => a.FileName).ToArray());
    }

    [Fact]
    public void Load_BuiltInSpeedUp_ExposesCommitMessageEvidenceAttachment()
    {
        var templates = TaskTemplates.Load("/nonexistent/user", "/nonexistent/repo");
        var speedUp = templates.Single(t => t.Id == "speed-up-automated-tests");

        var evidence = Assert.Single(speedUp.Attachments);
        Assert.Equal("commit-message-evidence.md", evidence.FileName);

        var content = System.Text.Encoding.UTF8.GetString(evidence.Content);
        Assert.Contains("- test time dropped from <before> to <after>, saving <delta> (<scope>)",
            content, StringComparison.Ordinal);
        Assert.Contains("commit message", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAttachmentsAsync_MaterializesFilesIntoTaskDirectory()
    {
        using var taskDir = new TempDir();
        var template = new TaskTemplate("kit", "Kit", "Kit", "Body", TaskTemplateSource.Repo)
        {
            Attachments =
            [
                new TaskTemplateAttachment("checklist.md", "check"u8.ToArray()),
                // A hostile name must not escape the task directory.
                new TaskTemplateAttachment("../escape.md", "evil"u8.ToArray()),
            ],
        };

        await TaskTemplates.WriteAttachmentsAsync(taskDir.Dir, template);

        Assert.Equal("check", File.ReadAllText(Path.Combine(taskDir.Dir, "checklist.md")));
        Assert.Equal("evil", File.ReadAllText(Path.Combine(taskDir.Dir, "escape.md")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(taskDir.Dir)!, "escape.md")));
    }

    [Fact]
    public void SpeedUpTemplate_PinsLoadBearingContent()
    {
        var templates = TaskTemplates.Load("/nonexistent/user", "/nonexistent/repo");
        var body = templates[1].Body;

        // The evidence bullet's exact shape lives in the attachment; the body
        // points at it for the run's own commit AND for every follow-up task.
        Assert.Contains("commit-message-evidence.md", body, StringComparison.Ordinal);
        Assert.Contains("Copy `commit-message-evidence.md`", body, StringComparison.Ordinal);
        // Numbers are measured at implementation time and belong in the commit
        // message — authored follow-ups must never carry a pre-filled bullet.
        Assert.Contains("never pre-fill", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dropped from 80s to 60s", body, StringComparison.Ordinal);
        // The parent folder is archived on completion — follow-ups must cite the
        // baseline where it will actually live.
        Assert.Contains("llm-tasks/completed/", body, StringComparison.Ordinal);
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
