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
/// </summary>
public sealed class SandboxGradleDaemonTests
{
    [Fact]
    public void BuildSandboxEnvironment_TurnsTheGradleAndKotlinDaemonsOff()
    {
        var env = SandboxedStage.BuildSandboxEnvironment(Config());

        Assert.Equal("-Dorg.gradle.daemon=false", env["GRADLE_OPTS"]);
        Assert.Equal("in-process", env["ORG_GRADLE_PROJECT_kotlin.compiler.execution.strategy"]);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_KeepsTheUsersGradleOptsAndAddsNoDaemon()
    {
        var snapshot = Path.Combine(Path.GetTempPath(), $"vr-gradle-snapshot-{Guid.NewGuid():N}.env");
        try
        {
            File.WriteAllText(snapshot, "PATH=/usr/bin:/bin\0GRADLE_OPTS=-Xmx2g -Dorg.gradle.daemon=true\0");
            var accessor = new DictionaryEnvironmentAccessor { ["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshot };

            var result = SandboxedStage.BuildTargetCommandEnvironment(Config(), accessor);

            Assert.Equal("-Xmx2g -Dorg.gradle.daemon=true -Dorg.gradle.daemon=false", result.Overrides["GRADLE_OPTS"]);
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

        Assert.Equal("-Xmx1g -Dorg.gradle.daemon=false", result.Overrides["GRADLE_OPTS"]);
    }

    private static RelayConfig Config() => RelayConfigLoader.Defaults("./gradlew test");
}
