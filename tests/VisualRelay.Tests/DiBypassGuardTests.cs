using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="DiBypassGuard"/> — detects method parameters
/// defaulting to a real collaborator (<c>?? new GitInvoker()</c>,
/// <c>?? TimeProvider.System</c>) on an optional param, plus call sites
/// of that method which omit the argument while the enclosing class holds
/// <c>RelayDriverDependencies</c>. This is the <c>EarlyImplementationDetector</c>
/// bug shape: an injection seam silently bypassed.
/// </summary>
public sealed class DiBypassGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public DiBypassGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// The canonical EarlyImplementationDetector shape: an optional IGitInvoker
    /// parameter defaulting to new GitInvoker() via ??, and a call site in a
    /// class holding RelayDriverDependencies that omits the argument. Reported.
    /// </summary>
    [Fact]
    public void GitInvokerBypass_WithCallSiteOmission_IsReported()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class EarlyDetector {
                internal static async Task<bool> M(
                    string root, IGitInvoker? gi = null) {
                    var g = gi ?? new GitInvoker();
                    return false;
                }
            }

            class Driver {
                RelayDriverDependencies _deps;
                async Task Caller() {
                    await EarlyDetector.M("root");
                }
            }
            """;

        var violations = DiBypassGuard.FindViolations([("Fixtures/Bypass.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A TimeProvider.System bypass on an optional parameter with a call site
    /// omission in a RelayDriverDependencies-holding class is reported.
    /// </summary>
    [Fact]
    public void TimeProviderBypass_WithCallSiteOmission_IsReported()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class Worker {
                internal static void M(TimeProvider? tp = null) {
                    var t = tp ?? TimeProvider.System;
                }
            }

            class Driver {
                RelayDriverDependencies _deps;
                void Caller() {
                    Worker.M();
                }
            }
            """;

        var violations = DiBypassGuard.FindViolations([("Fixtures/TpBypass.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// When the call site PROVIDES the bypassed argument, it is NOT reported
    /// (the seam is not being bypassed).
    /// </summary>
    [Fact]
    public void CallSiteProvidesArg_IsNotReported()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class EarlyDetector {
                internal static async Task<bool> M(
                    string root, IGitInvoker? gi = null) {
                    var g = gi ?? new GitInvoker();
                    return false;
                }
            }

            class Driver {
                RelayDriverDependencies _deps;
                async Task Caller() {
                    await EarlyDetector.M("root", _deps.GitInvoker);
                }
            }
            """;

        var violations = DiBypassGuard.FindViolations([("Fixtures/Provided.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A method with a ?? bypass default but NO call site that omits the arg
    /// is NOT reported.
    /// </summary>
    [Fact]
    public void MethodWithBypass_ButNoCallSite_IsNotReported()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class EarlyDetector {
                internal static async Task<bool> M(
                    string root, IGitInvoker? gi = null) {
                    var g = gi ?? new GitInvoker();
                    return false;
                }
            }
            """;

        var violations = DiBypassGuard.FindViolations([("Fixtures/NoCaller.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A call site omitting the arg in a class WITHOUT RelayDriverDependencies
    /// is NOT reported (the class doesn't hold the injectable seam to bypass).
    /// </summary>
    [Fact]
    public void CallSiteInClass_WithoutRelayDriverDependencies_IsNotReported()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class EarlyDetector {
                internal static async Task<bool> M(
                    string root, IGitInvoker? gi = null) {
                    var g = gi ?? new GitInvoker();
                    return false;
                }
            }

            class PlainClass {
                async Task Caller() {
                    await EarlyDetector.M("root");
                }
            }
            """;

        var violations = DiBypassGuard.FindViolations([("Fixtures/PlainCaller.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Self-exempt file is not scanned.
    /// </summary>
    [Fact]
    public void SelfExemptFile_IsNotScanned()
    {
        const string source = """
            using VisualRelay.Core.Execution;

            class E { internal static void M(IGitInvoker? gi = null) { var g = gi ?? new GitInvoker(); } }
            class D { RelayDriverDependencies _deps; void C() { E.M(); } }
            """;

        var violations = DiBypassGuard.FindViolations(
            [("tools/VisualRelay.Guards/DiBypassGuard.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Live-tree smoke: matcher completes without throwing over the full tree.
    /// </summary>
    [Fact]
    public void AllTrees_CompleteWithoutThrowing()
    {
        var violations = DiBypassGuard.FindViolations(_trees.AllTrees);

        Assert.NotNull(violations);
    }
}
