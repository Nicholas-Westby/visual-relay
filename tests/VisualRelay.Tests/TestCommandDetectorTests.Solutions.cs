using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// With more than one solution file at the root, a bare <c>dotnet test</c> does not run: it answers
/// "Found more than one solution file in ... Specify which one to use." Measured on the Mac with
/// ThreeMammals/Ocelot (Ocelot.slnx and Ocelot.Samples.slnx). Each solution becomes its own
/// candidate, the shortest name first, which is usually the project's main solution.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    [Fact]
    public void DetectCandidates_SeveralSolutionFiles_OffersOneCommandPerSolution()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Ocelot.Samples.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(repo.Root, "Ocelot.slnx"), "<Solution />");

        Assert.Equal(["dotnet test Ocelot.slnx", "dotnet test Ocelot.Samples.slnx"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    [Fact]
    public void DetectCandidates_OneSolutionFile_KeepsTheBareCommand()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project />");

        Assert.Equal(["dotnet test"], TestCommandDetector.DetectCandidates(repo.Root));
    }
}
