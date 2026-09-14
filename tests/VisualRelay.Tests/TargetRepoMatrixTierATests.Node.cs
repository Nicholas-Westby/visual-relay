using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// TypeScript/Node row of the Tier A matrix: a <c>scripts.test</c> runs through npm,
/// and the shell mismatch the spec names — init smoke-validates a candidate with a
/// direct exec while the pipeline later runs the persisted command through
/// <c>/bin/sh -lc</c>, so the two disagree about <c>&amp;&amp;</c>.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>A command that would be perfectly valid once a shell parses it.</summary>
    private const string ChainedScript = "vitest run && tsc --noEmit";

    /// <summary>
    /// The declared script, chain and all, runs through npm the way the project runs it, so the
    /// config names <c>npm test</c> rather than a copy of the script. A chained script has no
    /// trailing argument list, so no per-file form is seeded.
    /// </summary>
    [Fact]
    public async Task Row_Node_ChainedTestScriptRunsThroughNpm()
    {
        var root = NewRepo("node");
        try
        {
            Write(root, "package.json", $$"""{ "scripts": { "test": "{{ChainedScript}}" } }""");
            Write(root, "src/index.ts", "export const answer = 42;\n");
            Write(root, "src/index.test.ts");

            Assert.Equal(["npm test"], TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("npm test", result.TestCommand);
            Assert.Contains("\"testFileCmd\": null", config, StringComparison.Ordinal);
            // No format script and no prettier config → no formatter is inferred, so the
            // repo is never reformatted by a tool it did not ask for.
            Assert.DoesNotContain("\"formatCmd\"", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// A script whose runner is jest keeps jest's targeted form beside <c>npm test</c>.
    /// moment/luxon's script is "jest --coverage".
    /// </summary>
    [Fact]
    public async Task Row_Node_JestScriptKeepsItsTargetedForm()
    {
        var root = NewRepo("node-jest");
        try
        {
            Write(root, "package.json", """{ "scripts": { "test": "jest --coverage" } }""");
            Write(root, "src/index.js", "module.exports = 42;\n");

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("npm test", result.TestCommand);
            Assert.Contains("\"testFileCmd\": \"npx jest {files}\"", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The pipeline half of the mismatch: the persisted command reaches
    /// <c>/bin/sh -lc</c> as ONE argv entry, so the shell — not Visual Relay —
    /// parses the <c>&amp;&amp;</c> and the chain runs as written.
    /// </summary>
    [Fact]
    public void ShellMismatch_PipelineHandsTheWholeChainToBinSh()
    {
        var (fileName, arguments) = ShellTestRunner.BuildShellLaunch(ChainedScript, isWindows: false);

        Assert.Equal("/bin/sh", fileName);
        Assert.Equal(["-lc", ChainedScript], arguments);
    }

    /// <summary>
    /// The init half of the mismatch, and it is still real. Init validates with
    /// <see cref="DirectExecTestRunner"/>, which splits the string on
    /// whitespace and execs argv[0] with no shell: <c>true</c> is handed
    /// <c>&amp;&amp;</c> and the absent binary as plain ARGUMENTS, ignores
    /// them, and exits 0 — so the right-hand side of the chain is never
    /// evaluated and the candidate is accepted. Under <c>/bin/sh</c> the same
    /// string fails at the missing binary. The two paths therefore reach
    /// opposite verdicts on one command string.
    /// </summary>
    [Fact]
    public async Task ShellMismatch_InitDirectExecNeverEvaluatesTheRightHandSide()
    {
        var root = NewRepo("shell-mismatch");
        try
        {
            var command = $"true && vr-absent-{Guid.NewGuid():N}";
            var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(30));

            var validation = await new TestCommandValidator(runner).ValidateAsync(root, command);

            Assert.Equal(0, validation.RunResult.ExitCode);
            Assert.True(validation.Accepted, validation.RejectionReason);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The consequence for a real Node repo: when the chain's leading token is
    /// a runner that is not on PATH as a standalone binary — which is the norm,
    /// because <c>vitest</c>/<c>jest</c> live in <c>node_modules/.bin</c> and
    /// are only on PATH inside an <c>npm</c>-spawned shell — the direct exec
    /// returns ENOENT-as-127 and the candidate is rejected at init, even though
    /// the very same string would run under the pipeline's <c>/bin/sh -lc</c>.
    /// Init then falls through to the placeholder.
    /// </summary>
    [Fact]
    public async Task ShellMismatch_AbsentLeadingBinaryIsRejectedAtInitAsCommandNotFound()
    {
        var root = NewRepo("shell-mismatch-enoent");
        try
        {
            var command = $"vr-absent-{Guid.NewGuid():N} run && tsc --noEmit";
            var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(30));

            var validation = await new TestCommandValidator(runner).ValidateAsync(root, command);

            Assert.Equal(127, validation.RunResult.ExitCode);
            Assert.False(validation.Accepted);
            Assert.Contains("command not found", validation.RejectionReason!, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
