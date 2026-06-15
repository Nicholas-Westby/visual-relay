using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderFormatCmdTests
{
    [Fact]
    public async Task FormatCmd_AbsentFromJson_IsNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Null(config.FormatCommand);
    }

    [Fact]
    public async Task FormatCmd_PresentInJson_IsRead()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [], "formatCmd": "cargo fmt" }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal("cargo fmt", config.FormatCommand);
    }

    [Fact]
    public async Task FormatCmd_BlankInJson_IsNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [], "formatCmd": "   " }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Null(config.FormatCommand);
    }
}
