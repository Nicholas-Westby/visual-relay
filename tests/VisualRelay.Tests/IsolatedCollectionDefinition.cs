namespace VisualRelay.Tests;

/// <summary>
/// Defines the "Isolated" xUnit collection, which runs alone after the parallel collections
/// finish. It holds checks that read process-wide state or time a kill closely. Measured on
/// Windows in a parallel full run: the thread pool was queued so deep that a child gone 0.55 s
/// in was reported back at 6.1 s, and other tests' handles moved the process handle count by 17.
/// </summary>
[CollectionDefinition("Isolated", DisableParallelization = true)]
public sealed class IsolatedCollection;
