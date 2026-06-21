namespace VisualRelay.Cli.Commands;

/// <summary><c>test</c>: delegates to the C# <see cref="TestRunner"/> (the
/// <c>test.sh</c> port) — timestamped logs, NO_BUILD/filter args, console+trx
/// loggers, watchdog, and TRX failed-test extraction.</summary>
public static class TestCommand
{
    public static Task<int> RunAsync(RepoPaths paths, IReadOnlyList<string> args) =>
        TestRunner.RunAsync(paths, args);
}
