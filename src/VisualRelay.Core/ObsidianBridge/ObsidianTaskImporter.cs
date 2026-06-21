using System.Text.RegularExpressions;
using VisualRelay.Core.Tasks;

namespace VisualRelay.Core.ObsidianBridge;

/// <summary>
/// A candidate file in the <c>New Tasks/</c> folder that might be imported.
/// </summary>
public sealed record ImportCandidate(
    string FilePath,
    string Title,
    // ReSharper disable once NotAccessedPositionalProperty.Global — supplied
    // positionally at every construction site (incl. Scan's debounce value);
    // retained for completeness/diagnostics even though no caller reads it.
    DateTimeOffset LastWrite);

/// <summary>
/// The result of recognizing (importing) a candidate file.
/// </summary>
public sealed record ImportResult(
    string? Slug,
    Guid? SourceGuid,
    string? RecognizedPath,
    string? SkipReason);

/// <summary>
/// Imports markdown files from the Obsidian vault's <c>New Tasks/</c> folder
/// into the Relay <c>llm-tasks/</c> tree, using the established
/// <see cref="RelayTaskWriter"/> for slug creation and collision checks.
/// </summary>
public sealed class ObsidianTaskImporter
{
    // Regex to detect vr-recognized: <guid> in YAML frontmatter.
    private static readonly Regex VrRecognizedRegex = new(
        @"^vr-recognized\s*:\s*\S", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Regex to extract title: <value> from YAML frontmatter.
    private static readonly Regex TitleFieldRegex = new(
        @"^title\s*:\s*(.+?)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Regex to match YAML frontmatter block (--- ... ---).
    private static readonly Regex FrontmatterBlockRegex = new(
        @"\A---\s*\n.*?\n---\s*\n", RegexOptions.Singleline);

    // Regex to find the first # H1 heading.
    private static readonly Regex H1Regex = new(
        @"^#\s+(.+?)$", RegexOptions.Multiline);

    // Maximum number of suffix attempts for collision resolution.
    private const int MaxCollisionSuffixAttempts = 100;

    /// <summary>
    /// Enumerates candidate markdown files in the <c>New Tasks/</c> folder (top-level only).
    /// Excludes reserved files, already-recognized files, iCloud placeholders,
    /// zero-length files, and files newer than <paramref name="minStableAge"/>.
    /// </summary>
    public IReadOnlyList<ImportCandidate> Scan(
        ObsidianVaultLayout layout,
        DateTimeOffset nowUtc,
        TimeSpan minStableAge)
    {
        var newTasksDir = layout.NewTasksDir;
        if (!Directory.Exists(newTasksDir))
            return [];

        var candidates = new List<ImportCandidate>();

        foreach (var filePath in Directory.EnumerateFiles(newTasksDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);

            // Exclude reserved file names (INFO.md, README.md).
            if (ObsidianVaultLayout.ReservedFileNames.Contains(fileName))
                continue;

            // Exclude iCloud placeholder files (.icloud extension).
            if (fileName.EndsWith(".icloud", StringComparison.OrdinalIgnoreCase))
                continue;

            // Exclude zero-length files (iCloud not-yet-downloaded sentinels).
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
                continue;

            // Debounce: skip files newer than minStableAge.
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if ((nowUtc - lastWrite) < minStableAge)
                continue;

            // Exclude files already carrying vr-recognized frontmatter.
            if (HasVrRecognizedFrontmatter(filePath))
                continue;

            // Derive title for the candidate (used as display hint; Recognize re-derives).
            var title = DeriveTitle(filePath, out _);

            candidates.Add(new ImportCandidate(filePath, title, lastWrite));
        }

        return candidates;
    }

    /// <summary>
    /// Recognizes a candidate file: creates the Relay task via
    /// <see cref="RelayTaskWriter.CreateAsync"/>, stamps the source with
    /// vr-recognized frontmatter, and moves it to the Recognized/ folder.
    /// Returns the import result (slug + recognized path, or a skip reason).
    /// </summary>
    public async Task<ImportResult> Recognize(
        ImportCandidate candidate,
        string rootPath,
        DateTimeOffset nowUtc,
        Guid newGuid)
    {
        // Read the source file content.
        var sourceContent = File.ReadAllText(candidate.FilePath);

        // Derive the title: frontmatter title → first H1 → filename (no ext).
        var title = DeriveTitle(candidate.FilePath, out var hasFrontmatter);

        // Create slug and resolve collisions.
        var slug = RelayTaskWriter.Slugify(title);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return new ImportResult(null, null, null, "Could not derive a valid slug from the title.");
        }

        slug = ResolveSlug(slug, rootPath);
        if (slug is null)
        {
            return new ImportResult(null, null, null,
                $"Could not find a free slug for \"{title}\" after {MaxCollisionSuffixAttempts} attempts.");
        }

        // Strip leading YAML frontmatter from the body.
        var body = hasFrontmatter ? StripFrontmatter(sourceContent) : sourceContent;

        // Create the task through the canonical writer (slug validation + nested
        // directory layout).
        try
        {
            await RelayTaskWriter.CreateAsync(rootPath, slug, body);
        }
        catch (Exception ex)
        {
            return new ImportResult(null, null, null, $"Failed to create task: {ex.Message}");
        }

        // Stamp the source file with vr-recognized frontmatter.
        var repoName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stampFrontmatter = $"""
            ---
            vr-task-id: {slug}
            vr-recognized: {newGuid}
            vr-recognized-at: {nowUtc:yyyy-MM-ddTHH:mm:sszzz}
            vr-repo: {repoName}
            ---
            """;

        var stampedContent = stampFrontmatter + Environment.NewLine + sourceContent;

        // Move to Recognized/ directory (sibling to NewTasksDir).
        var fileName = Path.GetFileName(candidate.FilePath);
        var newTasksParent = Path.GetDirectoryName(candidate.FilePath)!;
        var recognizedTargetDir = Path.Combine(newTasksParent, "Recognized");
        if (!Directory.Exists(recognizedTargetDir))
            Directory.CreateDirectory(recognizedTargetDir);

        var recognizedPath = FindAvailablePath(recognizedTargetDir, fileName);

        // Write stamped content to recognized path, then delete original.
        File.WriteAllText(recognizedPath, stampedContent);
        File.Delete(candidate.FilePath);

        return new ImportResult(slug, newGuid, recognizedPath, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string DeriveTitle(string filePath, out bool hasFrontmatter)
    {
        var content = File.ReadAllText(filePath);
        hasFrontmatter = FrontmatterBlockRegex.IsMatch(content);

        // 1. Check for title: in YAML frontmatter.
        var titleMatch = TitleFieldRegex.Match(content);
        if (titleMatch.Success)
        {
            return titleMatch.Groups[1].Value.Trim();
        }

        // 2. Check for first # H1 heading.
        var h1Match = H1Regex.Match(content);
        if (h1Match.Success)
        {
            return h1Match.Groups[1].Value.Trim();
        }

        // 3. Fallback to filename without extension.
        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string? ResolveSlug(string baseSlug, string rootPath)
    {
        // Try the base slug first.
        if (RelayTaskWriter.ValidateSlug(baseSlug, rootPath) is null)
            return baseSlug;

        // Try suffixed versions.
        for (var i = 2; i <= MaxCollisionSuffixAttempts; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (RelayTaskWriter.ValidateSlug(candidate, rootPath) is null)
                return candidate;
        }

        return null;
    }

    private static bool HasVrRecognizedFrontmatter(string filePath)
    {
        try
        {
            var firstKb = new char[1024];
            using var reader = new StreamReader(filePath);
            var read = reader.Read(firstKb, 0, firstKb.Length);
            var prefix = new string(firstKb, 0, read);
            return VrRecognizedRegex.IsMatch(prefix);
        }
        catch
        {
            return false;
        }
    }

    private static string StripFrontmatter(string content)
    {
        return FrontmatterBlockRegex.Replace(content, "", 1);
    }

    private static string FindAvailablePath(string directory, string fileName)
    {
        var basePath = Path.Combine(directory, fileName);
        if (!File.Exists(basePath))
            return basePath;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{nameWithoutExt}-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // Last resort: append a GUID.
        return Path.Combine(directory, $"{nameWithoutExt}-{Guid.NewGuid():N}{ext}");
    }
}
