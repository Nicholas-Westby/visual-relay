using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A workspace whose git cannot name a committer runs every stage and then fails at the
/// sealed commit. Measured in a fresh WSL distro (user created with an empty full name):
/// the commit said "Author identity unknown ... empty ident name not allowed" after
/// planning and stages 5 to 10 had passed. The gate asks git once, before anything runs.
/// </summary>
public sealed class GitIdentityGateTests
{
    [Fact]
    public async Task IdentityPresent_Passes_AndAsksGitForTheCommitterInTheWorkspace()
    {
        var git = new ScriptedGit(0, "Ada Lovelace <ada@example.com> 1789331389 -0700\n");

        var refusal = await GitIdentityGate.CheckAsync("/home/u/repo", git, insideWsl: false);

        Assert.Null(refusal);
        Assert.Equal(("/home/u/repo", "var GIT_COMMITTER_IDENT"), git.Calls.Single());
    }

    [Fact]
    public async Task IdentityMissing_Refuses_WithTheConfigCommandsAndWhatGitSaid()
    {
        var git = new ScriptedGit(128,
            "Author identity unknown\n\n*** Please tell me who you are.\n\n"
            + "fatal: empty ident name (for <enjay@Hodgman.localdomain>) not allowed\n");

        var refusal = await GitIdentityGate.CheckAsync("/home/u/repo", git, insideWsl: false);

        Assert.NotNull(refusal);
        Assert.Contains("git config --global user.name", refusal);
        Assert.Contains("git config --global user.email", refusal);
        Assert.Contains("fatal: empty ident name", refusal);
        // git's advice repeats the fix the refusal already gives; its fatal line is the reason.
        Assert.DoesNotContain("Please tell me who you are", refusal);
        Assert.DoesNotContain("WSL", refusal);
    }

    [Fact]
    public async Task IdentityMissingInsideWsl_SaysTheFixGoesInsideTheDistro()
    {
        var refusal = await GitIdentityGate.CheckAsync(
            @"\\wsl.localhost\Ubuntu\home\u\repo", new ScriptedGit(128, "Author identity unknown\n"), insideWsl: true);

        Assert.Contains("inside the WSL distro", refusal);
    }

    [Fact]
    public async Task ProbeTimedOut_DoesNotRefuse()
    {
        var refusal = await GitIdentityGate.CheckAsync("/repo", new ScriptedGit(-1, "", timedOut: true), insideWsl: false);

        Assert.Null(refusal);
    }

    private sealed class ScriptedGit(int exitCode, string output, bool timedOut = false) : IGitInvoker
    {
        public List<(string Root, string Argv)> Calls { get; } = [];

        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken killToken = default,
            Action<string>? onActivity = null)
        {
            Calls.Add((rootPath, string.Join(' ', arguments)));
            return Task.FromResult((exitCode, output, timedOut));
        }
    }
}
