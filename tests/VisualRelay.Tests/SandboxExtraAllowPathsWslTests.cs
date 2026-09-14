using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A workspace sandboxed inside a WSL distro names its extra grants as the distro sees them and
/// is checked against the distro user's home. Measured on the Windows arm with litedb-org/LiteDB:
/// the entry /home/enjay/.nuget/NuGet was checked against the Windows profile and refused, and
/// the refusal emptied the task list.
/// </summary>
public sealed class SandboxExtraAllowPathsWslTests
{
    private static readonly WslContext Ubuntu = new("wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/enjay");

    [Theory]
    [InlineData("/home/enjay/.nuget/NuGet", "/home/enjay/.nuget/NuGet")]
    [InlineData("~/.nuget/NuGet", "/home/enjay/.nuget/NuGet")]
    [InlineData("$HOME/.nuget/NuGet", "/home/enjay/.nuget/NuGet")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\enjay\.cache\tool", "/home/enjay/.cache/tool")]
    [InlineData("/home/enjay//.m2/./repository/", "/home/enjay/.m2/repository")]
    public async Task AnEntryUnderTheDistroHome_IsGrantedInItsLinuxForm(string entry, string expected)
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, Ubuntu);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal([expected], result.Config.SandboxExtraAllowPaths!);
    }

    [Theory]
    [InlineData("/etc/nuget")]
    [InlineData("/home/other/.nuget")]
    [InlineData("/home/enjay-other/.nuget")]
    [InlineData(@"\\wsl.localhost\Debian\home\enjay\.cache")]
    [InlineData(@"C:\Users\enjay\.nuget")]
    [InlineData("~/.ssh")]
    [InlineData("/home/enjay/.aws/credentials")]
    [InlineData("~/../other")]
    public async Task AnEntryOutsideTheDistroHomeOrIntoASecret_RefusesTheConfig(string entry)
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, Ubuntu);

        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.Contains("sandboxExtraAllowPaths", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_NamesTheHomeTheEntryWasCheckedAgainst()
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, "/opt/tool");

        var result = await LoadInDistroAsync(repo, Ubuntu);

        Assert.Contains("/home/enjay", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryUnderTheWorkspace_IsGrantedAsTheDistroSeesTheWorkspace()
    {
        using var repo = TestRepository.Create();
        var entry = Path.Combine(repo.Root, "build-cache");
        await WriteConfigAsync(repo, entry);

        var result = await LoadInDistroAsync(repo, Ubuntu);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal([SandboxHost.Windows(Ubuntu).MapGrant(entry)!], result.Config.SandboxExtraAllowPaths!);
    }

    [Fact]
    public async Task WithNoDistroResolved_TheConfigStillLoadsWithoutTheEntries()
    {
        using var repo = TestRepository.Create();
        await WriteConfigAsync(repo, "/home/enjay/.nuget/NuGet");

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
