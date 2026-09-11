using System.Globalization;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>One row of <c>wsl -l -v</c>: an installed distro and its WSL version.</summary>
public sealed record WslDistro(string Name, int Version, bool IsDefault, string State);

/// <summary>
/// Parses the text of <c>wsl -l -v</c>. The NAME, STATE and VERSION columns are
/// located by the character positions of the header line's words, never by the
/// English words themselves, so a localized Windows (whose header is translated
/// and whose STATE values can contain a space) parses identically. The leading
/// <c>*</c> marks the default distro. Tolerates <c>\r\n</c>, and the NUL and
/// replacement characters left behind when wsl.exe's default UTF-16 output was
/// read as UTF-8 (the runner sets <c>WSL_UTF8=1</c>, but never trust it). Rows
/// without an integer version (a "no installed distributions" notice, a warning
/// line) are skipped, so the parser returns an empty list rather than throwing.
/// </summary>
public static class WslListParser
{
    private static readonly char[] Noise = ['\0', '�', '﻿'];

    public static IReadOnlyList<WslDistro> Parse(string text)
    {
        var distros = new List<WslDistro>();
        if (string.IsNullOrEmpty(text))
            return distros;

        var lines = string.Concat(text.Split(Noise)).Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToList();
        var headerIndex = lines.FindIndex(line => !string.IsNullOrWhiteSpace(line));
        if (headerIndex < 0)
            return distros;

        var columns = ColumnStarts(lines[headerIndex]);
        if (columns.Count < 3)
            return distros;

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (TryParseRow(line, columns[0], columns[1], columns[2]) is { } distro)
                distros.Add(distro);
        }

        return distros;
    }

    /// <summary>The index at which each whitespace-separated word of the header starts.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (!char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1])))
                starts.Add(i);
        }

        return starts;
    }

    private static WslDistro? TryParseRow(string line, int nameStart, int stateStart, int versionStart)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var name = Slice(line, nameStart, stateStart).Trim();
        var state = Slice(line, stateStart, versionStart).Trim();
        var versionText = Slice(line, versionStart, line.Length).Trim();
        if (!TryParseVersion(versionText, out var version))
        {
            // The row drifted off the header's columns (a state wider than its
            // column): the version is still the last word, the state everything
            // between the state column and that word.
            var lastSpace = line.TrimEnd().LastIndexOf(' ');
            if (lastSpace < stateStart || !TryParseVersion(line[(lastSpace + 1)..].Trim(), out version))
                return null;
            state = Slice(line, stateStart, lastSpace).Trim();
        }

        if (name.Length == 0)
            return null;
        var isDefault = Slice(line, 0, nameStart).Contains('*');
        return new WslDistro(name, version, isDefault, state);
    }

    private static bool TryParseVersion(string text, out int version) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version);

    private static string Slice(string line, int start, int end) =>
        start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)];
}
