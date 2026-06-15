namespace VisualRelay.Domain;

public sealed record TestRunResult(int ExitCode, string Output, bool TimedOut = false, TimeSpan Elapsed = default);

