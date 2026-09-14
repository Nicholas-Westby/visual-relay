using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Ruby and CMake markers. Both repos previously fell through every candidate to the
/// weak <c>tests/</c> → pytest guess, which exits 127 and leaves a placeholder test
/// command behind — a gate that reports green having run nothing.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    [Fact]
    public void DetectCandidates_Gemfile_OffersRakeTestThenBareRake()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Gemfile"), "source 'https://rubygems.org'\n");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal(["bundle exec rake test", "bundle exec rake"], candidates);
    }

    /// <summary>
    /// An RSpec project often has no rake test task: measured with ruby-grape/grape, bootstrap's
    /// <c>bundle exec rake test</c> answered "Don't know how to build task 'test'". A .rspec
    /// file or a spec directory names the runner, so rspec is tried first.
    /// </summary>
    /// <param name="marker">The RSpec marker the repo carries.</param>
    /// <param name="isDirectory">Whether the marker is a directory.</param>
    [Theory]
    [InlineData(".rspec", false)]
    [InlineData("spec", true)]
    public void DetectCandidates_GemfileWithAnRspecMarker_OffersRspecFirst(string marker, bool isDirectory)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Gemfile"), "source 'https://rubygems.org'\n");
        if (isDirectory) Directory.CreateDirectory(Path.Combine(repo.Root, marker));
        else File.WriteAllText(Path.Combine(repo.Root, marker), "--require spec_helper\n");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal(["bundle exec rspec", "bundle exec rake test", "bundle exec rake"], candidates);
    }

    [Fact]
    public void DetectCandidates_RakefileWithoutGemfile_StillOffersRake()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Rakefile"), "task :test\n");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Contains("bundle exec rake test", candidates);
    }

    [Fact]
    public void DetectCandidates_CMakeLists_OffersConfigureBuildAndCtest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "CMakeLists.txt"), "project(demo)\n");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal(
            ["cmake -S . -B build && cmake --build build && ctest --test-dir build --output-on-failure"],
            candidates);
    }

    [Fact]
    public void DetectCandidates_RubyRepoWithTestsDirectory_RanksRakeAboveTheWeakPytestGuess()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Gemfile"), "source 'https://rubygems.org'\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "test"));

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.True(
            candidates.ToList().IndexOf("bundle exec rake test") < candidates.ToList().IndexOf("pytest"),
            $"rake must be tried before the pytest guess; got {string.Join(", ", candidates)}");
    }

    [Fact]
    public void DetectCandidates_CMakeRepoWithTestsDirectory_RanksCtestAboveTheWeakPytestGuess()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "CMakeLists.txt"), "project(demo)\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.StartsWith("cmake -S . -B build", candidates[0], StringComparison.Ordinal);
        Assert.Contains("pytest", candidates);
    }
}
