using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// No Gradle daemon inside the sandbox. Measured on the Windows arm with nono 0.75 and VR's own
/// profile: a sandboxed gradle client handed its build over loopback to a daemon started outside
/// the sandbox, which then wrote a file no grant allowed; and a daemon started inside one sandbox
/// kept that sandbox's rules, so a later sandboxed build in another directory could not write its
/// own build output. Every sandboxed command, the tests and the agents' own, gets this environment.
/// Every setting rides in GRADLE_OPTS: dash, Ubuntu's /bin/sh and the shell gradle's own launcher
/// starts in, drops an environment variable whose name holds a dot, so an ORG_GRADLE_PROJECT_ name
/// for the Kotlin property never reached gradle.
/// </summary>
public sealed class SandboxGradleDaemonTests
{
    [Fact]
    public void BuildSandboxEnvironment_TurnsTheGradleAndKotlinDaemonsOff()
    {
        var env = SandboxedStage.BuildSandboxEnvironment(Config());

        Assert.Equal(NoDaemons, env["GRADLE_OPTS"]);
    }

    [Fact]
    public void BuildSandboxEnvironment_NamesOnlyVariablesAPosixShellKeeps()
    {
        var env = SandboxedStage.BuildSandboxEnvironment(Config());

        Assert.All(env.Keys, key => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", key));
    }

    [Fact]
    public void BuildTargetCommandEnvironment_KeepsTheUsersGradleOptsAndAddsNoDaemons()
    {
        var snapshot = Path.Combine(Path.GetTempPath(), $"vr-gradle-snapshot-{Guid.NewGuid():N}.env");
        try
        {
            File.WriteAllText(snapshot, "PATH=/usr/bin:/bin\0GRADLE_OPTS=-Xmx2g -Dorg.gradle.daemon=true\0");
            var accessor = new DictionaryEnvironmentAccessor { ["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshot };

            var result = SandboxedStage.BuildTargetCommandEnvironment(Config(), accessor);

            Assert.Equal($"-Xmx2g -Dorg.gradle.daemon=true {NoDaemons}", result.Overrides["GRADLE_OPTS"]);
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    [Fact]
    public void BuildTargetCommandEnvironment_WithoutASnapshot_AppendsToTheProcessGradleOpts()
    {
        var processEnv = new Dictionary<string, string> { ["GRADLE_OPTS"] = "-Xmx1g" };

        var result = SandboxedStage.BuildTargetCommandEnvironment(
            Config(), new DictionaryEnvironmentAccessor(), processEnv);

        Assert.Equal($"-Xmx1g {NoDaemons}", result.Overrides["GRADLE_OPTS"]);
    }

    // The plain kotlin.compiler.execution.strategy system property is ignored by the Kotlin Gradle
    // plugin (2.4.10 on the Windows arm); the project-property form is read.
    private const string NoDaemons =
        "-Dorg.gradle.daemon=false -Dorg.gradle.project.kotlin.compiler.execution.strategy=in-process";

    private static RelayConfig Config() => RelayConfigLoader.Defaults("./gradlew test");
}
