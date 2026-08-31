using System.Text.Json;
using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// Shared plumbing for the agent file-tool tests: a context pointing at a temp
/// repository, JSON arguments from a literal, and a one-line invoke.
/// </summary>
internal static class AgentToolTestHelpers
{
    // A context for a target root, with a stage budget no file tool will exhaust.
    private static ToolContext Context(string root) => new(root, TimeSpan.FromMinutes(5));

    // Tool arguments from a JSON literal.
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Invokes a tool against a root with JSON arguments.</summary>
    /// <param name="tool">The tool under test.</param>
    /// <param name="root">The target root.</param>
    /// <param name="json">The arguments object as JSON.</param>
    /// <returns>The tool's result.</returns>
    internal static Task<ToolResult> InvokeAsync(IAgentTool tool, string root, string json) =>
        tool.InvokeAsync(Args(json), Context(root), CancellationToken.None);

    /// <summary>Writes a file inside the root, creating directories as needed.</summary>
    /// <param name="root">The target root.</param>
    /// <param name="relative">Path relative to the root.</param>
    /// <param name="content">The file's contents.</param>
    /// <returns>The absolute path written.</returns>
    internal static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Builds the bytes of a minimal PNG with the given dimensions.</summary>
    /// <param name="width">Pixel width recorded in the IHDR header.</param>
    /// <param name="height">Pixel height recorded in the IHDR header.</param>
    /// <returns>Header-valid PNG bytes.</returns>
    internal static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        "IHDR"u8.ToArray().CopyTo(bytes, 12);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(width)).CopyTo(bytes, 16);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(height)).CopyTo(bytes, 20);
        return bytes;
    }
}
