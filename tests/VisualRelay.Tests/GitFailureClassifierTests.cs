using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class GitFailureClassifierTests
{
    [Fact]
    public void NotAGitRepository_IsDeterministic()
    {
        Assert.True(GitFailureClassifier.IsDeterministic(128,
            "fatal: not a git repository (or any of the parent directories): .git"));
    }

    [Fact]
    public void InvalidReference_IsDeterministic()
    {
        Assert.True(GitFailureClassifier.IsDeterministic(128,
            "fatal: invalid reference: refs/heads/nonexistent\n"));
    }

    [Fact]
    public void ExitZero_NeverDeterministic()
    {
        Assert.False(GitFailureClassifier.IsDeterministic(0,
            "fatal: not a git repository"));
    }

    [Fact]
    public void IndexLockMessage_IsRetryable()
    {
        // Exit 128 is used for both deterministic and transient failures;
        // the classifier must key on the MESSAGE, not the exit code alone.
        Assert.False(GitFailureClassifier.IsDeterministic(128,
            "fatal: Unable to create '.git/index.lock': File exists."));
    }

    [Fact]
    public void UnknownFatal_IsRetryable()
    {
        Assert.False(GitFailureClassifier.IsDeterministic(128,
            "fatal: something went wrong"));
    }
}
