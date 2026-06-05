namespace VisualRelay.Domain;

// Classifies how a project's .relay/config.json resolved. Loaded = valid;
// Defaulted = no file (Config is Defaults()); Incomplete = file present but
// required testCmd missing/blank; Malformed = invalid JSON or wrong field type.
public enum RelayConfigStatus
{
    Loaded,
    Defaulted,
    Incomplete,
    Malformed
}

// Result of a non-throwing config load. Diagnostic carries the full, untruncated
// message for the Malformed case (null otherwise).
public sealed record RelayConfigResult(
    RelayConfig Config,
    RelayConfigStatus Status,
    string? Diagnostic)
{
    public bool IsRunnable => Status == RelayConfigStatus.Loaded;

    public bool NeedsInitialization =>
        Status is RelayConfigStatus.Defaulted or RelayConfigStatus.Incomplete;
}
