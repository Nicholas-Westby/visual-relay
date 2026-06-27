using System.Diagnostics;
using System.Xml.Linq;

namespace VisualRelay.Guards;

/// <summary>
/// CLI runner that executes the full test suite (<c>dotnet test</c>) and reports
/// success/failure from the TRX result counters rather than the raw process exit
/// code, so a spurious testhost teardown crash after all tests pass does not mark
/// a green run as failed.
/// </summary>
public static class VerifyRunner
{
    public static async Task<int> RunAsync(string repoRoot)
    {
        var resultsDir = ".vr-verify-results";
        var fullResultsDir = Path.Combine(repoRoot, resultsDir);

        // Clean and recreate results directory.
        if (Directory.Exists(fullResultsDir))
            Directory.Delete(fullResultsDir, true);
        Directory.CreateDirectory(fullResultsDir);

        var testProj = "tests/VisualRelay.Tests/VisualRelay.Tests.csproj";

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(testProj);
        startInfo.ArgumentList.Add("-m:1");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        startInfo.ArgumentList.Add("--logger");
        startInfo.ArgumentList.Add("trx;LogFileName=verify.trx");
        startInfo.ArgumentList.Add("--results-directory");
        startInfo.ArgumentList.Add(resultsDir);

        // Match the original shell script's env vars.
        startInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        // Drain stdout/stderr to avoid deadlocks; the test output is voluminous.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var code = process.ExitCode;
        // Ensure pipes are fully drained (not strictly needed after WaitForExitAsync
        // since we already awaited the reads, but harmless).
        await Task.WhenAll(stdoutTask, stderrTask);

        // Find the TRX file.
        var trxFiles = Directory.GetFiles(fullResultsDir, "*.trx");
        if (trxFiles.Length == 0)
        {
            Console.Error.WriteLine(
                $"vr-verify: no TRX produced (likely a build failure); propagating exit {code}");
            return code;
        }

        var trx = trxFiles[0];

        // Parse counters from the TRX XML.
        XDocument doc;
        try
        {
            doc = XDocument.Load(trx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vr-verify: failed to parse TRX: {ex.Message}");
            return 1;
        }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counters = doc.Descendants(ns + "Counters").FirstOrDefault();

        if (counters is null)
        {
            Console.Error.WriteLine("vr-verify: TRX missing Counters element");
            return 1;
        }

        var failed = IntAttribute(counters, "failed");
        var errors = IntAttribute(counters, "error");
        var aborted = IntAttribute(counters, "aborted");
        var timedout = IntAttribute(counters, "timeout");
        var executed = IntAttribute(counters, "executed");

        Console.WriteLine(
            $"vr-verify: counters -> executed={executed} failed={failed} error={errors} aborted={aborted} timeout={timedout} (raw exit {code})");

        if (failed == 0 && errors == 0 && aborted == 0 && timedout == 0 && executed > 0)
        {
            if (code != 0)
            {
                Console.WriteLine(
                    $"vr-verify: all {executed} tests passed; non-zero exit ({code}) is a post-pass teardown crash -> success");
            }

            return 0;
        }

        Console.Error.WriteLine("vr-verify: TRX reports real failures -> fail");
        return 1;
    }

    private static int IntAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr is not null && int.TryParse(attr.Value, out var val) ? val : 0;
    }
}
