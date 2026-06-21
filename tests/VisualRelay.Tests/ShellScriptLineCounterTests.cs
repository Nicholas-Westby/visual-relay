using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ShellScriptLineCounter.CountLogicLines"/> —
/// excludes blank lines, full-line comments (including the hashbang),
/// and here-doc bodies; includes inline-comment lines as logic.
/// Written TDD: these must fail against absent code.
/// </summary>
public sealed class ShellScriptLineCounterTests
{
    // ── Blank lines ─────────────────────────────────────────────────

    [Fact]
    public void BlankLines_AreExcluded()
    {
        var lines = new[] { "", "   ", "\t", "echo hello" };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, ShellScriptLineCounter.CountLogicLines(Array.Empty<string>()));
    }

    // ── Full-line comments ──────────────────────────────────────────

    [Fact]
    public void FullLineComments_AreExcluded()
    {
        var lines = new[]
        {
            "# this is a comment",
            "   # indented comment",
            "echo hello",
            "# another comment"
        };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void Hashbang_IsExcluded()
    {
        var lines = new[]
        {
            "#!/usr/bin/env bash",
            "echo hello"
        };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void Hashbang_WithSetE_IsExcluded()
    {
        var lines = new[]
        {
            "#!/bin/bash",
            "set -euo pipefail",
            "echo hello"
        };
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    // ── Inline comments count as logic ──────────────────────────────

    [Fact]
    public void InlineComment_CountsAsLogic()
    {
        var lines = new[]
        {
            "echo hello  # this is an inline comment",
        };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void InlineCommentAfterIndentedCode_CountsAsLogic()
    {
        var lines = new[]
        {
            "    cat file  # show file contents",
        };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    // ── Here-doc — basic forms ──────────────────────────────────────

    [Fact]
    public void HereDoc_BodyIsExcluded_ClosingDelimiterIsExcluded()
    {
        var lines = new[]
        {
            "cat <<EOF",
            "line one",
            "line two",
            "EOF",
            "echo after"
        };
        // cat <<EOF counts, echo after counts. Body (2 lines) + closing EOF excluded.
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void HereDoc_WithSingleQuotedDelimiter_BodyExcluded()
    {
        var lines = new[]
        {
            "cat <<'MSG'",
            "$variables are not expanded",
            "MSG",
            "echo done"
        };
        // cat <<'MSG' counts, echo done counts.
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void HereDoc_WithDashDelimiter_BodyExcluded()
    {
        var lines = new[]
        {
            "cat <<-EOF",
            "\tindented content",
            "\tEOF",
            "echo done"
        };
        // cat <<-EOF counts, echo done counts. The indented closing EOF is still
        // the closing delimiter (dash allows leading tabs on the closing delimiter).
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void HereDoc_OpeningLineContainsCode_BodyStillExcluded()
    {
        var lines = new[]
        {
            "cat <<EOF > output.txt",
            "body line",
            "EOF",
            "echo done"
        };
        // cat <<EOF > output.txt counts (1), body excluded, closing EOF excluded,
        // echo done counts (1).
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void HereDoc_MultipleHereDocs_EachBodyExcluded()
    {
        var lines = new[]
        {
            "cat <<EOF",
            "first body",
            "EOF",
            "cat <<MSG",
            "second body",
            "MSG",
            "echo all done"
        };
        // cat <<EOF (1) + cat <<MSG (1) + echo all done (1) = 3
        Assert.Equal(3, ShellScriptLineCounter.CountLogicLines(lines));
    }

    // ── Realistic scripts ───────────────────────────────────────────

    [Fact]
    public void ThinWrapper_ThreeLines_CountsThree()
    {
        var lines = new[]
        {
            "#!/usr/bin/env bash",
            "set -euo pipefail",
            "dotnet run --project \"$SCRIPT_DIR/tools/Foo/Foo.csproj\" -- \"$@\""
        };
        // Hashbang excluded (full-line comment), set -euo pipefail + dotnet run = 2
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void BranchingScript_IfThenFi_CountsBranchLines()
    {
        var lines = new[]
        {
            "#!/bin/bash",
            "if [[ -f somefile ]]; then",
            "  echo file exists",
            "else",
            "  echo file missing",
            "fi"
        };
        // Hashbang excluded. if, echo, else, echo, fi = 5
        Assert.Equal(5, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void WhileLoop_CountsBranchLines()
    {
        var lines = new[]
        {
            "#!/bin/bash",
            "while read -r line; do",
            "  echo \"$line\"",
            "done < input.txt"
        };
        // Hashbang excluded. while, echo, done = 3
        Assert.Equal(3, ShellScriptLineCounter.CountLogicLines(lines));
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Fact]
    public void LineStartingWithSpacesThenHash_IsFullLineComment()
    {
        var lines = new[]
        {
            "    # indented full-line comment",
            "echo hello"
        };
        Assert.Equal(1, ShellScriptLineCounter.CountLogicLines(lines));
    }

    [Fact]
    public void HereDocWordAppearingAsSubstring_DoesNotCloseHereDoc()
    {
        // "EOF" appears inside a longer word — not the closing delimiter.
        var lines = new[]
        {
            "cat <<EOF",
            "  echo NOTEOF",
            "EOF",
            "echo done"
        };
        Assert.Equal(2, ShellScriptLineCounter.CountLogicLines(lines));
    }
}
