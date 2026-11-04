namespace MaimaiTouchTool.ViewModels;

public sealed record GuidedIdleObservation(ulong TimeMs, double[] Drops, ulong TouchBits, ushort[]? Filtered = null);
public sealed record GuidedIdleStatistics(double Median, double Upper, double Noise, double Drift, bool Transient)
{
    // Stationary noise is handled by the threshold safety interval, not a blanket rejection.
    public bool Unstable => Drift > 8;

    public static GuidedIdleStatistics From(IReadOnlyList<double> values)
    {
        if (values.Count < 3) return new(0, 0, 0, double.PositiveInfinity, false);
        var noise = GuidedStatistics.Quantile(values, 0.95) - GuidedStatistics.Quantile(values, 0.05);
        var drift = Math.Abs(GuidedStatistics.Quantile(values.Take(values.Count / 2), 0.5) -
            GuidedStatistics.Quantile(values.Skip(values.Count / 2), 0.5));
        return new(GuidedStatistics.Quantile(values, 0.5), GuidedStatistics.Quantile(values, 0.95),
            Math.Max(0, noise), drift, values.Max() - values.Min() > Math.Max(12, noise * 10));
    }

    public static bool HasIdleTouch(IReadOnlyList<GuidedIdleObservation> frames, int key)
    {
        double start = -1;
        var count = 0;
        ulong previous = 0;
        foreach (var frame in frames)
        {
            if ((frame.TouchBits & (1UL << key)) != 0)
            {
                count++;
                if (start < 0 || frame.TimeMs - previous > 250) start = frame.TimeMs;
                if (frame.TimeMs - start >= 90) return true;
            }
            else start = -1;
            previous = frame.TimeMs;
        }
        // Repeated brief idle touches are still a failure; one isolated flicker is only a warning.
        return count >= 3 && count >= frames.Count * 0.1;
    }
}
