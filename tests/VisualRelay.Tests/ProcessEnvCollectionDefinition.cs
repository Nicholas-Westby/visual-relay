namespace VisualRelay.Tests;

/// <summary>
/// Defines the "ProcessEnv" xUnit collection that serializes tests which mutate a
/// process-global environment variable (notably <c>HOME</c>) against tests that
/// read it. Membership prevents a transient <c>HOME</c> override from one test
/// racing another's <c>Environment.GetFolderPath(UserProfile)</c> read — which
/// returns the live <c>HOME</c>, so a concurrent override makes an under-home path
/// check fail non-deterministically. Other collections run in parallel with this one.
/// </summary>
[CollectionDefinition("ProcessEnv")]
public sealed class ProcessEnvCollection;
