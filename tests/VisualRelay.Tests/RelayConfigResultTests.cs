using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigResultTests
{
    private static RelayConfig AnyConfig() =>
        new("llm-tasks", "dotnet test", "bun test {files}", [],
            new Dictionary<string, string>(), 5, 3, 200, true, true, 1_200_000, 300_000);

    [Theory]
    [InlineData(RelayConfigStatus.Loaded, true, false)]
    [InlineData(RelayConfigStatus.Defaulted, false, true)]
    [InlineData(RelayConfigStatus.Incomplete, false, true)]
    [InlineData(RelayConfigStatus.Malformed, false, false)]
    public void Flags_FollowStatus(RelayConfigStatus status, bool runnable, bool needsInit)
    {
        var result = new RelayConfigResult(AnyConfig(), status, status == RelayConfigStatus.Malformed ? "bad" : null);
        Assert.Equal(runnable, result.IsRunnable);
        Assert.Equal(needsInit, result.NeedsInitialization);
    }
}
