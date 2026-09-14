using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Records of NUL-terminated (<c>-z</c>) git output. The capture appends a line ending after the
/// last read, and that is not a record. Measured on Windows: <c>ls-files --ignored -z</c> came back
/// as <c>vendor/\0\r\n</c>, the <c>\r\n</c> survived the split as an ignored entry, and every
/// in-distro verify overlay warned it could not copy an entry with an empty name.
/// </summary>
public sealed class GitPathOutputNulRecordsTests
{
    [Theory]
    [InlineData("vendor/\0\r\n")]
    [InlineData("vendor/\0\n")]
    [InlineData("vendor/\0")]
    public void TheCapturesLineEnding_IsNotARecord(string output)
    {
        Assert.Equal(["vendor/"], GitPathOutput.SplitNulRecords(output));
    }

    [Fact]
    public void APathEndingInANewline_KeepsIt()
    {
        Assert.Equal(["odd\n", "plain"], GitPathOutput.SplitNulRecords("odd\n\0plain\0\n"));
    }

    [Fact]
    public void NoOutput_HasNoRecords()
    {
        Assert.Empty(GitPathOutput.SplitNulRecords(null));
        Assert.Empty(GitPathOutput.SplitNulRecords("\r\n"));
    }
}
