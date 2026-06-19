using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class TaskOrderStoreTests
{
    [Fact]
    public void SaveThenRead_RoundTripsTheOrder()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);

        store.Save(["gamma", "alpha", "beta"]);

        Assert.Equal(["gamma", "alpha", "beta"], store.Read());
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmptyWithoutThrowing()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void Read_CorruptFile_ReturnsEmptyWithoutThrowing()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "task-order.json"), "{ this is not json ]");
        var store = new TaskOrderStore(repo.Root);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void Apply_OrdersByPersistedRankFirst()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);
        store.Save(["gamma", "beta", "alpha"]);

        // Source list arrives alphabetical (as RelayTaskRepository.ListAsync returns it).
        var ordered = store.Apply(["alpha", "beta", "gamma"], id => id);

        Assert.Equal(["gamma", "beta", "alpha"], ordered);
    }

    [Fact]
    public void Apply_UnrankedTasksFallBackToAlphabeticalAfterRanked()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);
        // Only gamma + alpha have a saved rank; "delta" and "charlie" are new.
        store.Save(["gamma", "alpha"]);

        var ordered = store.Apply(["alpha", "charlie", "delta", "gamma"], id => id);

        // Ranked ids first in saved order, then the unranked ids alphabetically.
        Assert.Equal(["gamma", "alpha", "charlie", "delta"], ordered);
    }

    [Fact]
    public void Apply_StaleIdsInFileAreIgnored()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);
        // "ghost" was deleted/renamed since the order was saved.
        store.Save(["ghost", "beta", "alpha"]);

        var ordered = store.Apply(["alpha", "beta"], id => id);

        Assert.Equal(["beta", "alpha"], ordered);
    }

    [Fact]
    public void Apply_EmptyPersistedOrder_PreservesSourceOrder()
    {
        using var repo = TestRepository.Create();
        var store = new TaskOrderStore(repo.Root);

        // No file → fall back to whatever order the source already has (alphabetical).
        var ordered = store.Apply(["alpha", "beta", "gamma"], id => id);

        Assert.Equal(["alpha", "beta", "gamma"], ordered);
    }
}
