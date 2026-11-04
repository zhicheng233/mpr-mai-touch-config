using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Controls;

/// <summary>Two independent count axes keep small drops visible alongside ~700-count baselines.</summary>
public sealed class SignalChart : Control
{
    public static readonly StyledProperty<SignalChartModel?> ModelProperty =
        AvaloniaProperty.Register<SignalChart, SignalChartModel?>(nameof(Model));
    public SignalChartModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    private SignalChartModel? subscribed;
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#35404D"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#AFBAC6"));
    private static readonly IBrush FilteredBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush BaselineBrush = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush DropBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush TouchBrush = new SolidColorBrush(Color.Parse("#FB7185"));
    private static readonly IBrush ReleaseBrush = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush OutputBrush = new SolidColorBrush(Color.Parse("#22D3EE"));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) { Subscribe(); InvalidateVisual(); }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Subscribe(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (subscribed is not null) subscribed.PropertyChanged -= ModelChanged;
        subscribed = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= ModelChanged;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignalChartModel.Revision)) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 160 || Bounds.Height < 180) return;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111820")), new Rect(Bounds.Size));
        var all = Model?.Points() ?? [];
        var valid = all.Where(p => p.Valid).ToArray();
        var left = 50.0;
        var width = Bounds.Width - left - 14;
        var paneHeight = (Bounds.Height - 90) / 2;
        var top = new Rect(left, 20, width, paneHeight);
        var bottom = new Rect(left, top.Bottom + 30, width, paneHeight);
        var end = all.Count > 0 ? (double)all[^1].TimeMs : 0;
        double X(SignalChartPoint p) => left + (p.TimeMs - end + SignalChartModel.WindowMs) / SignalChartModel.WindowMs * width;
        var absoluteMin = valid.Length > 0 ? valid.Min(p => Math.Min(p.Filtered, p.Baseline)) : 0;
        var absoluteMax = valid.Length > 0 ? valid.Max(p => Math.Max(p.Filtered, p.Baseline)) : 100;
        var dropMin = valid.Length > 0 ? Math.Min(0, valid.Min(p => p.Drop)) : 0;
        var dropMax = valid.Length > 0 ? Math.Max(8, valid.Max(p => Math.Max(p.CandidateThreshold, Math.Max(p.Drop, p.TouchThreshold)))) : 100;
        var absolutePadding = Math.Max(4, (absoluteMax - absoluteMin) * 0.1);
        var dropPadding = Math.Max(2, (dropMax - dropMin) * 0.1);
        var minA = absoluteMin - absolutePadding;
        var maxA = absoluteMax + absolutePadding;
        var minD = dropMin - dropPadding;
        var maxD = dropMax + dropPadding;
        double Y(Rect pane, double value, double min, double max) => pane.Bottom - (value - min) / (max - min) * pane.Height;

        void Label(string text, double x, double y, IBrush? brush = null) => context.DrawText(
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush ?? TextBrush), new Point(x, y));
        void Axes(Rect pane, double min, double max)
        {
            for (var i = 0; i <= 2; i++)
            {
                var value = min + (max - min) * i / 2;
                var y = Y(pane, value, min, max);
                context.DrawLine(new Pen(GridBrush, 1), new Point(pane.Left, y), new Point(pane.Right, y));
                Label(value.ToString("0", CultureInfo.CurrentCulture), 3, y - 7);
            }
            for (var i = 0; i <= 3; i++)
            {
                var x = pane.Left + pane.Width * i / 3;
                context.DrawLine(new Pen(GridBrush, 1), new Point(x, pane.Top), new Point(x, pane.Bottom));
            }
        }
        Axes(top, minA, maxA);
        Axes(bottom, minD, maxD);
        Label(Localizer.Instance["Chart.Absolute"], top.Left, 3);
        Label(Localizer.Instance["Chart.Difference"], bottom.Left, bottom.Top - 17);
        for (var i = 0; i <= 3; i++) Label($"{i * 10 - 30}", left + width * i / 3 - 8, bottom.Bottom + 15);
        Label(Localizer.Instance["Chart.Time"], left, Bounds.Height - 15);
        if (valid.Length == 0) { Label(Localizer.Instance["Chart.Empty"], left + 10, top.Top + 12); return; }

        void Curve(Rect pane, Func<SignalChartPoint, double> value, double min, double max, IBrush brush)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var open = false;
                foreach (var point in all)
                {
                    if (!point.Valid || !point.Connect)
                    {
                        if (open) path.EndFigure(false);
                        open = false;
                    }
                    if (!point.Valid) continue;
                    var position = new Point(X(point), Y(pane, value(point), min, max));
                    if (!open) { path.BeginFigure(position, false); open = true; }
                    else path.LineTo(position);
                }
                if (open) path.EndFigure(false);
            }
            using (context.PushClip(pane)) context.DrawGeometry(null, new Pen(brush, 1.5), geometry);
        }
        Curve(top, p => p.Filtered, minA, maxA, FilteredBrush);
        Curve(top, p => p.Baseline, minA, maxA, BaselineBrush);
        Curve(bottom, p => p.Drop, minD, maxD, DropBrush);
        Curve(bottom, p => p.TouchThreshold, minD, maxD, TouchBrush);
        Curve(bottom, p => p.ReleaseThreshold, minD, maxD, ReleaseBrush);
        if (valid.Any(p => p.CandidateThreshold > 0)) Curve(bottom, p => p.CandidateThreshold > 0 ? p.CandidateThreshold : p.TouchThreshold, minD, maxD, OutputBrush);
        for (var i = 1; i < all.Count; i++)
        {
            if (all[i].Valid && all[i].Connect && all[i - 1].HardwareTouched)
                context.FillRectangle(TouchBrush, new Rect(X(all[i - 1]), bottom.Bottom + 3,
                    Math.Max(1, X(all[i]) - X(all[i - 1])), 3));
            if (all[i].Valid && all[i].Connect && all[i - 1].Touched)
                context.FillRectangle(OutputBrush, new Rect(X(all[i - 1]), bottom.Bottom + 7,
                    Math.Max(1, X(all[i]) - X(all[i - 1])), 3));
        }
    }
}
