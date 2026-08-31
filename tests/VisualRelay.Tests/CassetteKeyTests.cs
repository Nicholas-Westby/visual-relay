using static VisualRelay.Tests.CassetteTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// The canonicalizer's contract: requests that mean the same thing key the same,
/// requests that differ key differently, and a canonicalizer version bump
/// invalidates every key at once.
/// </summary>
public sealed class CassetteKeyTests
{
    /// <summary>Header order, header casing and JSON key order are all normalized away.</summary>
    [Fact]
    public void Compute_SameExchange_KeysIdentically_WhateverTheOrdering()
    {
        var first = Post(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":false}""",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/json",
                ["Authorization"] = "Bearer sk-live-first",
            });

        var second = Post(
            """{"stream":false,"messages":[{"content":"hi","role":"user"}],"model":"test-model"}""",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = "application/json",
                ["authorization"] = "Bearer sk-live-second",
                ["accept"] = "application/json",
            });

        Assert.Equal(CassetteKey.Compute(first), CassetteKey.Compute(second));
    }

    /// <summary>A changed body field is a different exchange and gets a different key.</summary>
    [Fact]
    public void Compute_ChangedBodyField_ChangesTheKey()
    {
        var recorded = Post(ChatBody("hi"));
        var changed = Post(ChatBody("hello"));

        Assert.NotEqual(CassetteKey.Compute(recorded), CassetteKey.Compute(changed));
    }

    /// <summary>Array order is semantic: swapping two messages is a different request.</summary>
    [Fact]
    public void Compute_ReorderedMessages_ChangeTheKey()
    {
        var forwards = Post(
            """{"model":"m","messages":[{"role":"user","content":"a"},{"role":"user","content":"b"}]}""");
        var backwards = Post(
            """{"model":"m","messages":[{"role":"user","content":"b"},{"role":"user","content":"a"}]}""");

        Assert.NotEqual(CassetteKey.Compute(forwards), CassetteKey.Compute(backwards));
    }

    /// <summary>
    /// Only allowlisted headers reach the key: a new client header, a rotating
    /// request id and a credential all leave it untouched, so none of them can
    /// silently invalidate a whole cassette tree.
    /// </summary>
    [Fact]
    public void Compute_IgnoresHeadersOutsideTheAllowlist()
    {
        var bare = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = "application/json",
        });

        var noisy = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = "application/json",
            ["x-client-version"] = "2026.8.31",
            ["x-request-id"] = Guid.NewGuid().ToString("N"),
            ["x-api-key"] = "sk-live-secret",
        });

        Assert.Equal(CassetteKey.Compute(bare), CassetteKey.Compute(noisy));
    }

    /// <summary>An allowlisted header IS part of the key: changing accept changes it.</summary>
    [Fact]
    public void Compute_ChangedAllowlistedHeader_ChangesTheKey()
    {
        var json = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = "application/json",
        });

        var sse = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = "text/event-stream",
        });

        Assert.NotEqual(CassetteKey.Compute(json), CassetteKey.Compute(sse));
    }

    /// <summary>
    /// The canonicalizer version is hashed in, so bumping it invalidates every key
    /// at once — a canonicalization change becomes a deliberate re-record instead
    /// of a silent, unexplained mass miss.
    /// </summary>
    [Fact]
    public void Compute_VersionBump_InvalidatesTheKey()
    {
        var request = Post(ChatBody("hi"));

        Assert.NotEqual(CassetteKey.Compute(request), CassetteKey.Compute(request, CassetteKey.Version + 1));
    }

    /// <summary>The canonical form sorts object keys recursively and keeps array order.</summary>
    [Fact]
    public void Canonical_SortsObjectKeysRecursively_AndKeepsArrayOrder()
    {
        var canonical = CassetteKey.Canonical(Post(
            """{"model":"m","messages":[{"role":"user","content":"a"},{"role":"user","content":"b"}]}"""));

        Assert.Equal(
            """{"messages":[{"content":"a","role":"user"},{"content":"b","role":"user"}],"model":"m"}""",
            canonical["body"]!.ToJsonString());
        Assert.Equal("m", canonical["model"]!.GetValue<string>());
        Assert.Equal("/v1/chat/completions", canonical["path"]!.GetValue<string>());
    }

    /// <summary>A body that is not JSON is keyed verbatim rather than rejected.</summary>
    [Fact]
    public void Canonical_NonJsonBody_IsKeptVerbatim()
    {
        var canonical = CassetteKey.Canonical(Post("prompt=hello&n=1"));

        Assert.Equal("prompt=hello&n=1", canonical["body"]!.GetValue<string>());
        Assert.Null(canonical["model"]);
        Assert.NotEqual(CassetteKey.Compute(Post("prompt=hello&n=1")), CassetteKey.Compute(Post("prompt=bye&n=1")));
    }

    /// <summary>
    /// Query parameters are sorted and credential-bearing ones redacted, so one
    /// endpoint keys stably however the query was written and whichever key signed it.
    /// </summary>
    [Fact]
    public void Compute_QueryString_IsSortedAndCredentialsRedacted()
    {
        const string endpoint = "https://api.example.test/v1/models";
        var first = Post(ChatBody("hi"), uri: $"{endpoint}?api_key=sk-live-first&beta=on");
        var second = Post(ChatBody("hi"), uri: $"{endpoint}?beta=on&api_key=sk-live-second");
        var different = Post(ChatBody("hi"), uri: $"{endpoint}?beta=off&api_key=sk-live-first");

        Assert.Equal(CassetteKey.Compute(first), CassetteKey.Compute(second));
        Assert.NotEqual(CassetteKey.Compute(first), CassetteKey.Compute(different));
        Assert.DoesNotContain("sk-live", CassetteKey.Canonical(first).ToJsonString(), StringComparison.Ordinal);
    }
}
