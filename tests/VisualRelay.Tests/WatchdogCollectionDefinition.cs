namespace VisualRelay.Tests;

/// <summary>
/// Defines the "Watchdog" xUnit collection that serializes tests that launch real
/// CPU-burning subprocesses. These tests use timing-sensitive process supervision
/// and must not compete with other parallel collections for CPU or OS resources.
/// Headless and other non-watchdog collections continue to run in parallel with this one.
/// </summary>
[CollectionDefinition("Watchdog")]
public sealed class WatchdogCollection;
