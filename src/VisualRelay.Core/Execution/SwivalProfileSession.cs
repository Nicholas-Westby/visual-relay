namespace VisualRelay.Core.Execution;

internal sealed class SwivalProfileSession : IAsyncDisposable
{
    private const string FileName = "swival.toml";
    private readonly string _path;
    private readonly bool _created;

    private SwivalProfileSession(string path, bool created)
    {
        _path = path;
        _created = created;
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

    public ValueTask DisposeAsync()
    {
        if (_created)
        {
            File.Delete(_path);
        }

        return ValueTask.CompletedTask;
    }

    private const string DefaultToml =
        """
        [profiles.frontier]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "frontier"
        max_context_tokens = 128000

        [profiles.balanced]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "balanced-kimi"
        max_context_tokens = 128000

        [profiles.cheap]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "cheap-kimi"
        max_context_tokens = 128000

        [profiles.vision]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "vision"
        max_context_tokens = 128000

        [profiles.claude]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "claude"
        max_context_tokens = 200000

        [profiles.opus]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "claude-opus-1m"
        max_context_tokens = 1000000

        [profiles.sonnet]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "claude-sonnet"
        max_context_tokens = 200000

        [profiles.gpt5]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "gpt-5"
        max_context_tokens = 400000

        [profiles.qwen-coder]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "hf-qwen3-coder-next"
        max_context_tokens = 256000

        [profiles.kimi]
        provider = "generic"
        base_url = "http://127.0.0.1:4000"
        model = "kimi-k2"
        max_context_tokens = 200000
        """;
}
