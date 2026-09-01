using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// TypeScript/Node row of the Tier A matrix: the verbatim <c>scripts.test</c>
/// copy, and the shell mismatch the spec names — init smoke-validates a
/// candidate with a direct exec while the pipeline later runs the persisted
/// command through <c>/bin/sh -lc</c>, so the two disagree about <c>&amp;&amp;</c>.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>A command that would be perfectly valid once a shell parses it.</summary>
    private const string ChainedScript = "vitest run && tsc --noEmit";

    /// <summary>
    /// The declared script is copied verbatim — including a <c>&amp;&amp;</c>
    /// chain — into the candidate list and then into the persisted config, for
    /// both <c>testCmd</c> and <c>testFileCmd</c>. In the config BYTES the
    /// ampersands come out as <c>\u0026</c>: the writer uses the default
    /// <c>System.Text.Json</c> encoder, which escapes <c>&amp;</c>, <c>&lt;</c>
    /// and <c>&gt;</c> for HTML safety. It round-trips exactly, so nothing
    /// breaks, but the file a human is invited to hand-edit does not read back
    /// the way they wrote it.
    /// </summary>
    [Fact]
    public async Task Row_Node_ScriptsTestIsCopiedVerbatimIncludingAndChain()
    {
        var root = NewRepo("node");
        try
        {
            Write(root, "package.json", $$"""{ "scripts": { "test": "{{ChainedScript}}" } }""");
            Write(root, "src/index.ts", "export const answer = 42;\n");
            Write(root, "src/index.test.ts");

            Assert.Equal([ChainedScript], TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal(ChainedScript, result.TestCommand);
            Assert.Contains("\"testCmd\": \"vitest run \\u0026\\u0026 tsc --noEmit\"",
                config, StringComparison.Ordinal);
            Assert.Contains("\"testFileCmd\": \"vitest run \\u0026\\u0026 tsc --noEmit\"",
                config, StringComparison.Ordinal);
            // No format script declared → the de-facto Node formatter is inferred.
            Assert.Contains("\"formatCmd\": \"prettier --write .\"", config, StringComparison.Ordinal);

            // The escaping is lossless: the loader hands the pipeline the chain back.
            var loaded = await RelayConfigLoader.TryLoadAsync(root);
            Assert.Equal(ChainedScript, loaded.Config.TestCommand);
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
