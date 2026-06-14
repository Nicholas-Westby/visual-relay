namespace VisualRelay.Tests;

/// <summary>
/// Defines the "Headless" xUnit collection that serializes all Avalonia headless UI tests.
/// Avalonia headless uses ONE process-global app/dispatcher per process; test classes using
/// [AvaloniaFact]/[AvaloniaTheory] must share this collection so they run serially and cannot
/// race on the shared dispatcher. Non-headless collections continue to run in parallel with this one.
/// </summary>
[CollectionDefinition("Headless")]
public sealed class HeadlessCollection { }
