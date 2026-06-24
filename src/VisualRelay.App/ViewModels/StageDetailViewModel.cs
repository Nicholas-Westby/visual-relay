using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.App.Services;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Traces;

namespace VisualRelay.App.ViewModels;

public enum StageDetailState { NoStage, NotStarted, NotComplete, Ready, DriverStage, Skipped, NotAvailable }

public partial class StageDetailViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _systemPromptText = "";

    [ObservableProperty]
    private IReadOnlyList<PromptSection> _inputSections = [];

    [ObservableProperty]
    private string _inputPromptRawText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputReadyAndNotRawText))]
    [NotifyPropertyChangedFor(nameof(IsInputReadyAndRawText))]
    private bool _isInputRawText;

    [ObservableProperty]
    private IReadOnlyList<OutputField> _outputFields = [];

    [ObservableProperty]
    private string _rawJson = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputReadyAndNotRawJson))]
    [NotifyPropertyChangedFor(nameof(IsOutputReadyAndRawJson))]
    private bool _isOutputRawJson;

    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemNoStage))]
    [NotifyPropertyChangedFor(nameof(IsSystemNotStarted))]
    [NotifyPropertyChangedFor(nameof(IsSystemNotComplete))]
    [NotifyPropertyChangedFor(nameof(IsSystemReady))]
    [NotifyPropertyChangedFor(nameof(IsSystemDriverStage))]
    private StageDetailState _systemState = StageDetailState.NoStage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputNoStage))]
    [NotifyPropertyChangedFor(nameof(IsInputNotStarted))]
    [NotifyPropertyChangedFor(nameof(IsInputNotComplete))]
    [NotifyPropertyChangedFor(nameof(IsInputReady))]
    [NotifyPropertyChangedFor(nameof(IsInputDriverStage))]
    [NotifyPropertyChangedFor(nameof(IsInputNotAvailable))]
    [NotifyPropertyChangedFor(nameof(IsInputReadyAndNotRawText))]
    [NotifyPropertyChangedFor(nameof(IsInputReadyAndRawText))]
    private StageDetailState _inputState = StageDetailState.NoStage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputNoStage))]
    [NotifyPropertyChangedFor(nameof(IsOutputNotStarted))]
    [NotifyPropertyChangedFor(nameof(IsOutputNotComplete))]
    [NotifyPropertyChangedFor(nameof(IsOutputReady))]
    [NotifyPropertyChangedFor(nameof(IsOutputDriverStage))]
    [NotifyPropertyChangedFor(nameof(IsOutputSkipped))]
    [NotifyPropertyChangedFor(nameof(IsOutputNotAvailable))]
    [NotifyPropertyChangedFor(nameof(IsOutputReadyAndNotRawJson))]
    [NotifyPropertyChangedFor(nameof(IsOutputReadyAndRawJson))]
    private StageDetailState _outputState = StageDetailState.NoStage;

    // ── Boolean state properties for XAML binding (no converter needed) ──

    public bool IsSystemNoStage => SystemState == StageDetailState.NoStage;
    public bool IsSystemNotStarted => SystemState == StageDetailState.NotStarted;
    public bool IsSystemNotComplete => SystemState == StageDetailState.NotComplete;
    public bool IsSystemReady => SystemState == StageDetailState.Ready;
    public bool IsSystemDriverStage => SystemState == StageDetailState.DriverStage;

    public bool IsInputNoStage => InputState == StageDetailState.NoStage;
    public bool IsInputNotStarted => InputState == StageDetailState.NotStarted;
    public bool IsInputNotComplete => InputState == StageDetailState.NotComplete;
    public bool IsInputReady => InputState == StageDetailState.Ready;
    public bool IsInputDriverStage => InputState == StageDetailState.DriverStage;
    public bool IsInputNotAvailable => InputState == StageDetailState.NotAvailable;
    public bool IsOutputNoStage => OutputState == StageDetailState.NoStage;
    public bool IsOutputNotStarted => OutputState == StageDetailState.NotStarted;
    public bool IsOutputNotComplete => OutputState == StageDetailState.NotComplete;
    public bool IsOutputReady => OutputState == StageDetailState.Ready;
    public bool IsOutputDriverStage => OutputState == StageDetailState.DriverStage;
    public bool IsOutputSkipped => OutputState == StageDetailState.Skipped;
    public bool IsOutputNotAvailable => OutputState == StageDetailState.NotAvailable;

    // ── Raw toggle visibility helpers ──

    public bool IsInputReadyAndNotRawText => InputState == StageDetailState.Ready && !IsInputRawText;
    public bool IsInputReadyAndRawText => InputState == StageDetailState.Ready && IsInputRawText;
    public bool IsOutputReadyAndNotRawJson => OutputState == StageDetailState.Ready && !IsOutputRawJson;
    public bool IsOutputReadyAndRawJson => OutputState == StageDetailState.Ready && IsOutputRawJson;

    // ── Tooltip strings for the Raw / Raw JSON checkboxes ──

    public static string InputRawToggleTooltip { get; } =
        "Show the raw LLM prompt text instead of parsed sections";

    public static string OutputRawToggleTooltip { get; } =
        "Show the raw JSON output instead of rendered fields";

    /// <summary>
    /// Loads stage detail from the latest attempt artifacts in
    /// <paramref name="taskDirectory"/> for <paramref name="stage"/>.
    /// Call with <c>null</c> stage to reset to <see cref="StageDetailState.NoStage"/>.
    /// </summary>
    public void Load(StageRowViewModel? stage, string? taskDirectory)
    {
        if (stage is null)
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

        if (string.IsNullOrEmpty(taskDirectory) || !Directory.Exists(taskDirectory))
        {
            LoadSystemPrompt(null, stage);

            var done = "Done".Equals(stage.Status, StringComparison.OrdinalIgnoreCase);
            InputSections = [];
            InputPromptRawText = "";
            IsInputRawText = false;
            InputState = done ? StageDetailState.NotAvailable : StageDetailState.NotStarted;
            OutputFields = [];
            RawJson = "";
            OutputState = done ? StageDetailState.NotAvailable : StageDetailState.NotComplete;

            Header = BuildHeader(stage, null);
            return;
        }

        var inputPath = StageInputArtifact.LatestPath(taskDirectory, stage.Number);

        // ── System prompt ──────────────────────────────────────────
        LoadSystemPrompt(inputPath, stage);

        // ── Input sections ─────────────────────────────────────────
        LoadInput(inputPath, stage.Status);

        // ── Output fields ───────────────────────────────────────────
        LoadOutput(taskDirectory, stage.Number, stage.Status);

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

    private void LoadInput(string? inputPath, string? status)
    {
        if (inputPath is not null &&
            StageInputArtifact.TryRead(inputPath, out var data) &&
            data is not null)
        {
            InputSections = AssembledPromptParser.Parse(data.InputPrompt);
            InputPromptRawText = data.InputPrompt;
            IsInputRawText = false;
            InputState = StageDetailState.Ready;
        }
        else
        {
            InputSections = [];
            InputPromptRawText = "";
            IsInputRawText = false;
            var done = "Done".Equals(status, StringComparison.OrdinalIgnoreCase);
            InputState = done ? StageDetailState.NotAvailable : StageDetailState.NotStarted;
        }
    }

    private void LoadOutput(string taskDirectory, int stageNumber, string? status)
    {
        var reportPath = LatestReportPath(taskDirectory, stageNumber);
        if (reportPath is null || !File.Exists(reportPath))
        {
            OutputFields = [];
            RawJson = "";
            OutputState = "Done".Equals(status, StringComparison.OrdinalIgnoreCase)
                ? StageDetailState.Skipped
                : StageDetailState.NotComplete;
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
            var name = Path.GetFileName(path);
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
            var attempt = RelayAttempt.AttemptNumber(Path.GetFileName(inputPath));
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
        InputPromptRawText = "";
        IsInputRawText = false;
        OutputFields = [];
        RawJson = "";
        IsOutputRawJson = false;
        Header = "";
    }
}
