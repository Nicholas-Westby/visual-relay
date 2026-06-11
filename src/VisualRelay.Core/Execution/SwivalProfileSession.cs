using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

internal sealed class SwivalProfileSession : IAsyncDisposable
{
    internal const string FileName = "swival.toml";
    private readonly string _path;
    private readonly bool _created;
    private readonly string? _originalContent;
    private readonly string? _pinnedContent;
    private readonly bool _pinnedMode;

    private SwivalProfileSession(string path, bool created)
    {
        _path = path;
        _created = created;
        _pinnedMode = false;
    }

    private SwivalProfileSession(string path, string? originalContent, string pinnedContent)
    {
        _path = path;
        _originalContent = originalContent;
        _pinnedContent = pinnedContent;
        _created = originalContent is null;
        _pinnedMode = true;
    }

    public static async Task<SwivalProfileSession> PrepareAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, FileName);
        if (File.Exists(path))
        {
            return new SwivalProfileSession(path, created: false);
        }

        await File.WriteAllTextAsync(path, DefaultToml, cancellationToken);
        return new SwivalProfileSession(path, created: true);
    }

    /// <summary>
    /// Prepares a swival profile session with pinned content, saving the
    /// working tree's original swival.toml (if any) and overwriting it with
    /// <paramref name="pinnedContent"/> so the launched swival process sees
    /// the frozen profile. On <see cref="DisposeAsync"/>, if the file on disk
    /// still matches <paramref name="pinnedContent"/> (i.e. the session did
    /// not edit it), the original tree content is restored (or the file
    /// deleted if none existed). If the file differs — because the task
    /// edited it — the edit is left untouched so it survives to commit.
    /// When <paramref name="eventSink"/> is non-null and pinned content differs
    /// from the tree's current content, an info-level
    /// "swival_profile_divergence" event is emitted.
    /// </summary>
    public static async Task<SwivalProfileSession> PrepareWithPinnedContentAsync(
        string rootPath,
        string pinnedContent,
        string runId,
        string taskId,
        IRelayEventSink? eventSink,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, FileName);
        string? originalContent = null;
        if (File.Exists(path))
        {
            originalContent = await File.ReadAllTextAsync(path, cancellationToken);
        }

        // Emit divergence event when pinned content differs from tree content.
        if (eventSink is not null && originalContent is not null
            && !string.Equals(originalContent, pinnedContent, StringComparison.Ordinal))
        {
            await eventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "info",
                "swival_profile_divergence",
                RunId: runId,
                RootPath: rootPath,
                TaskId: taskId,
                Data: new Dictionary<string, string>
                {
                    ["reason"] = "pinned swival profile differs from working-tree swival.toml — "
                               + "the run will use the pinned (frozen) content; "
                               + "a backend/profile swap is pending at the drive boundary"
                }), cancellationToken);
        }

        await File.WriteAllTextAsync(path, pinnedContent, cancellationToken);
        return new SwivalProfileSession(path, originalContent, pinnedContent);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pinnedMode)
        {
            // Only restore the original tree content when the task did NOT
            // edit swival.toml during the session (file still matches pinned).
            // If it differs, the task authored a legitimate edit — leave it.
            string? currentContent = null;
            if (File.Exists(_path))
            {
                currentContent = await File.ReadAllTextAsync(_path);
            }

            if (currentContent is not null
                && string.Equals(currentContent, _pinnedContent, StringComparison.Ordinal))
            {
                // No edit occurred — restore the original tree content.
                if (_originalContent is not null)
                {
                    await File.WriteAllTextAsync(_path, _originalContent);
                }
                else
                {
                    File.Delete(_path);
                }
            }
            // else: file was edited during the session — leave it untouched.
        }
        else if (_created)
        {
            File.Delete(_path);
        }
    }

    // Interpolated raw string so every base_url reads from the centralized
    // ModelBackend (one source of truth). static readonly because interpolation
    // is not a compile-time constant; the generated TOML is byte-identical.
    internal static readonly string DefaultToml =
        $"""
        [profiles.frontier]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "frontier"
        max_context_tokens = 128000

        [profiles.balanced]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "balanced"
        max_context_tokens = 128000

        [profiles.cheap]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "cheap"
        max_context_tokens = 128000

        [profiles.vision]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "vision"
        max_context_tokens = 128000

        [profiles.claude]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude"
        max_context_tokens = 200000

        [profiles.opus]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude-opus-1m"
        max_context_tokens = 1000000

        [profiles.sonnet]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "claude-sonnet"
        max_context_tokens = 200000

        [profiles.gpt5]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "gpt-5"
        max_context_tokens = 400000

        [profiles.qwen-coder]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "hf-qwen3-coder-next"
        max_context_tokens = 256000

        [profiles.fallback]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "fallback"
        max_context_tokens = 256000

        [profiles.kimi]
        provider = "generic"
        base_url = "{ModelBackend.BaseUrl}"
        model = "kimi-k2"
        max_context_tokens = 200000
        """;
}
