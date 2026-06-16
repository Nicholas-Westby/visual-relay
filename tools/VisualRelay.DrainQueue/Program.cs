using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.DrainQueue;

// ── Parse args ──
var (parseResult, parseError) = ArgParser.Parse(args);
if (parseError is not null)
{
    Console.Error.WriteLine(parseError);
    return 2;
}

var rootPath = parseResult!.RootPath;
var requestedIds = parseResult.TaskIds;

// ── Load config ──
var configResult = await RelayConfigLoader.TryLoadAsync(rootPath);
if (!configResult.IsRunnable)
{
    Console.Error.WriteLine(configResult.Diagnostic
        ?? $"No usable .relay/config.json in {rootPath}");
    return 2;
}

var config = configResult.Config;

// ── Build two-phase controller ──
var planTestRunner = new SandboxedTestRunner(
    new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config);

ISubagentRunner PlanSubagentFactory(string taskId) =>
    new SwivalSubagentRunner(config, eventSink: new ConsoleRelayEventSink(taskId));

// PlanPhaseRunner internally creates FileRelayEventSink for each planning task.
// The ConsoleRelayEventSink gives attributable interleaved console output.
IRelayEventSink PlanSinkFactory(string taskId) =>
    new ConsoleRelayEventSink(taskId);

var phase2Runner = new ConsoleTaskRunner(rootPath, config,
    new SandboxedTestRunner(new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config));

var controller = new RelayQueueController(
    rootPath,
    phase2Runner,
    planSubagentRunnerFactory: PlanSubagentFactory,
    planTestRunner: planTestRunner,
    planEventSinkFactory: PlanSinkFactory);

// ── Refresh and optionally enforce subset/order ──
await controller.RefreshAsync();

if (requestedIds is { Count: > 0 })
{
    // Validate all requested IDs exist in the pending set.
    var validationError = ArgParser.ValidateTaskIds(requestedIds, controller.Tasks);
    if (validationError is not null)
    {
        Console.Error.WriteLine(validationError);
        return 2;
    }

    // Build a lookup of the existing tasks for quick retrieval.
    var taskLookup = controller.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

    // Rewrite the Tasks collection to the requested subset/order.
    controller.Tasks.Clear();
    foreach (var id in requestedIds)
    {
        controller.Tasks.Add(taskLookup[id]);
    }
}

// ── Check for empty queue ──
if (controller.Tasks.Count == 0)
{
    Console.WriteLine(DrainOutcome.NothingPendingMessage);
    return 0;
}

// ── Drain ──
var results = await controller.DrainAsync();

// ── Print per-task outcomes ──
foreach (var outcome in results)
{
    Console.WriteLine(DrainOutcome.FormatOutcomeLine(outcome));
}

// ── Print summary ──
var summary = DrainOutcome.ComputeSummary(results);
Console.WriteLine(DrainOutcome.FormatSummary(summary));

// ── Exit code ──
return DrainOutcome.GetExitCode(results);
