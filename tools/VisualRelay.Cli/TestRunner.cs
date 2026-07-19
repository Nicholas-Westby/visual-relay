namespace VisualRelay.Cli;

/// <summary>
/// Runs the test suite and persists all output so failures are diagnosable —
/// the C# port of <c>test.sh</c>. Computes a host/VM-safe log stem
/// (<see cref="TestLogPaths"/>), honors <c>NO_BUILD</c> and a filter token, runs
/// <c>dotnet test</c> with console+trx loggers under the timeout watchdog, and on
/// failure prints the failed test names extracted from the TRX
/// (<see cref="TrxFailureParser"/>). Returns the test exit code (124 on timeout).
/// </summary>
public static class TestRunner
{
    public static async Task<int> RunAsync(RepoPaths paths, IReadOnlyList<string> args)
    {
        var logDir = Environment.GetEnvironmentVariable("VR_TEST_LOG_DIR");
        if (string.IsNullOrEmpty(logDir))
            logDir = Path.Combine(paths.Root, "test-logs");
        Directory.CreateDirectory(logDir);

        var log = TestLogPaths.Create(
            logDir,
            DateTime.Now,
            HostName(),
            Environment.ProcessId);

        var noBuild = string.Equals(Environment.GetEnvironmentVariable("NO_BUILD"), "1", StringComparison.Ordinal);

        string[] testArgs;
        bool isSerial;
        try
        {
            testArgs = BuildTestArgs(paths.TestsProject, args.ToArray(), log.Stem, logDir, noBuild, out isSerial);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine(
                "visual-relay: serial mode cannot be combined with a caller-supplied -- RunSettings tail");
            return 2;
        }

        var timeout = WatchdogTimeouts.ForTest(isSerial);
        var rc = await TimeoutWatchdog.RunAsync(ProcessLauncher.Dotnet, testArgs, paths.Root, timeout);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Log dir  : {logDir}");
        Console.Error.WriteLine($"TRX file : {log.TrxFile}");
        if (isSerial)
            Console.Error.WriteLine("serial mode: one collection at a time; per-test timings are trustworthy");

        if (rc == 124)
        {
            Console.Error.WriteLine($"visual-relay: test timed out after {timeout.TotalSeconds:F0}s");
            Console.Error.WriteLine("  See TROUBLESHOOTING.md for diagnosing hangs.");
            Console.Error.WriteLine(
                "  To find which test is stuck: ./visual-relay test --blame-hang --blame-hang-timeout 120s");
            Console.Error.WriteLine(
                "  Override timeout: VISUAL_RELAY_TEST_TIMEOUT=<seconds> ./visual-relay test");
        }
        else if (rc != 0)
        {
            PrintFailures(log.TrxFile);
        }

        return rc;
    }

    /// <summary>
    /// Pure builder: produces the <c>dotnet test</c> argument list. When the
    /// first token is <c>"serial"</c> (ordinal), it is consumed and
    /// <paramref name="isSerial"/> is set — the returned args end with
    /// <c>-- xUnit.ParallelizeTestCollections=false</c>. The remaining tokens
    /// follow the existing filter/forward logic unchanged.
    /// </summary>
    internal static string[] BuildTestArgs(
        string testsProject,
        ReadOnlySpan<string> args,
        string trxStem,
        string resultsDirectory,
        bool noBuild,
        out bool isSerial)
    {
        var testArgs = new List<string>
        {
            "test", testsProject,
            "-m:1", "-p:UseSharedCompilation=false",
            "--logger", "console;verbosity=normal",
            "--logger", $"trx;LogFileName={trxStem}.trx",
            "--results-directory", resultsDirectory,
        };

        if (noBuild)
            testArgs.Add("--no-build");

        isSerial = false;
        if (args.Length > 0 && args[0].Equals("serial", StringComparison.Ordinal))
        {
            isSerial = true;
            args = args.Slice(1);
        }

        // A leading non-flag token becomes the FullyQualifiedName filter (the
        // test.sh positional). Remaining args are forwarded verbatim.
        var forwarded = new List<string>(args.Length);
        foreach (var a in args)
            forwarded.Add(a);
        if (forwarded.Count > 0 && !forwarded[0].StartsWith('-'))
        {
            testArgs.Add("--filter");
            testArgs.Add($"FullyQualifiedName~{forwarded[0]}");
            forwarded.RemoveAt(0);
        }

        if (isSerial)
        {
            if (forwarded.Contains("--"))
                throw new InvalidOperationException(
                    "Cannot combine serial mode with a caller-supplied -- RunSettings tail.");
            testArgs.Add("--");
            testArgs.Add("xUnit.ParallelizeTestCollections=false");
        }

        testArgs.AddRange(forwarded);
        return testArgs.ToArray();
    }

    private static void PrintFailures(string trxFile)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("FAILING TESTS:");
        if (!File.Exists(trxFile))
        {
            Console.Error.WriteLine($"  (TRX not found at {trxFile} — check log for details)");
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(trxFile);
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"  (could not read TRX at {trxFile})");
            return;
        }

        foreach (var name in TrxFailureParser.ExtractFailedTestNames(content))
            Console.Error.WriteLine($"  - {name}");
    }

    private static string HostName()
    {
        try
        {
            var host = Environment.MachineName;
            // Match `hostname -s`: short name only.
            var dot = host.IndexOf('.');
            return dot > 0 ? host[..dot] : host;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
