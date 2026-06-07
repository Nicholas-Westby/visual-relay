using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: VisualRelay.RunTask <root> <task-id>");
    return 2;
}

var rootPath = Path.GetFullPath(args[0]);
var taskId = args[1];
var config = await RelayConfigLoader.LoadAsync(rootPath);
var sink = new ConsoleRelayEventSink();
var dependencies = new RelayDriverDependencies(
    new SwivalSubagentRunner(config, eventSink: sink),
    new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)),
    sink);
var driver = new RelayDriver(dependencies, RelayDriverOptions.Default);
var outcome = await driver.RunTaskAsync(rootPath, taskId);
Console.WriteLine($"{outcome.Status}: {outcome.TaskId} {outcome.CommitSha ?? outcome.Reason}");
return outcome.Status == RelayTaskOutcomeStatus.Committed ? 0 : 2;

internal sealed class ConsoleRelayEventSink : IRelayEventSink
{
    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        var detail = relayEvent.DetailLine == relayEvent.Level ? string.Empty : $" {relayEvent.DetailLine}";
        Console.WriteLine($"{relayEvent.Timestamp:HH:mm:ss} {relayEvent.DisplayLine}{detail}");
        return Task.CompletedTask;
    }
}
