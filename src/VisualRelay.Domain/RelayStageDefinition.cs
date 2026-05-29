namespace VisualRelay.Domain;

public sealed record RelayStageDefinition(
    int Number,
    string Name,
    string Tier,
    string Kind,
    string Files,
    string Commands,
    string SystemPrompt,
    string OutputContract);

