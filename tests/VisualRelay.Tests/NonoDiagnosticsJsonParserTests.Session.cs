using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// nono 0.75 (pinned since the upgrade on 2026-09-01) nests its record under "session" and splits
/// it: "violations" holds the operation and target pairs the older top-level "denials" held, and
/// "denials" holds the supervised path refusals. Read as before, the block never parsed, so every
/// run since the upgrade reported no denials and left the JSON in the output. Measured on the Mac:
/// a guard refusal for swiftlang/swift-format quoted nono's JSON instead of the build's error.
/// The blocks below are nono 0.75's real output.
/// </summary>
public sealed partial class NonoDiagnosticsJsonParserTests
{
    private const string SessionWithADenial = """
        {
          "session": {
            "exit_code": 1,
            "denials": [
              {
                "path": "/Users/admin/should-be-denied.txt",
                "access": "Write",
                "reason": "PolicyBlocked"
              }
            ],
            "ipc_denials": [],
            "violations": [
              {
                "operation": "file-write-create",
                "target": "/Users/admin/should-be-denied.txt"
              }
            ],
            "diagnostics": [
              {
                "code": "sandbox_denied_path",
                "severity": "warning",
                "message": "access to /Users/admin/should-be-denied.txt (write) denied: PolicyBlocked"
              }
            ]
          }
        }
        """;

    [Fact]
    public void TryExtractDenials_Nono075Session_ReadsTheViolationAndStripsTheBlock()
    {
        var output = "touch: /Users/admin/should-be-denied.txt: Operation not permitted\n" + SessionWithADenial;

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        var denial = Assert.Single(denials);
        Assert.Equal("file-write-create", denial.Operation);
        Assert.Equal("/Users/admin/should-be-denied.txt", denial.Target);
        Assert.Equal("touch: /Users/admin/should-be-denied.txt: Operation not permitted\n", stripped);
    }

    [Fact]
    public void TryExtractDenials_Nono075SessionWithNoDenials_StillStripsTheBlock()
    {
        var output = "sandbox-exec: sandbox_apply: Operation not permitted\nerror: ExitCode(rawValue: 1)\n"
            + """
              {
                "session": {
                  "exit_code": 1,
                  "denials": [],
                  "ipc_denials": [],
                  "violations": [],
                  "diagnostics": [
                    {
                      "code": "command_failed_likely_sandbox",
                      "severity": "info",
                      "message": "discover additional paths required by the command",
                      "remediation": {
                        "kind": "run_discovery"
                      }
                    }
                  ]
                }
              }
              """;

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.Empty(denials);
        Assert.Equal("sandbox-exec: sandbox_apply: Operation not permitted\nerror: ExitCode(rawValue: 1)\n", stripped);
    }

    /// <summary>A supervised refusal with no matching violation (as Landlock reports) is still a denial.</summary>
    [Fact]
    public void TryExtractDenials_Nono075SupervisedDenialWithoutAViolation_IsKept()
    {
        var output = "restore failed\n" + """
            {"session":{"exit_code":1,"denials":[{"path":"/home/u/.nuget/NuGet/NuGet.Config","access":"Read","reason":"PolicyBlocked"}],"ipc_denials":[],"violations":[],"diagnostics":[]}}
            """;

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out _, out var denials);

        Assert.True(result);
        var denial = Assert.Single(denials);
        Assert.Equal("file-read", denial.Operation);
        Assert.Equal("/home/u/.nuget/NuGet/NuGet.Config", denial.Target);
    }
}
