namespace VisualRelay.Domain;

/// <summary>
/// Line endings for text written into a file git may track. Git reports a rewritten
/// file as modified when its size changes, even when the diff is empty, so a rewrite
/// that changes a file's endings leaves it looking edited until someone stages it.
/// <para>
/// Which endings git checks a file out with depends on the repository, not on the
/// platform: LF under <c>eol=lf</c>, CRLF under <c>core.autocrlf=true</c> with no
/// attributes, which is the Git for Windows default. Both measured 2026-09-18. So
/// neither <c>"\n"</c> nor <see cref="Environment.NewLine"/> is right everywhere; the
/// endings the file already has are. A file that does not exist yet is written LF,
/// which is how git stores text. Console output and logs keep the platform's newline:
/// nothing tracks them, and a reader there wants the platform's convention.
/// </para>
/// </summary>
public static class LineEndings
{
    /// <summary>
    /// <paramref name="text"/> in the endings of the file at <paramref name="path"/>, or LF
    /// when there is no such file, including one that disappears while this looks.
    /// </summary>
    public static string ForFile(string path, string text)
    {
        string? existing;
        try
        {
            existing = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            existing = null;
        }

        return Match(existing, text);
    }

    /// <summary>
    /// <paramref name="text"/> in the endings most of <paramref name="existing"/>'s lines
    /// use: CRLF when more of its breaks are CRLF than LF alone, else LF, and LF when it is
    /// null. A majority rather than "any CRLF", so one stray CR written into a text does not
    /// turn the file's next rewrite into CRLF throughout. Only CRLF and LF are line breaks
    /// here; a lone CR is part of the text and stays as it is.
    /// </summary>
    public static string Match(string? existing, string text)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return existing is not null && IsMostlyCrlf(existing)
            ? lf.Replace("\n", "\r\n", StringComparison.Ordinal)
            : lf;
    }

    private static bool IsMostlyCrlf(string text)
    {
        int crlf = 0, lfAlone = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            if (i > 0 && text[i - 1] == '\r')
                crlf++;
            else
                lfAlone++;
        }

        return crlf > lfAlone;
    }
}
