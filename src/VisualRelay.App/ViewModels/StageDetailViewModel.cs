using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.App.Services;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Traces;

namespace VisualRelay.App.ViewModels;

public enum StageDetailState { NoStage, NotStarted, NotComplete, Ready, DriverStage }

public partial class StageDetailViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _systemPromptText = "";

    [ObservableProperty]
    private IReadOnlyList<PromptSection> _inputSections = [];

    [ObservableProperty]
    private IReadOnlyList<OutputField> _outputFields = [];

    [ObservableProperty]
    private string _rawJson = "";

    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    private StageDetailState _systemState = StageDetailState.NoStage;

    [ObservableProperty]
    private StageDetailState _inputState = StageDetailState.NoStage;

    [ObservableProperty]
    private StageDetailState _outputState = StageDetailState.NoStage;

    /// <summary>
    /// Loads stage detail from the latest attempt artifacts in
    /// <paramref name="taskDirectory"/> for <paramref name="stage"/>.
    /// Call with <c>null</c> stage to reset to <see cref="StageDetailState.NoStage"/>.
    /// </summary>
    public void Load(StageRowViewModel? stage, string? taskDirectory)
    {
        if (stage is null || string.IsNullOrEmpty(taskDirectory) || !Directory.Exists(taskDirectory))
        {
            SetAllStates(StageDetailState.NoStage);
            ClearContent();
            return;
        }

        // Check for driver stage (Commit, Kind == "driver")
        var definition = RelayStages.All.FirstOrDefault(s => s.Number == stage.Number);
        if (definition is { Kind: "driver" })
        {
            SetAllStates(StageDetailState.DriverStage);
            ClearContent();
            Header = $"Stage {stage.Ordinal} ({stage.Name})";
            return;
        }

        var inputPath = StageInputArtifact.LatestPath(taskDirectory, stage.Number);

        // ── System prompt ──────────────────────────────────────────
        LoadSystemPrompt(inputPath, stage);

        // ── Input sections ─────────────────────────────────────────
        LoadInput(inputPath);

        // ── Output fields ───────────────────────────────────────────
        LoadOutput(taskDirectory, stage.Number);

        // ── Header ──────────────────────────────────────────────────
        Header = BuildHeader(stage, inputPath);
    }

    private void LoadSystemPrompt(string? inputPath, StageRowViewModel stage)
    {
        if (inputPath is not null &&
            StageInputArtifact.TryRead(inputPath, out var data) &&
            data is not null)
        {
            SystemPromptText = data.SystemPrompt;
        }
        else
        {
            // Fall back to the static definition
            var definition = RelayStages.All.FirstOrDefault(s => s.Number == stage.Number);
            SystemPromptText = definition?.SystemPrompt ?? "";
        }

        SystemState = StageDetailState.Ready;
    }

    private void LoadInput(string? inputPath)
    {
        if (inputPath is not null &&
            StageInputArtifact.TryRead(inputPath, out var data) &&
            data is not null)
        {
            InputSections = AssembledPromptParser.Parse(data.InputPrompt);
            InputState = StageDetailState.Ready;
        }
        else
        {
            InputSections = [];
            InputState = StageDetailState.NotStarted;
        }
    }

    private void LoadOutput(string taskDirectory, int stageNumber)
    {
        var reportPath = LatestReportPath(taskDirectory, stageNumber);
        if (reportPath is null || !File.Exists(reportPath))
        {
            OutputFields = [];
            RawJson = "";
            OutputState = StageDetailState.NotComplete;
            return;
        }

        try
        {
            var reportJson = File.ReadAllText(reportPath);
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;

            // Extract result.answer, fall back to empty string
            var answer = "";
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("answer", out var answerElement) &&
                answerElement.ValueKind == JsonValueKind.String)
            {
                answer = answerElement.GetString() ?? "";
            }

            var parseResult = OutputFieldParser.Parse(answer);
            OutputFields = parseResult.Fields;
            RawJson = parseResult.RawJson;
            OutputState = StageDetailState.Ready;
        }
        catch (JsonException)
        {
            OutputFields = [];
            RawJson = "";
            OutputState = StageDetailState.NotComplete;
        }
    }

    private static string? LatestReportPath(string taskDirectory, int stageNumber)
    {
        if (!Directory.Exists(taskDirectory))
            return null;

        var pattern = $"stage{stageNumber}-attempt*.report.json";
        string? bestPath = null;
        var bestAttempt = 0;

        foreach (var path in Directory.EnumerateFiles(taskDirectory, pattern))
        {
            var name = System.IO.Path.GetFileName(path);
            var attempt = RelayAttempt.AttemptNumber(name);
            if (attempt > bestAttempt)
            {
                bestAttempt = attempt;
                bestPath = path;
            }
        }

        return bestPath;
    }

    private static string BuildHeader(StageRowViewModel stage, string? inputPath)
    {
        var header = $"Stage {stage.Ordinal} ({stage.Name})";

        if (inputPath is not null && File.Exists(inputPath))
        {
            var attempt = RelayAttempt.AttemptNumber(System.IO.Path.GetFileName(inputPath));
            var sizeKb = new FileInfo(inputPath).Length / 1024.0;
            header += $" · attempt {attempt} · {sizeKb:0.#} KB";
        }

        return header;
    }

    private void SetAllStates(StageDetailState state)
    {
        SystemState = state;
        InputState = state;
        OutputState = state;
    }

    private void ClearContent()
    {
        SystemPromptText = "";
        InputSections = [];
        OutputFields = [];
        RawJson = "";
        Header = "";
    }
}
