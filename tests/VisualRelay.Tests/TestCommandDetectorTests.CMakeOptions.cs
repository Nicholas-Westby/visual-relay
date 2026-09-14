using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// A CMake project whose tests are behind an option gets that option switched on. Found
/// bootstrapping odygrd/quill on the Mac: the suite only builds with QUILL_BUILD_TESTS=ON, the
/// detected command did not set it, and on a fresh build directory ctest answers "No tests were
/// found!!!" with exit 0, which the setup check took for a passing run.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    [Fact]
    public void DetectCandidates_CMakeWithATestOption_TurnsItOnButNotItsHeavierSiblings()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "CMakeLists.txt"),
            "project(quill)\n"
            + "option(QUILL_BUILD_EXAMPLES \"Build the examples.\" OFF)\n"
            + "option(QUILL_BUILD_TESTS \"Enable this option to build the test suite.\" OFF)\n"
            + "option(QUILL_ENABLE_EXTENSIVE_TESTS \"Tests not suitable for hosted CI runners.\" OFF)\n");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal(
            ["cmake -S . -B build -DQUILL_BUILD_TESTS=ON && cmake --build build && ctest --test-dir build --output-on-failure"],
            candidates);
    }

    [Theory]
    [InlineData("option(BUILD_TESTING \"Build tests\" OFF)", "BUILD_TESTING")]
    [InlineData("option(SPDLOG_BUILD_TESTS \"Build tests\" OFF)", "SPDLOG_BUILD_TESTS")]
    [InlineData("option(JSON_BuildTests \"Build the unit tests\" ${MAIN_PROJECT})", "JSON_BuildTests")]
    [InlineData("option( ABSL_ENABLE_TESTING \"Enable tests\" OFF )", "ABSL_ENABLE_TESTING")]
    public void DetectCandidates_CMakeTestOptionSpellings_AreAllRecognized(string option, string name)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "CMakeLists.txt"), "project(demo)\n" + option + "\n");

        var candidate = Assert.Single(TestCommandDetector.DetectCandidates(repo.Root));

        Assert.StartsWith($"cmake -S . -B build -D{name}=ON && ", candidate, StringComparison.Ordinal);
    }
}
