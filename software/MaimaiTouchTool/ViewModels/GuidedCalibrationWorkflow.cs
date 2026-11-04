using System.Collections.ObjectModel;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public sealed partial class MainViewModel
{
    private Task? guidedStreamTask;
    private ConfigBlob? guidedSource;
    private GuidedCalibrationTrial? guidedTrial;
    private GuidedVerificationReport? guidedVerification;
    private List<GuidedCalibrationSession.TargetKey> guidedScope = [];
    private readonly Dictionary<int, GuidedKeyInput> guidedMeasurements = [];
    private bool guidedVerifying;
    private string? guidedTrialPort;

    public ObservableCollection<GuidedBoardSelection> GuidedBoardChoices { get; } = new(Enumerable.Range(0, 4).Select(i => new GuidedBoardSelection(i)));
    public bool IsGuidedLocked => IsGuidedRunning || guidedTrial is not null;
    public bool CanEditGuidedSelection => !IsBusy && !IsGuidedLocked;
    private bool CanStartGuided => IsConnected && !IsBusy && !IsGuidedRunning && !IsStreaming && !IsGameMode &&
        !IsMappingRunning && !IsCalibrating && !IsDirty && !HasConflict && !Editor.HasMappingConflict;
    public string GuidedVerificationLabel => guidedVerification is not null
        ? Localizer.Instance[guidedVerification.Passed ? "Guided.VerifyPassed" : "Guided.VerifyFailed"]
        : Localizer.Instance[guidedTrial is not null ? "Guided.TrialActive" : "Guided.NotVerified"];

    private void EnsureGuidedNotLocked()
    {
        if (IsGuidedLocked) throw new InvalidOperationException(Localizer.Instance["Guided.Locked"]);
    }

    private void StartGuidedCapture(List<GuidedCalibrationSession.TargetKey> targets, bool verification)
    {
        guidedVerifying = verification;
        guidedVerification = null;
        guidedResult = verification ? guidedResult : null;
        if (!verification) { GuidedBoards.Clear(); GuidedKeys.Clear(); }
        GuidedProgress = GuidedKeyProgress = GuidedLiveDepth = 0;
        SignalChart.Clear();
        SignalChart.Follow(targets.FirstOrDefault()?.KeyIndex);
        guidedSession = new GuidedCalibrationSession(targets, requiredRepeats: verification ? 8 : 5,
            allowRawRecovery: !verification);
        guidedCancellation = new CancellationTokenSource();
        guidedLastFrameMs = guidedPreviousRawMs = 0;
        guidedFirstFrame = true;
        GuidedStepLabel = Localizer.Instance["Guided.Warmup"];
        StreamForGuided();
        StatusMessage = Localizer.Instance[verification ? "Guided.Verifying" : "Guided.Running"];
        RefreshCommands();
    }

    private async Task VerifyGuidedAsync()
    {
        if (!CanStartGuided || guidedSource is null || guidedResult is null || workflow is null) return;
        IsBusy = true;
        try
        {
            if (guidedTrial is not null && !await RestoreGuidedTrialAsync()) return;
            if (!applied.Encode().AsSpan().SequenceEqual(guidedSource.Encode()))
                throw new InvalidOperationException(Localizer.Instance["Guided.ConfigChanged"]);
            var keys = GuidedKeys.Where(k => k.Adopt && k.Outcome.Status == GuidedKeyStatus.Measured).Select(k => k.Outcome.KeyIndex).ToHashSet();
            var boards = GuidedBoards.Where(b => b.Adopt).Select(b => b.Index).ToHashSet();
            if (keys.Count == 0) throw new InvalidOperationException(Localizer.Instance["Guided.NoTargets"]);
            var candidate = GuidedCalibrationAlgorithm.Apply(guidedResult, guidedSource,
                key => keys.Contains(key.KeyIndex), board => boards.Contains(board.BoardIndex));
            foreach (var target in guidedScope)
            {
                var board = candidate.Boards[target.Board];
                var threshold = board.BaseThreshold + board.SensOffset + candidate.ElectrodeOffsets[target.KeyIndex];
                if (threshold < GuidedCalibrationAlgorithm.MinimumSafeThreshold ||
                    GuidedCalibrationAlgorithm.HardwareReleaseThreshold(threshold) == 0)
                    throw new InvalidOperationException(target.Label + ": " + Localizer.Instance["Guided.UnsafeRelease"]);
            }
            guidedTrial = new GuidedCalibrationTrial(guidedSource, workflow);
            guidedTrialPort = SelectedPort;
            applied = await guidedTrial.ApplyAsync(candidate);
            UpdateGuidedEditor();
            StartGuidedCapture(guidedScope, verification: true);
        }
        catch (Exception ex)
        {
            if (guidedTrial is null || await RestoreGuidedTrialAsync())
                ReportCommandFailure(Localizer.Instance["Guided.Failed"], new InvalidOperationException(UserMessage(ex)));
        }
        finally { IsBusy = false; }
    }

    private HashSet<int> FailedGuidedKeys() => guidedResult is null ? [] : guidedResult.Keys
        .Where(k => k.Status != GuidedKeyStatus.Measured || guidedVerification?.Keys.Any(v => v.KeyIndex == k.KeyIndex && !v.Passed) == true)
        .Select(k => k.KeyIndex).ToHashSet();

    private async Task RetryGuidedAsync()
    {
        if (!CanStartGuided) return;
        var failed = FailedGuidedKeys();
        if (failed.Count == 0) return;
        IsBusy = true;
        try
        {
            if (guidedTrial is not null && !await RestoreGuidedTrialAsync()) return;
            if (guidedSource is null || !applied.Encode().AsSpan().SequenceEqual(guidedSource.Encode()))
            {
                StatusMessage = Localizer.Instance["Guided.ConfigChanged"];
                return;
            }
            StartGuidedCapture(guidedScope.Where(k => failed.Contains(k.KeyIndex)).ToList(), verification: false);
        }
        finally { IsBusy = false; }
    }

    private void UpdateGuidedEditor()
    {
        Editor.Update(applied);
        WireEditor();
        IsDirty = false;
        OnPropertyChanged(nameof(DirtyLabel));
        Telemetry.ApplyThresholds(applied);
    }

    private async Task<bool> RestoreGuidedTrialAsync()
    {
        if (guidedTrial is null) return true;
        guidedVerification = null; // An ambiguous rollback invalidates even a previously passed trial.
        try
        {
            // Do not clear the rollback handle until readback confirms it. Retry remains available.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            applied = await guidedTrial.RestoreAsync(timeout.Token);
            guidedTrial = null;
            guidedTrialPort = null;
            guidedVerification = null;
            UpdateGuidedEditor();
            RefreshCommands();
            return true;
        }
        catch (Exception ex)
        {
            ReportCommandFailure(Localizer.Instance["Guided.RestoreFailed"], ex);
            RefreshCommands();
            return false;
        }
    }

    private async Task DiscardGuidedResultAsync()
    {
        IsBusy = true;
        try
        {
            if (!await RestoreGuidedTrialAsync()) return;
            ClearGuidedResult();
            StatusMessage = Localizer.Instance["Guided.Discarded"];
        }
        finally { IsBusy = false; }
    }

    private async Task FailGuidedCaptureAsync(string error)
    {
        var wasBusy = IsBusy;
        IsBusy = true;
        try
        {
            guidedSession = null;
            await EndGuidedStreamAsync();
            if (guidedTrial is not null && !await RestoreGuidedTrialAsync()) return;
            guidedVerifying = false;
            guidedVerification = null;
            GuidedStepLabel = error;
            ReportCommandFailure(Localizer.Instance["Guided.Failed"], new InvalidOperationException(error));
        }
        finally { IsBusy = wasBusy; RefreshCommands(); }
    }
}
