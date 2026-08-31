using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="HttpClientConstructionGuard"/>. The guard flags
/// construction of <c>HttpClient</c>, <c>HttpMessageInvoker</c>,
/// <c>HttpClientHandler</c> or <c>SocketsHttpHandler</c> anywhere under
/// <c>src/</c>, <c>tools/</c> or <c>tests/</c> outside the allowlisted
/// composition roots, so the <c>IProviderTransport</c> seam stays the only route
/// to the network and the hermetic fast suite has nothing to route around.
/// </summary>
/// <param name="trees">
/// The assembly-wide parsed-tree fixture, registered in
/// <c>TestModuleInitializer.cs</c>. Taken as a primary-constructor parameter
/// rather than the explicit-constructor shape the older guard tests use, so the
/// class adds no <c>ConvertToPrimaryConstructor</c> finding to the InspectCode
/// baseline.
/// </param>
public sealed class HttpClientConstructionGuardTests(CachedSyntaxTreesFixture trees)
{
    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>
    /// Teeth: an explicit <c>new HttpClient(...)</c> in ordinary production
    /// source is flagged.
    /// </summary>
    [Fact]
    public void ExplicitHttpClientConstruction_IsFlagged()
    {
        const string source = """
            class C {
                void M() {
                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("constructs 'HttpClient'", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: a target-typed <c>new()</c> whose declared field type is
    /// <c>HttpClient</c> is flagged — the shape that a name-only text match
    /// would miss entirely.
    /// </summary>
    [Fact]
    public void TargetTypedNew_OnHttpClientField_IsFlagged()
    {
        const string source = """
            class C {
                private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("tools/VisualRelay.Cli/Foo.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("constructs 'HttpClient'", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: <c>HttpMessageInvoker</c> and the two concrete framework handlers
    /// are flagged as well — banning only <c>HttpClient</c> would leave
    /// <c>new HttpMessageInvoker(handler).SendAsync(...)</c> as an open door and
    /// an un-gated handler as the way past the hermetic suite.
    /// </summary>
    [Fact]
    public void InvokerAndHandlerConstruction_AreFlagged()
    {
        const string source = """
            class C {
                void M() {
                    var invoker = new HttpMessageInvoker(new SocketsHttpHandler());
                    var legacy = new HttpClientHandler();
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("tests/VisualRelay.Tests/Foo.cs", source)]);

        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, v => v.Reason.Contains("'HttpMessageInvoker'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Reason.Contains("'SocketsHttpHandler'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Reason.Contains("'HttpClientHandler'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fully qualified <c>new System.Net.Http.HttpClient()</c> is flagged —
    /// the matcher compares the rightmost identifier, not the written text.
    /// </summary>
    [Fact]
    public void QualifiedHttpClientConstruction_IsFlagged()
    {
        const string source = """
            class C {
                void M() {
                    var http = new System.Net.Http.HttpClient();
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("src/VisualRelay.Core/Foo.cs", source)]);

        Assert.Single(violations);
    }

    /// <summary>
    /// The allowlisted production composition root is NOT flagged — the whole
    /// point of the guard is that exactly one file in <c>src/</c> may do this.
    /// </summary>
    [Fact]
    public void AllowlistedCompositionRoot_IsNotFlagged()
    {
        const string source = """
            class C {
                void M() {
                    var http = new HttpClient(handler, disposeHandler: false);
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("src/VisualRelay.Core/Llm/LiveProviderTransport.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A hand-written <c>HttpMessageHandler</c> subclass — an in-memory test
    /// double that opens no socket — is NOT flagged.
    /// </summary>
    [Fact]
    public void CustomHandlerSubclass_IsNotFlagged()
    {
        const string source = """
            class StubHandler : HttpMessageHandler {
                void M() {
                    var stub = new StubHandler();
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("tests/VisualRelay.Tests/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A construction outside <c>src/</c>, <c>tools/</c> and <c>tests/</c> is
    /// NOT flagged — the guard scans only the three source roots.
    /// </summary>
    [Fact]
    public void ConstructionOutsideScannedRoots_IsNotFlagged()
    {
        const string source = """
            class C {
                void M() {
                    var http = new HttpClient();
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("packaging/Scratch.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Happy path: source that reaches the network through
    /// <c>IProviderTransport</c> produces zero violations.
    /// </summary>
    [Fact]
    public void CleanCode_UsingTheTransportSeam_ReportsZero()
    {
        const string source = """
            class C {
                private readonly IProviderTransport _transport;
                async Task M() {
                    await _transport.SendAsync(request);
                }
            }
            """;

        var violations = HttpClientConstructionGuard.FindViolations(
            [("src/VisualRelay.Core/Llm/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    // ── Live-tree test ─────────────────────────────────────────────────────

    /// <summary>
    /// The live enforcing gate: no file under <c>src/</c>, <c>tools/</c> or
    /// <c>tests/</c> may construct an <c>HttpClient</c>, an
    /// <c>HttpMessageInvoker</c> or a real handler outside the allowlisted
    /// composition roots. Any new site flips the guard to a build failure.
    /// </summary>
    [Fact]
    public void LiveTree_HasNoUnallowedHttpConstruction()
    {
        var violations = HttpClientConstructionGuard.FindViolations(trees.AllTrees);

        Assert.True(violations.Count == 0,
            "HttpClientConstructionGuard found HTTP client/handler construction outside the " +
            "allowlisted composition roots (take IProviderTransport, or in tests use " +
            "HermeticHttpHandler):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Reason} — {v.Snippet}")));
    }
}
