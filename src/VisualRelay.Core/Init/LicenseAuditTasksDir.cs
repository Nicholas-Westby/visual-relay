using System.Text.RegularExpressions;

namespace VisualRelay.Core.Init;

/// <summary>
/// Keeps task files out of an Apache RAT license audit. RAT fails the build on every visible file
/// without an approved license header, does not read .git/info/exclude, and skips hidden entries.
/// Measured with apache/commons-lang on the Windows arm: <c>mvn test</c> failed once a task file
/// existed, an agent wrote .mvn/maven.config with -Drat.skip=true to get past it, and after the run
/// the two committed DONE files failed the project's own <c>mvn validate</c> on HEAD.
/// </summary>
internal static partial class LicenseAuditTasksDir
{
    /// <summary>The tasks directory a RAT-audited project gets: hidden, so RAT skips it.</summary>
    private const string HiddenTasksDir = ".llm-tasks";

    private const string DefaultTasksDir = "llm-tasks";

    /// <summary>
    /// Points <c>tasksDir</c> at <see cref="HiddenTasksDir"/> when the project at
    /// <paramref name="rootPath"/> runs RAT, and returns the note that says so. A project that
    /// already keeps tasks in the default directory is left alone, with a note on how to move them.
    /// Null for a project RAT does not audit.
    /// </summary>
    internal static string? Apply(string rootPath)
    {
        if (!RunsApacheRat(rootPath))
            return null;

        if (Directory.Exists(Path.Combine(rootPath, DefaultTasksDir)))
            return $"This project runs Apache RAT, which fails the build on the task files in {DefaultTasksDir}/: "
                + $"move them to {HiddenTasksDir}/ and set tasksDir to \"{HiddenTasksDir}\", since RAT skips hidden directories.";

        RelayConfigWriter.UpsertTasksDir(rootPath, HiddenTasksDir);
        return $"tasksDir set to {HiddenTasksDir}: this project runs Apache RAT, which fails the build on visible "
            + "files without a license header and skips hidden directories.";
    }

    /// <summary>Whether the build runs Apache RAT: its Maven or Gradle plugin, or Apache Commons' parent POM.</summary>
    private static bool RunsApacheRat(string rootPath)
    {
        var pom = Read(rootPath, "pom.xml");
        if (pom.Contains("apache-rat-plugin", StringComparison.Ordinal) || CommonsParent().IsMatch(pom))
            return true;
        return Read(rootPath, "build.gradle").Contains("apache.rat", StringComparison.Ordinal)
            || Read(rootPath, "build.gradle.kts").Contains("apache.rat", StringComparison.Ordinal)
            || File.Exists(Path.Combine(rootPath, ".rat-excludes"));
    }

    private static string Read(string rootPath, string name)
    {
        var path = Path.Combine(rootPath, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // commons-parent binds apache-rat-plugin's check to the validate phase for every Commons project.
    [GeneratedRegex(@"<parent>(?:(?!</parent>)[\s\S])*<artifactId>\s*commons-parent\s*</artifactId>")]
    private static partial Regex CommonsParent();
}
