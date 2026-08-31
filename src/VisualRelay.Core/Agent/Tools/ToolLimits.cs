namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Every output ceiling in one place, with the reasoning behind each.
///
/// The loop keeps the whole conversation in memory for the life of a stage, so a
/// tool result is not transient: it is paid for once in RAM and again in every
/// subsequent request's prompt. Process exit no longer reclaims it. These caps
/// are therefore chosen so that a single careless call — <c>read_file</c> on a
/// generated 200k-line file, <c>grep '.'</c> across a repository — costs a
/// bounded, roughly-known number of tokens instead of an unbounded one. Every
/// cap is paired with a truncation marker naming what was elided and which
/// argument narrows the next call, so a truncated result is still actionable.
/// </summary>
internal static class ToolLimits
{
    /// <summary>Lines returned by one <c>read_file</c> call (~a long source file).</summary>
    internal const int ReadFileLines = 2_000;

    /// <summary>Characters returned by one <c>read_file</c> call: ~16k tokens.</summary>
    internal const int ReadFileChars = 64 * 1024;

    /// <summary>Paths accepted by one <c>read_multiple_files</c> call.</summary>
    internal const int MultiFileCount = 20;

    /// <summary>Characters per file in a <c>read_multiple_files</c> batch.</summary>
    internal const int MultiFilePerFileChars = 16 * 1024;

    /// <summary>Characters across a whole <c>read_multiple_files</c> batch: ~24k tokens.</summary>
    internal const int MultiFileTotalChars = 96 * 1024;

    /// <summary>Entries returned by one <c>list_files</c> call.</summary>
    internal const int ListEntries = 500;

    /// <summary>Matching lines returned by one <c>grep</c> call.</summary>
    internal const int GrepMatches = 200;

    /// <summary>Characters kept from a single matching line before it is clipped.</summary>
    internal const int GrepLineChars = 300;

    /// <summary>Characters returned by one <c>grep</c> call.</summary>
    internal const int GrepChars = 64 * 1024;

    /// <summary>Files larger than this are skipped by <c>grep</c> as build output or data.</summary>
    internal const int GrepFileBytes = 1024 * 1024;

    /// <summary>Files opened by one <c>grep</c> call before the walk stops.</summary>
    internal const int GrepFilesScanned = 5_000;

    /// <summary>Declarations returned by one <c>outline</c> call.</summary>
    internal const int OutlineEntries = 400;

    /// <summary>
    /// Bytes of image <c>view_image</c> will encode. Base64 inflates by 4/3, so
    /// this is ~6.7 MiB on the wire — under every provider's per-image ceiling,
    /// and far above any screenshot the render stage produces.
    /// </summary>
    internal const int ViewImageBytes = 5 * 1024 * 1024;
}
