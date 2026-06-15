using System.Security.Cryptography;
using System.Text;

namespace VisualRelay.Core.Execution;

internal static class Hashing
{
    private const string Separator = "\u2016";

    public static string Sha256Hex(params string[] parts)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join(Separator, parts.Select(Canonicalize)));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Canonicalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Normalize();
}

