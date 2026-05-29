namespace VisualRelay.Domain;

public sealed record RelayTaskItem(
    string Id,
    string MarkdownPath,
    string TaskDirectory,
    bool IsNested,
    IReadOnlyList<string> SiblingPaths);

