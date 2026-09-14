using System.Collections;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

internal sealed record TargetCommandEnvironment(
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlySet<string> Remove);

public static partial class SandboxedStage
{
    // Build environment overrides shared by every nono-wrapped invocation.
    // These belong to the TARGET's own commands: they stop `dotnet test` leaving
    // orphaned descendants that outlive the finished tests and keep the nono
    // wrapper alive past completion (the stage-5/9 timeout-after-tests-pass).
    //
    // The Python-containment overrides that used to sit here — the HF, XDG and uv
    // cache redirects into the retired agent's own config dir, and the bytecode and
    // encoding settings — existed because a Python subprocess ran inside this sandbox.
    // Nothing Python runs here any more, so they went with it. A target repo that
    // is itself Python keeps its own environment; nono's own profile still grants
    // the uv cache paths.
    internal static IReadOnlyDictionary<string, string> BuildSandboxEnvironment(RelayConfig config)
    {
        // MSBUILDDISABLENODEREUSE=1 makes MSBuild node-reuse workers exit instead
        // of lingering past the tests; the telemetry opt-out drops the background
        // uploader. Both are ignored by non-.NET targets. (UseSharedCompilation=false
        // in the configured testCmd already disables the Roslyn build server.)
        //
        // No Gradle or Kotlin daemon either. Measured on the Windows arm: a sandboxed gradle handed
        // its build over loopback to a daemon started outside the sandbox, whose tasks then wrote
        // where no grant allowed, and a daemon started in one sandbox kept its rules for the next
        // build, which then could not write its own output.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["GRADLE_OPTS"] = NoGradleDaemon,
            ["ORG_GRADLE_PROJECT_kotlin.compiler.execution.strategy"] = "in-process",
        };
    }

    private const string NoGradleDaemon = "-Dorg.gradle.daemon=false";

    // The user's own GRADLE_OPTS (heap size and the like) stay; the daemon switch comes last so it wins.
    private static string WithNoGradleDaemon(string? existing) =>
        string.IsNullOrWhiteSpace(existing) ? NoGradleDaemon : $"{existing} {NoGradleDaemon}";

    /// <summary>
    /// Builds the target-repo command environment by starting from the
    /// pre-devshell user snapshot (when available), then applying VR's sandbox
    /// overrides on top. When no snapshot exists — packaged/brew installs, or
    /// the env var is absent — falls back to returning the VR overrides alone
    /// (the current process env is the base, matching existing behavior).
    /// </summary>
    internal static TargetCommandEnvironment BuildTargetCommandEnvironment(
        RelayConfig config, IEnvironmentAccessor? accessor = null,
        IReadOnlyDictionary<string, string>? processEnv = null)
    {
        processEnv ??= SnapshotProcessEnv();

        var snapshot = UserEnvSnapshot.Load(accessor);
        if (snapshot is null || !snapshot.ContainsKey("PATH"))
        {
            var overrides = new Dictionary<string, string>(BuildSandboxEnvironment(config))
            {
                ["GRADLE_OPTS"] = WithNoGradleDaemon(processEnv.GetValueOrDefault("GRADLE_OPTS")),
            };
            return new TargetCommandEnvironment(overrides, new HashSet<string>());
        }

        var merged = new Dictionary<string, string>(snapshot!);
        foreach (var kvp in BuildSandboxEnvironment(config))
            merged[kvp.Key] = kvp.Value;
        merged["GRADLE_OPTS"] = WithNoGradleDaemon(snapshot.GetValueOrDefault("GRADLE_OPTS"));

        var remove = new HashSet<string>();
        foreach (var key in processEnv.Keys)
        {
            if (!merged.ContainsKey(key))
                remove.Add(key);
        }

        return new TargetCommandEnvironment(merged, remove);
    }

    private static IReadOnlyDictionary<string, string> SnapshotProcessEnv()
    {
        var dict = new Dictionary<string, string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string key, Value: string value })
                dict[key] = value;
        }
        return dict;
    }
}
