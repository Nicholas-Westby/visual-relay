using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// <see cref="RelayDriver.ParseDeletedTrackedPaths"/> extracts status-D paths from a
/// <c>git diff --name-status -z</c> stream for the verify-worktree deletion overlay.
/// The caller keeps <c>--no-renames</c>, so every record is a flat
/// <c>&lt;status&gt;\0&lt;path&gt;\0</c> pair — but the parser must FAIL SAFE if that flag is
/// ever dropped: a rename then surfaces as a 3-token <c>R&lt;score&gt;\0src\0dst\0</c> record
/// that, if walked pairwise, shifts every following status onto a path and could remove
/// the WRONG file (or miss a real deletion). The parser detects rename/copy records and
/// consumes both their paths to stay aligned, and treats only a single-char <c>D</c> as a
/// deletion.
/// </summary>
public sealed class DeletedTrackedPathParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsNothing()
    {
        Assert.Empty(RelayDriver.ParseDeletedTrackedPaths(null));
        Assert.Empty(RelayDriver.ParseDeletedTrackedPaths(""));
    }

    [Fact]
    public void Parse_NoRenamesStream_ExtractsOnlyDeletions()
    {
        // A staged `git mv old->new` under --no-renames surfaces as A new + D old.
        var stream = "A\0new.txt\0D\0old.txt\0M\0edited.txt\0";
        Assert.Equal(new[] { "old.txt" }, RelayDriver.ParseDeletedTrackedPaths(stream));
    }

    [Fact]
    public void Parse_MultipleDeletions_ExtractsAll()
    {
        var stream = "D\0a.txt\0D\0dir/b.txt\0";
        Assert.Equal(new[] { "a.txt", "dir/b.txt" }, RelayDriver.ParseDeletedTrackedPaths(stream));
    }

    // ── DEFENSIVE: if --no-renames is ever dropped, rename records are 3 tokens. ──

    [Fact]
    public void Parse_RenameRecordFollowedByDeletion_StaysAligned_FindsDeletion()
    {
        // R<score>\0src\0dst\0  then  D\0gone\0 . A pairwise walk that does NOT account
        // for the rename's two paths shifts alignment and MISSES the real deletion.
        var stream = "R100\0old.txt\0new.txt\0D\0gone.txt\0";
        Assert.Equal(new[] { "gone.txt" }, RelayDriver.ParseDeletedTrackedPaths(stream));
    }

    [Fact]
    public void Parse_RenameRecordAlone_IsNotTreatedAsDeletion()
    {
        // A rename src must NOT be reported as a plain deletion (fail safe: never
        // over-remove). The new path is carried by the add/modify overlay instead.
        var stream = "R100\0old.txt\0new.txt\0";
        Assert.Empty(RelayDriver.ParseDeletedTrackedPaths(stream));
    }
}
