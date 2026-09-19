namespace VisualRelay.Tests;

/// <summary>
/// Defines the "Isolated" xUnit collection, which runs alone after the parallel collections
/// finish. It holds checks that read process-wide state or time a kill closely. Measured on
/// Windows in a parallel full run: the thread pool was queued so deep that a child gone 0.55 s
/// in was reported back at 6.1 s, and other tests' handles moved the process handle count by 17.
/// <para>
/// It also holds checks whose helper keeps a fixed deadline: a 5 s HTTP timeout, a 30 s token
/// around a spawned CLI, a 4 s drain after a child exits. In the parallel phase the work behind
/// those deadlines is never slow; being NOTICED is, because its completions queue behind blocked
/// pool threads (measured on Windows: a request that took 5,085 ms against its 5,000 ms budget,
/// and 71 work items pending against 10 threads). Widening a deadline only moves that race, so
/// these run where nothing else competes for the pool. Each such class says so where it joins.
/// </para>
/// </summary>
[CollectionDefinition("Isolated", DisableParallelization = true)]
public sealed class IsolatedCollection;
