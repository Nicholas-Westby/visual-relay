using System.Text;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Single choke point for parsing git path output. Splits on newlines,
/// trims, skips blanks, and decodes C-quoted paths (git's default
/// <c>core.quotePath=true</c> format) into real file-system paths.
/// Callers should also pass <c>-c core.quotePath=false</c> to git so the
/// C-unquote path is a defense-in-depth fallback, not the primary code path.
/// </summary>
internal static class GitPathOutput
{
    /// <summary>
    /// Splits <paramref name="output"/> on <c>\n</c>/<c>\r</c>, trims each
    /// line, discards blanks, and decodes any C-quoted lines via
    /// <see cref="CUnquote"/>.
    /// </summary>
    public static IReadOnlyList<string> ParseLines(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var lines = output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                result.Add(CUnquote(trimmed));
        }

        return result;
    }

    /// <summary>
    /// Decodes a single C-quoted git path line.  If <paramref name="line"/>
    /// starts with <c>"</c> the surrounding quotes are stripped and every
    /// backslash escape (<c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\t</c>,
    /// <c>\r</c>, <c>\b</c>, <c>\f</c>, plus <c>\NNN</c> octal for raw
    /// bytes) is decoded.  Otherwise <paramref name="line"/> is returned
    /// as-is (the normal path when <c>core.quotePath=false</c> is in effect).
    /// The result preserves the exact byte form that <c>git ls-files</c>
    /// reports — no Unicode normalisation — so it round-trips safely into
    /// <c>git add</c> / <c>git rm --cached</c> pathspecs.
    /// </summary>
    internal static string CUnquote(string line)
    {
        if (line.Length == 0 || line[0] != '"')
            return line;

        // The line starts with ". Walk the quoted content between the
        // outer quotes, decoding byte-by-byte so multi-byte UTF-8
        // sequences (emitted as \NNN octal per byte) are reassembled
        // correctly.
        var bytes = new List<byte>(line.Length);
        var i = 1;
        var end = line.Length - 1; // closing quote index

        while (i < end)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < end)
            {
                i++;
                var next = line[i];
                switch (next)
                {
                    case '\\':
                        bytes.Add((byte)'\\');
                        break;
                    case '"':
                        bytes.Add((byte)'"');
                        break;
                    case 'n':
                        bytes.Add((byte)'\n');
                        break;
                    case 't':
                        bytes.Add((byte)'\t');
                        break;
                    case 'r':
                        bytes.Add((byte)'\r');
                        break;
                    case 'b':
                        bytes.Add((byte)'\b');
                        break;
                    case 'f':
                        bytes.Add((byte)'\f');
                        break;
                    case 'a':
                        bytes.Add((byte)'\a');
                        break;
                    case 'v':
                        bytes.Add((byte)'\v');
                        break;
                    default:
                        {
                            // Octal escape \NNN (1–3 octal digits).
                            if (next is >= '0' and <= '7')
                            {
                                var octal = next - '0';
                                var digits = 1;
                                while (digits < 3 && i + 1 < end && line[i + 1] is >= '0' and <= '7')
                                {
                                    i++;
                                    digits++;
                                    octal = (octal * 8) + (line[i] - '0');
                                }

                                bytes.Add((byte)octal);
                            }
                            else
                            {
                                // Unknown escape — emit the byte for 'next' as-is
                                // (defensive; git shouldn't produce these).
                                bytes.Add((byte)next);
                            }

                            break;
                        }
                }
            }
            else
            {
                // Plain character: encode as UTF-8 byte(s).  For ASCII this is
                // a single byte; for non-ASCII (defensive — git octal-escapes
                // bytes ≥ 0x80) this emits the multi-byte UTF-8 sequence.
                EncodeChar(bytes, c);
            }

            i++;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void EncodeChar(List<byte> bytes, char c)
    {
        if (c < 0x80)
        {
            bytes.Add((byte)c);
        }
        else
        {
            // Encode a single char as UTF-8 bytes.
            var rune = new Rune(c);
            Span<byte> buf = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buf);
            for (var j = 0; j < written; j++)
                bytes.Add(buf[j]);
        }
    }
}
