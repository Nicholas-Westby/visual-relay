namespace VisualRelay.Domain;

public sealed record RelayTaskItem(
    string Id,
    string MarkdownPath,
    string TaskDirectory,
    bool IsNested,
    IReadOnlyList<string> SiblingPaths,
    string? ReviewReason = null)
{
    public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewReason);
    public string StateLabel => NeedsReview ? "Needs review" : "Pending";
}
