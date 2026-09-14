using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A first build longer than bootstrap's check is not a broken command either. Measured on the Mac
/// with a fresh clone of the-open-engine/zeroshot: <c>cargo test</c> was still compiling its
/// dependencies when the 60 s check stopped it, was rejected, and bootstrap took the repository's
/// 4-test tooling script instead of its Rust suite. Every sample is real output: cargo from that
/// clone and CMake from a small project on the Mac, and Gradle (Unciv), Maven (commons-lang) and
/// dotnet (LiteDB, whose lines the Windows arm captures with CRLF endings) from the Windows arm.
/// </summary>
public sealed partial class TestCommandValidatorTests
{
    [Theory]
    [InlineData("cargo test",
        "   Compiling proc-macro2 v1.0.106\n   Compiling quote v1.0.46\n   Compiling unicode-ident v1.0.24\n")]
    [InlineData("./gradlew test",
        "> Task :core:processResources NO-SOURCE\n> Task :core:processTestResources NO-SOURCE\n> Task :core:compileKotlin\n")]
    [InlineData("mvn -B -ntp test",
        "[INFO] --- compiler:3.16.0:compile (default-compile) @ commons-lang3 ---\n"
        + "[INFO] Recompiling the module because of changed source code.\n"
        + "[INFO] Compiling 264 source files with javac [debug release 8] to target/classes\n")]
    [InlineData("dotnet test LiteDB.Tests/LiteDB.Tests.csproj",
        "  Determining projects to restore...\r\n"
        + "  Restored /home/enjay/vr-eval/LiteDB/LiteDB.Tests/LiteDB.Tests.csproj (in 939 ms).\r\n"
        + "  Restored /home/enjay/vr-eval/LiteDB/LiteDB/LiteDB.csproj (in 930 ms).\r\n")]
    [InlineData("dotnet test LiteDB.Tests/LiteDB.Tests.csproj",
        "  LiteDB -> /home/enjay/vr-eval/LiteDB/LiteDB/bin/Release/net10.0/LiteDB.dll\r\n")]
    [InlineData("cmake -S . -B build && cmake --build build && ctest --test-dir build --output-on-failure",
        "-- Configuring done (0.3s)\n-- Generating done (0.0s)\n"
        + "[ 20%] Building CXX object CMakeFiles/core.dir/src/a.cpp.o\n[ 40%] Building CXX object CMakeFiles/core.dir/src/b.cpp.o\n")]
    public async Task ValidateAsync_AFirstBuildStillCompilingAtTheLimit_IsAccepted(string command, string printed)
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(
            new TestRunResult(-1, "test command timed out after 60000ms\n\n" + printed, TimedOut: true)));

        var result = await validator.ValidateAsync("/tmp/repo", command);

        Assert.True(result.Accepted, result.RejectionReason);
    }

    /// <summary>A build that went on to refuse the test task was never going to run tests.</summary>
    [Fact]
    public async Task ValidateAsync_ABuildThatRefusedTheTaskAtTheLimit_StaysRejected()
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(new TestRunResult(
            -1, "test command timed out after 60000ms\n\n> Task :buildSrc:compileKotlin\n\n"
                + "FAILURE: Build failed with an exception.\n\n* What went wrong:\n"
                + "Task 'test' not found in root project 'demo'.\n", TimedOut: true)));

        Assert.False((await validator.ValidateAsync("/tmp/repo", "./gradlew test")).Accepted);
    }

    /// <summary>
    /// A restore that has printed only its opening line has built nothing, and may be waiting on a
    /// package source for ever, so it stays rejected.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ARestoreThatNeverFinishedAtTheLimit_StaysRejected()
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(new TestRunResult(
            -1, "test command timed out after 60000ms\n\n  Determining projects to restore...\r\n", TimedOut: true)));

        Assert.False((await validator.ValidateAsync("/tmp/repo", "dotnet test")).Accepted);
    }
}
