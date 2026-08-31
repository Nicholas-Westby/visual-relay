using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The command guard, applied IN-PROCESS on every command a tool runs.
/// <para>Swival reached the guard as an external binary wired through
/// <c>--command-middleware</c>, which <c>ProcessRunners.BuildArguments</c> only
/// passes when <c>&lt;targetRoot&gt;/.githooks/command-guard</c> exists. Visual Relay
/// never provisions that file into a target repository, so every repository except
/// this one ran with no command guard at all, and swival's <c>python</c> tool
/// bypassed the middleware entirely (89 uninspected calls in this repo's history).
/// Calling <see cref="CommandGuardDecider"/> directly closes both holes: the policy
/// now applies in every repository, to every command tool, with no subprocess.</para>
/// <para>The policy itself is unchanged — the same decider the
/// <c>VisualRelay.CommandGuard</c> binary wraps, fed the same payload shape, so
/// <c>--no-verify</c> is stripped unconditionally, <c>-n</c> and a combined short
/// flag's <c>n</c> are stripped only inside a <c>git commit</c>, and a malformed
/// payload fails open for non-git and fails CLOSED for a git commit.</para>
/// </summary>
internal static class AgentCommandGuard
{
    /// <summary>What the guard decided about one command.</summary>
    /// <param name="DenyReason">Non-null when the command must not run.</param>
    /// <param name="Argv">The argv to run, for an argv-mode command.</param>
    /// <param name="Shell">The shell string to run, for a shell-mode command.</param>
    /// <param name="Rewritten">True when the guard stripped hook-bypass flags.</param>
    internal sealed record Verdict(
        string? DenyReason, IReadOnlyList<string>? Argv, string? Shell, bool Rewritten);

    /// <summary>Told to the model whenever the guard rewrote its command.</summary>
    public const string RewrittenNotice =
        "Note: the command guard removed git hook-bypass flags (--no-verify / -n) before running "
        + "this command. The repository's commit-authority hook is not optional.";

    /// <summary>Inspects an argv-form command.</summary>
    /// <param name="toolName">The calling tool, recorded in the payload.</param>
    /// <param name="argv">The program and its arguments.</param>
    /// <returns>The verdict.</returns>
    public static Verdict Inspect(string toolName, IReadOnlyList<string> argv)
    {
        var command = new JsonArray();
        foreach (var token in argv)
            command.Add(token);

        return Decide(
            new JsonObject
            {
                ["phase"] = "before",
                ["tool"] = toolName,
                ["mode"] = "argv",
                ["command"] = command,
            },
            argv,
            shell: null);
    }

    /// <summary>Inspects a shell-form command.</summary>
    /// <param name="toolName">The calling tool, recorded in the payload.</param>
    /// <param name="command">The shell command line.</param>
    /// <returns>The verdict.</returns>
    public static Verdict Inspect(string toolName, string command) =>
        Decide(
            new JsonObject
            {
                ["phase"] = "before",
                ["tool"] = toolName,
                ["mode"] = "shell",
                ["command"] = command,
            },
            argv: null,
            command);

    /// <summary>
    /// Turns a decider verdict into the verdict the executor acts on. Split out so
    /// the deny mapping — the fail-CLOSED half of the policy, which a well-formed
    /// payload cannot reach on demand — is directly testable.
    /// </summary>
    /// <param name="result">What the decider returned.</param>
    /// <param name="argv">The original argv, for an argv-mode command.</param>
    /// <param name="shell">The original shell string, for a shell-mode command.</param>
    /// <returns>The verdict.</returns>
    public static Verdict Interpret(
        CommandGuardResult result, IReadOnlyList<string>? argv, string? shell)
    {
        if (result.IsDeny)
            return new Verdict(result.Reason ?? "blocked by the command guard", null, null, false);

        if (result.IsRewritten)
        {
            if (result is { Mode: "argv", Command: IReadOnlyList<string> rewrittenArgv })
                return new Verdict(null, rewrittenArgv, null, true);

            if (result is { Mode: "shell", Command: string rewrittenShell })
                return new Verdict(null, null, rewrittenShell, true);
        }

        return new Verdict(null, argv, shell, false);
    }

    // The raw JSON is handed to the decider alongside the parsed element for the
    // same reason Program.cs does it: it is what the catch path scans to fail
    // CLOSED on a git commit it could not safely inspect.
    private static Verdict Decide(
        JsonObject payload, IReadOnlyList<string>? argv, string? shell)
    {
        var rawJson = payload.ToJsonString();
        using var document = JsonDocument.Parse(rawJson);
        return Interpret(CommandGuardDecider.Decide(document.RootElement, rawJson), argv, shell);
    }
}
