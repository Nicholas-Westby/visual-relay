namespace VisualRelay.Core.Execution;

public sealed record RelayDriverOptions(bool CreateGitCommit)
{
    public static RelayDriverOptions Default { get; } = new(CreateGitCommit: true);
    public static RelayDriverOptions NoGitCommit { get; } = new(CreateGitCommit: false);
}

