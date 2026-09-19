using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A workspace sandboxed inside a WSL distro names its extra grants as the distro sees them and
/// is checked against the distro user's home. Measured on the Windows arm with litedb-org/LiteDB:
/// the entry /home/alice/.nuget/NuGet was checked against the Windows profile and refused, and
/// the refusal emptied the task list.
/// </summary>
public sealed class SandboxExtraAllowPathsWslTests
{
    private static readonly WslContext VrNoSuchDistro = new("wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");

    [Theory]
    [InlineData("/home/alice/.nuget/NuGet", "/home/alice/.nuget/NuGet")]
    [InlineData("~/.nuget/NuGet", "/home/alice/.nuget/NuGet")]
    [InlineData("$HOME/.nuget/NuGet", "/home/alice/.nuget/NuGet")]
    [InlineData(@"\\wsl.localhost\VrNoSuchDistro\home\alice\.cache\tool", "/home/alice/.cache/tool")]
    [InlineData("/home/alice//.m2/./repository/", "/home/alice/.m2/repository")]
    public async Task AnEntryUnderTheDistroHome_IsGrantedInItsLinuxForm(string entry, string expected)
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, VrNoSuchDistro);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal([expected], result.Config.SandboxExtraAllowPaths!);
    }

    [Theory]
    [InlineData("/etc/nuget")]
    [InlineData("/home/other/.nuget")]
    [InlineData("/home/alice-other/.nuget")]
    [InlineData(@"\\wsl.localhost\VrNoSuchOtherDistro\home\alice\.cache")]
    [InlineData(@"C:\Users\alice\.nuget")]
    [InlineData("~/.ssh")]
    [InlineData("/home/alice/.aws/credentials")]
    [InlineData("~/../other")]
    public async Task AnEntryOutsideTheDistroHomeOrIntoASecret_RefusesTheConfig(string entry)
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, VrNoSuchDistro);

        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.Contains("sandboxExtraAllowPaths", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_NamesTheHomeTheEntryWasCheckedAgainst()
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, "/opt/tool");

        var result = await LoadInDistroAsync(repo, VrNoSuchDistro);

        Assert.Contains("/home/alice", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryUnderTheWorkspace_IsGrantedAsTheDistroSeesTheWorkspace()
    {
        using var repo = TestRepository.Create();
        var entry = Path.Combine(repo.Root, "build-cache");
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, VrNoSuchDistro);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal([SandboxHost.Windows(VrNoSuchDistro).MapGrant(entry)!], result.Config.SandboxExtraAllowPaths!);
    }

    [Fact]
    public async Task WithNoDistroResolved_TheConfigStillLoadsWithoutTheEntries()
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, "/home/alice/.nuget/NuGet");

        var result = await LoadInDistroAsync(repo, null);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Empty(result.Config.SandboxExtraAllowPaths!);
    }

    private static Task<RelayConfigResult> LoadInDistroAsync(TestRepository repo, WslContext? wsl) =>
        RelayConfigLoader.TryLoadAsync(repo.Root, (_, _) => Task.FromResult(SandboxHost.Windows(wsl)), CancellationToken.None);

    private static async Task WriteConfigAsync(TestRepository repo, string entry)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["{{entry.Replace("\\", "\\\\")}}"]
            }
            """);
    }
}
