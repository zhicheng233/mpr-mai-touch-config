using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;

namespace MaimaiTouchTool.ViewModels;

/// <summary>No SAVE_CFG is ever sent here. A failed/ambiguous APPLY still requires rollback.</summary>
public sealed class GuidedCalibrationTrial(ConfigBlob original, ConfigurationWorkflow workflow)
{
    public ConfigBlob Original { get; } = ConfigBlob.Decode(original.Encode());
    public bool NeedsRestore { get; private set; }

    public async Task<ConfigBlob> ApplyAsync(ConfigBlob candidate, CancellationToken cancellationToken = default)
    {
        NeedsRestore = true;
        return await workflow.ApplyAsync(candidate, cancellationToken);
    }

    public async Task<ConfigBlob> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var restored = await workflow.ApplyAsync(Original, cancellationToken);
        NeedsRestore = false; // Only after readback has verified restoration.
        return restored;
    }
}

public sealed record GuidedVerificationKey(int KeyIndex, bool Passed, string ReasonKey);
public sealed record GuidedVerificationReport(IReadOnlyList<GuidedVerificationKey> Keys)
{
    public bool Passed => Keys.Count > 0 && Keys.All(k => k.Passed);

    public static GuidedVerificationReport From(IReadOnlyList<GuidedCalibrationSession.TargetKey> targets,
        GuidedCalibrationSession session)
    {
        var unexpected = session.Inputs.Aggregate(session.IdleTouchedBits, (bits, input) => bits | input.UnexpectedTouchBits);
        var rows = targets.Select(target =>
        {
            var input = session.Inputs.FirstOrDefault(i => i.KeyIndex == target.KeyIndex);
            var reason = session.Fault is not null ? session.Fault
                : input?.ReleaseStuck == true || input?.Samples.Any(p => !p.HardwareReleased) == true ? "Guided.VerifyStuck"
                : !session.IsComplete || input is null || !input.Measured || input.TimedOut || input.Samples.Count < session.RepeatsPerKey ? "Guided.VerifyIncomplete"
                : input.IdleTouched || (session.IdleTouchedBits & (1UL << target.KeyIndex)) != 0 ? "Guided.StatusIdleHardware"
                : input.IdleUnstable ? "Guided.StatusIdleDrift"
                : input.UnexpectedTouch || (unexpected & (1UL << target.KeyIndex)) != 0 ? "Guided.VerifyCrossTalk"
                : input.Samples.Any(p => !p.HardwareSeen) ? "Guided.VerifyMissed"
                : "Guided.VerifyPassed";
            return new GuidedVerificationKey(target.KeyIndex, reason == "Guided.VerifyPassed", reason);
        }).ToArray();
        return new(rows);
    }
}

public sealed class GuidedBoardSelection(int board) : NotifyModel
{
    private bool selected = true;
    public int Board { get; } = board;
    public bool Selected { get => selected; set { selected = value; OnPropertyChanged(); } }
    public string Label => string.Format(Localization.Localizer.Instance["Config.BoardN"], Board + 1) + $" · 0x{0x5A + Board:X2}";
    public void RaiseLocalized() => OnPropertyChanged(nameof(Label));
}
