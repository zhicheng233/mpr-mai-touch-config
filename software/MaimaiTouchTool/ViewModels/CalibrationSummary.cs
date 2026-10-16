using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

/// <summary>单块板的校准结果行（板级阈值增量 + 受影响的映射通道数）。</summary>
public sealed class CalibrationBoardResult
{
    public CalibrationBoardResult(int index, int previousThreshold, int currentThreshold, int mappedChannels, int changedChannels)
    {
        Index = index;
        PreviousThreshold = previousThreshold;
        CurrentThreshold = currentThreshold;
        MappedChannels = mappedChannels;
        ChangedChannels = changedChannels;
    }

    public int Index { get; }
    public int PreviousThreshold { get; }
    public int CurrentThreshold { get; }
    public int MappedChannels { get; }
    public int ChangedChannels { get; }
    public bool Changed => CurrentThreshold != PreviousThreshold;
    public string Label => string.Format(Localizer.Instance["Config.BoardN"], Index);
    public string Detail => string.Format(Localizer.Instance["Cal.ResultBoardDetail"], PreviousThreshold, CurrentThreshold, ChangedChannels, MappedChannels);
    public string StateLabel => Changed ? Localizer.Instance["Cal.ResultRaised"] : Localizer.Instance["Cal.ResultUnchanged"];
}

/// <summary>一次校准的可展示结果：板级明细 + 一行概要。纯逻辑，便于单测。</summary>
public sealed class CalibrationSummary
{
    private CalibrationSummary(bool succeeded, bool readbackFailed, CalDonePayload done, IReadOnlyList<CalibrationBoardResult> boards, int calibratedChannels, int minBaseline, int maxBaseline)
    {
        Succeeded = succeeded;
        ReadbackFailed = readbackFailed;
        Done = done;
        Boards = boards;
        CalibratedChannels = calibratedChannels;
        MinBaseline = minBaseline;
        MaxBaseline = maxBaseline;
    }

    public bool Succeeded { get; }
    public bool ReadbackFailed { get; }
    public CalDonePayload Done { get; }
    public IReadOnlyList<CalibrationBoardResult> Boards { get; }
    public int CalibratedChannels { get; }
    public int MinBaseline { get; }
    public int MaxBaseline { get; }
    public int ChangedBoardCount => Boards.Count(board => board.Changed);
    public string FailureReasonName => Enum.GetName(Done.Reason) ?? "unknown";
    /// <summary>失败诊断：涉及的通道及越界幅度（仅失败时可能非空）。</summary>
    public IReadOnlyList<CalibrationDiagnostic> Diagnostics => Done.Diagnostics;
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public string DiagnosticDetail => string.Join(", ", Diagnostics.Select(d => $"{ChannelLabel(d.Channel)} {d.Magnitude}"));

    /// <summary>把固件通道号(0..47 = 板*12+端口)渲染为可读标签，例如 B2P5 或键名。</summary>
    private static string ChannelLabel(byte channel)
    {
        var board = channel / 12;
        var port = channel % 12;
        var keyIndex = TelemetrySessionModel.KeyIndexForChannel(channel);
        return keyIndex >= 0
            ? $"B{board + 1}P{port}"
            : $"B{board + 1}P{port}(-UNMAPPED)";
    }

    /// <summary>
    /// 由校准前配置、校准后配置与 CAL_DONE 载荷计算摘要。
    /// <paramref name="current"/> 为 null 时表示配置读回失败，此时只给出基线信息。
    /// </summary>
    public static CalibrationSummary From(ConfigBlob? previous, ConfigBlob? current, CalDonePayload done)
    {
        var calibratedChannels = done.Baseline.Length;
        var minBaseline = int.MaxValue;
        var maxBaseline = int.MinValue;
        // 固件 baselines[channel] = latestBaseline(8 位寄存器精度) * 4，载荷里已是 ×4 后的值，这里不再乘。
        foreach (var baseline in done.Baseline)
        {
            if (baseline < minBaseline) minBaseline = baseline;
            if (baseline > maxBaseline) maxBaseline = baseline;
        }
        if (calibratedChannels == 0) { minBaseline = 0; maxBaseline = 0; }

        var boards = new List<CalibrationBoardResult>();
        if (current is not null)
        {
            for (var board = 0; board < current.Boards.Length; board++)
            {
                var previousThreshold = previous is not null && board < previous.Boards.Length ? previous.Boards[board].BaseThreshold : current.Boards[board].BaseThreshold;
                var mapped = 0;
                var changed = 0;
                for (var port = 0; port < 12; port++)
                {
                    var channel = board * 12 + port;
                    var keyIndex = TelemetrySessionModel.KeyIndexForChannel(channel);
                    if (keyIndex < 0 || channel >= done.TouchThreshold.Length) continue;
                    mapped++;
                    var previousValue = previous is not null ? ThresholdFor(previous, board, channel) : ThresholdFor(current, board, channel);
                    if (previousValue != done.TouchThreshold[channel]) changed++;
                }
                boards.Add(new CalibrationBoardResult(board + 1, previousThreshold, current.Boards[board].BaseThreshold, mapped, changed));
            }
        }

        return new CalibrationSummary(true, current is null, done, boards, calibratedChannels, minBaseline, maxBaseline);
    }

    /// <summary>校准失败时的空摘要（保留诊断块）。</summary>
    public static CalibrationSummary Failed(CalDonePayload done) => new(false, false, done, [], 0, 0, 0);

    /// <summary>与固件 thresholdForChannel 一致：clamp(baseThreshold + sensOffset + electrodeOffset, 1, 1023)。</summary>
    private static int ThresholdFor(ConfigBlob config, int board, int channel)
    {
        var keyIndex = TelemetrySessionModel.KeyIndexForChannel(channel);
        var offset = keyIndex >= 0 && keyIndex < config.ElectrodeOffsets.Length ? config.ElectrodeOffsets[keyIndex] : 0;
        return Math.Clamp(config.Boards[board].BaseThreshold + config.Boards[board].SensOffset + offset, 1, 1023);
    }

    /// <summary>一行概要：板级增量 + 已校准通道数 + 基线范围 + 读回状态。</summary>
    public string BuildHeadline()
    {
        if (!Succeeded)
        {
            var failure = string.Format(Localizer.Instance["Cal.ResultFailed"], FailureReasonName);
            return HasDiagnostics
                ? string.Format(Localizer.Instance["Cal.ResultFailedChannels"], failure, DiagnosticDetail)
                : failure;
        }
        var boards = ChangedBoardCount == 0
            ? Localizer.Instance["Cal.ResultNoBoardChange"]
            : string.Format(Localizer.Instance["Cal.ResultBoardsRaised"], ChangedBoardCount,
                string.Join(", ", Boards.Where(board => board.Changed).Select(board => $"{board.Label} {board.PreviousThreshold}→{board.CurrentThreshold}")));
        var baseline = CalibratedChannels == 0
            ? Localizer.Instance["Cal.ResultNoBaseline"]
            : string.Format(Localizer.Instance["Cal.ResultBaseline"], CalibratedChannels, MinBaseline, MaxBaseline);
        var readback = ReadbackFailed ? " " + Localizer.Instance["Cal.ResultReadbackFailed"] : string.Empty;
        return string.Format(Localizer.Instance["Cal.ResultHeadline"], boards, baseline, readback);
    }
}
