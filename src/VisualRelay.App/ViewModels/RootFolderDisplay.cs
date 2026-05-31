namespace VisualRelay.App.ViewModels;

public static class RootFolderDisplay
{
    public static string DefaultPath()
    {
        var sample = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Dev",
            "sample-tasks");
        return Directory.Exists(sample) ? sample : string.Empty;
    }

    public static string Name(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return "Choose project";
        }

        var trimmed = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    public static string Parent(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return "Folder";
        }

        var trimmed = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(trimmed) ?? trimmed;
    }
}
