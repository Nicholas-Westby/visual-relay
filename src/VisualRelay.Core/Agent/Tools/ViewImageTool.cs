using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>view_image</c>: hands a picture to a multimodal model.
///
/// The name is load-bearing — the Visual-review stage prompt tells the model to
/// "open each listed image with view_image" and lists render paths beside it —
/// so the tool must accept exactly those paths and put the pixels in front of
/// the model. It returns the encoded image in <see cref="ToolResult.Images"/>
/// and a one-line description in the text; the loop attaches the former as a
/// <c>ContentPart.FromImage</c> part, since the tool-result message itself is
/// text-only on these providers.
/// </summary>
public sealed class ViewImageTool : IAgentTool
{
    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] GifMagic = "GIF8"u8.ToArray();
    private static readonly byte[] WebpMagic = "RIFF"u8.ToArray();

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "view_image",
        "Open an image file (PNG, JPEG, GIF or WebP) and look at it. Use this for every screenshot "
        + "or image attachment listed in your input — the picture itself is attached to the "
        + "conversation, so judge what it shows rather than reasoning about the file name. Images "
        + $"must be under {ToolLimits.ViewImageBytes / (1024 * 1024)} MiB.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text(
                    "Path to the image, exactly as listed in your input (relative to the repository root)."),
            },
            "path"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var missing = ToolFiles.DescribeMissing(context, absolute, "view_image");
        if (missing is not null) return ToolResult.Error(missing);

        var shown = ToolPaths.Display(context, absolute);
        var extension = Path.GetExtension(absolute).ToLowerInvariant();
        if (FormatFor(extension) is not { } format)
            return ToolResult.Error(
                $"view_image: \"{shown}\" is not a supported image ({(extension.Length == 0 ? "no extension" : extension)}). "
                + "Supported: .png, .jpg, .jpeg, .gif, .webp. Use read_file for text files.");

        try
        {
            var size = new FileInfo(absolute).Length;
            if (size > ToolLimits.ViewImageBytes)
                return ToolResult.Error(
                    $"view_image: \"{shown}\" is {ToolText.Bytes(size)}, over the "
                    + $"{ToolLimits.ViewImageBytes / (1024 * 1024)} MiB limit for an attached image. "
                    + "Ask for a smaller capture, or review a different render.");

            var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
            if (!bytes.AsSpan().StartsWith(format.Magic))
                return ToolResult.Error(
                    $"view_image: \"{shown}\" is named {extension} but its contents are not a "
                    + $"{format.MediaType} image (it may be empty or truncated). Check the render "
                    + "step's output, or view a different file.");

            var url = $"data:{format.MediaType};base64,{Convert.ToBase64String(bytes)}";
            return ToolResult.WithImages(
                $"view_image: attached \"{shown}\" — {format.MediaType}{Dimensions(bytes)}, "
                + $"{ToolText.Bytes(size)}. The image is in the conversation; describe what it "
                + "actually shows.\n",
                [url]);
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"view_image: could not read \"{shown}\": {ex.Message}. Confirm the path with list_files.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error($"view_image: \"{shown}\" is not readable (permission denied).");
        }
    }

    private static (string MediaType, byte[] Magic)? FormatFor(string extension) => extension switch
    {
        ".png" => ("image/png", PngMagic),
        ".jpg" or ".jpeg" => ("image/jpeg", JpegMagic),
        ".gif" => ("image/gif", GifMagic),
        ".webp" => ("image/webp", WebpMagic),
        _ => null,
    };

    /// <summary>
    /// Reads a PNG's IHDR header, which sits at a fixed offset. Only PNG is
    /// decoded: it is what the render stage produces, and the other formats
    /// would each need their own parser for a line of prose.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>A dimensions fragment, or an empty string when they cannot be read.</returns>
    private static string Dimensions(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[1] != 'P' || bytes[2] != 'N' || bytes[3] != 'G') return string.Empty;

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0 ? $" {width}×{height}" : string.Empty;
    }
}
