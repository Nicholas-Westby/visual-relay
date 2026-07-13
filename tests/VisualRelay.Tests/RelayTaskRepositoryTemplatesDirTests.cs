using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class RelayTaskRepositoryTemplatesDirTests
{
    [Fact]
    public async Task ListPendingAsync_SkipsTemplatesDirectory()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        // Write a task inside the templates/ subdirectory — this must be skipped.
        repo.WriteTask("templates/skeleton", "# Skeleton\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }
}
