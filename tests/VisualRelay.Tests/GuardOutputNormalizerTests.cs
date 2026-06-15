using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Direct unit tests for <see cref="RelayDriver.NormalizeForComparison"/>,
/// the digit-normalization helper that makes the baseline guard diff
/// count-insensitive.
/// </summary>
public sealed class GuardOutputNormalizerTests
{
    /// <summary>
    /// Two guard lines that differ only in the embedded live line count
    /// must normalize to the same key so <c>ExceptWith</c> excludes the
    /// pre-existing file.
    /// </summary>
    [Fact]
    public void NormalizeForComparison_CountOnlyDifference_CollapsesToSameKey()
    {
        var a = RelayDriver.NormalizeForComparison(
            "file too large: src/big.cs has 332 lines (limit 300)");
        var b = RelayDriver.NormalizeForComparison(
            "file too large: src/big.cs has 333 lines (limit 300)");

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Lines referring to different file paths must produce different keys
    /// — only the digit runs are normalized, not the structural text.
    /// </summary>
    [Fact]
    public void NormalizeForComparison_DifferentPaths_ProduceDifferentKeys()
    {
        var a = RelayDriver.NormalizeForComparison(
            "file too large: src/foo.cs has 332 lines (limit 300)");
        var b = RelayDriver.NormalizeForComparison(
            "file too large: src/bar.cs has 332 lines (limit 300)");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// A line with no digits at all passes through unchanged.
    /// </summary>
    [Fact]
    public void NormalizeForComparison_NoDigits_ReturnsUnchanged()
    {
        Assert.Equal("abc", RelayDriver.NormalizeForComparison("abc"));
    }
}
