using System.Reflection;

namespace VisualRelay.Domain;

/// <summary>
/// Pure helpers for the auto-incrementing 0.x version.
/// The hook delegates to <see cref="BumpVersionFile"/>; every other consumer
/// reads the baked-in assembly version via <see cref="ReadInformationalVersion"/>.
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Increments the minor component of a "0.x" version string.
    /// Returns e.g. "0.2" for input "0.1", "0.10" for "0.9".
    /// </summary>
    /// <exception cref="ArgumentException">when <paramref name="version"/> is not a valid "0.x" string.</exception>
    public static string Bump(string version)
    {
        if (!TryParse(version, out var minor))
            throw new ArgumentException(
                $"Invalid version format: '{version}'. Expected '0.x' with x >= 0.", nameof(version));
        return Format(minor + 1);
    }

    /// <summary>
    /// Strictly parses a "0.x" version string. Returns false for null, empty,
    /// whitespace, missing/extra dots, non-digit characters, or major != 0.
    /// Leading zeros in the minor are accepted (e.g. "0.01" → 1).
    /// </summary>
    public static bool TryParse(string? text, out int minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Must be exactly "0.<non-negative integer>", no surrounding whitespace.
        if (text!.Length < 3 || text[0] != '0' || text[1] != '.')
            return false;

        var rest = text.Substring(2);
        if (rest.Length == 0)
            return false;

        foreach (var c in rest)
        {
            if (!char.IsDigit(c))
                return false;
        }

        return int.TryParse(rest, out minor);
    }

    /// <summary>Formats a minor number into "0.x" canonical form.</summary>
    public static string Format(int minor) => $"0.{minor}";

    /// <summary>
    /// Reads the VERSION file at <paramref name="path"/>, bumps it, and writes
    /// it back. If the file is missing, empty, whitespace-only, or garbled, it
    /// seeds the file with "0.1" and returns "0.1".
    /// Returns the new version string (e.g. "0.2").
    /// </summary>
    public static string BumpVersionFile(string path)
    {
        string? text = null;
        if (File.Exists(path))
        {
            text = File.ReadAllText(path).Trim();
        }

        if (text is not null && TryParse(text, out var current))
        {
            var bumped = Format(current + 1);
            File.WriteAllText(path, bumped + Environment.NewLine);
            return bumped;
        }

        // Seed with 0.1
        const string seed = "0.1";
        File.WriteAllText(path, seed + Environment.NewLine);
        return seed;
    }

    /// <summary>
    /// Returns the <see cref="AssemblyInformationalVersionAttribute"/> value from
    /// the Domain assembly, which carries the version baked in by the build.
    /// Returns "0.1" as a fallback when the attribute is missing.
    /// </summary>
    public static string ReadInformationalVersion()
    {
        var attr = typeof(VersionHelper).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? "0.1";
    }
}
