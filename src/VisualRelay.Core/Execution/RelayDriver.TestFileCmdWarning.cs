using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Returns a warning when <paramref name="config"/>.TestFileCommand is set but
    /// contains no <c>{files}</c> token — so <see cref="BuildTargetedTestCommand"/>
    /// and the stage-5 red gate silently degrade to the full <c>testCmd</c> suite
    /// instead of narrowing to the changed/authored files. Returns null when the
    /// command is blank (no targeting configured) or already contains <c>{files}</c>.
    /// A footgun for any project; surfaced at run start.
    /// </summary>
    internal static string? TestFileCommandWarning(RelayConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TestFileCommand)
            || config.TestFileCommand.Contains("{files}", StringComparison.Ordinal))
            return null;
        return $"testFileCmd has no {{files}} token (\"{config.TestFileCommand}\") — the " +
            "stage-5 red gate runs the FULL test suite instead of only the changed/authored " +
            "files, making every gate slow and widening the window for orphaned test " +
            "processes. Add a {files} placeholder (e.g. \"bun test {files}\") so the gate narrows.";
    }

    // Surface the {files}-less testFileCmd footgun at run start as a warn event.
    private async Task WarnTestFileCmdAsync(
        RelayConfig config, string runId, string rootPath, string taskId, CancellationToken ct)
    {
        if (TestFileCommandWarning(config) is not { } warning)
            return;
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "testfilecmd_no_files_token", runId, rootPath, taskId,
            Data: new Dictionary<string, string>
            {
                ["message"] = warning,
                ["testFileCmd"] = config.TestFileCommand
            }), ct);
    }

    /// <summary>
    /// Surfaces the no-op placeholder gate at run start. The placeholder exists so a
    /// greenfield folder is runnable, but on a repo whose detection simply failed it
    /// makes Verify report a green gate having run no tests at all — silently. A warn
    /// event is the only thing standing between that and a committed, untested change.
    /// </summary>
    private async Task WarnPlaceholderTestCommandAsync(
        RelayConfig config, string runId, string rootPath, string taskId, CancellationToken ct)
    {
        if (!ProjectBootstrapper.IsPlaceholder(config.TestCommand)) return;
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "test_command_placeholder", runId, rootPath, taskId,
            Data: new Dictionary<string, string>
            {
                ["message"] =
                    "testCmd is the visual-relay placeholder: it exits 0 without running any "
                    + "tests, so every gate in this run is vacuous. Set a real testCmd in "
                    + ".relay/config.json before trusting a green Verify.",
                ["testCmd"] = config.TestCommand
            }), ct);
    }
}
