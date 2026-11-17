using System;
using Avalonia;
using Avalonia.Media;

namespace RCForb.Gui;

/// <summary>
/// An authentic analog VU (Volume Unit) meter in the style of a broadcast/audio
/// VU panel: cream face, brass bezel, -20..+3 dB scale with a red overload zone,
/// dark tapered needle, and a red peak-hold jewel lamp that sticks when the signal
/// enters the red zone.
///
/// Matches the classic VU appearance: scale from -20 to +3, red zone from 0 to +3,
/// "VU" lettering on the face, and a peak indicator that holds briefly.
/// </summary>
public sealed class NeedleGaugeControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Value), 0.0);
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Minimum), -20.0);
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Maximum), 3.0);
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, string>(nameof(Label), "VU");
    public static readonly StyledProperty<string> ValueTextProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, string>(nameof(ValueText), "");
    public static readonly StyledProperty<IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, IBrush?>(nameof(NeedleBrush), null);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, IBrush?>(nameof(TrackBrush), null);
    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, IBrush?>(nameof(TickBrush), null);
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, IBrush?>(nameof(TextBrush), null);
    public static readonly StyledProperty<IBrush?> RedZoneBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, IBrush?>(nameof(RedZoneBrush), null);

    /// <summary>Peak-hold level (sticks at the highest recent value, decays slowly).</summary>
    public static readonly StyledProperty<double> PeakValueProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(PeakValue), 0.0);

    /// <summary>True when the peak-hold lamp should be lit (in the red zone).</summary>
    public static readonly StyledProperty<bool> IsPeakLitProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, bool>(nameof(IsPeakLit), false);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public IBrush? NeedleBrush { get => GetValue(NeedleBrushProperty); set => SetValue(NeedleBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? RedZoneBrush { get => GetValue(RedZoneBrushProperty); set => SetValue(RedZoneBrushProperty, value); }
    public double PeakValue { get => GetValue(PeakValueProperty); set => SetValue(PeakValueProperty, value); }
    public bool IsPeakLit { get => GetValue(IsPeakLitProperty); set => SetValue(IsPeakLitProperty, value); }

    // VU palette matching the reference instrument.
    private static readonly Color BezelFillC = Color.Parse("#231F19");
    private static readonly Color BrassC = Color.Parse("#9C8A55");
    private static readonly Color RimC = Color.Parse("#C9B77E");
    private static readonly Color FaceCenterC = Color.Parse("#E8D7A8");
    private static readonly Color FaceEdgeC = Color.Parse("#D9BE85");
    private static readonly Color FaceCornerC = Color.Parse("#B89A5E");
    private static readonly Color InkC = Color.Parse("#1A1712");
    private static readonly Color RedZoneC = Color.Parse("#C8201E");
    private static readonly Color RedLabelC = Color.Parse("#C0281F");
    private static readonly Color JewelDarkC = Color.Parse("#7A1515");
    private static readonly Color JewelLitC = Color.Parse("#FF4444");
    private static readonly Color JewelGlowC = Color.Parse("#FF8A8A");
    private static readonly Color NeedleInkC = Color.Parse("#141210");
    private static readonly Color ReadoutInkC = Color.Parse("#E4E6EA");

    private double _peakHold; // decaying peak-hold value

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty ||
            change.Property == MaximumProperty || change.Property == LabelProperty ||
            change.Property == ValueTextProperty || change.Property == NeedleBrushProperty ||
            change.Property == TrackBrushProperty || change.Property == TickBrushProperty ||
            change.Property == TextBrushProperty || change.Property == RedZoneBrushProperty ||
            change.Property == PeakValueProperty || change.Property == IsPeakLitProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var cx = bounds.Width / 2.0;
        var cy = bounds.Height * 0.86;
        var r = Math.Min(bounds.Width / 2.0, bounds.Height * 0.86) * 0.92;
        if (r <= 0) return;

        var range = Maximum - Minimum;
        if (range == 0) return;

        double Normalized(double v) => Math.Clamp((v - Minimum) / range, 0.0, 1.0);
        double Theta(double v) => Math.PI * (1.0 - Normalized(v));

        var needle = NeedleBrush ?? new SolidColorBrush(Color.FromRgb(20, 18, 16));
        var redZone = RedZoneBrush ?? new SolidColorBrush(RedZoneC);
        var text = TextBrush ?? new SolidColorBrush(InkC);

        double bezelPad = 10.0;
        var housingRect = new Rect(cx - r - bezelPad, cy - r - bezelPad,
                                   (r + bezelPad) * 2, r + bezelPad * 2 + 6);

        // --- Housing (dark brown bezel) ---
        context.DrawRectangle(new SolidColorBrush(BezelFillC), new Pen(new SolidColorBrush(RimC), 1.5),
                              housingRect, 6, 6);

        // --- Cream face (upper semicircle) ---
        var clipRect = new Rect(cx - r, cy - r, r * 2, r);
        using (context.PushClip(clipRect))
        {
            var gradient = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.95, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.95, RelativeUnit.Relative),
            };
            gradient.GradientStops.Add(new GradientStop(FaceCenterC, 0.0));
            gradient.GradientStops.Add(new GradientStop(FaceEdgeC, 0.72));
            gradient.GradientStops.Add(new GradientStop(FaceCornerC, 1.0));
            context.DrawEllipse(gradient, null, new Point(cx, cy), r, r);
        }

        // --- Red overload wedge (0 .. +3 dB) as filled pie slice ---
        double t0 = Normalized(0.0);
        double t3 = Normalized(3.0);
        double th0 = Theta(0.0);
        double th3 = Theta(3.0);
        var wedge = new StreamGeometry();
        using (var gctx = wedge.Open())
        {
            gctx.BeginFigure(new Point(cx, cy), true);
            gctx.LineTo(PointHelper(cx, cy, r, th0));
            int segs = 24;
            for (int i = 1; i <= segs; i++)
            {
                double th = th0 + (th3 - th0) * i / segs;
                gctx.LineTo(PointHelper(cx, cy, r, th));
            }
            gctx.LineTo(new Point(cx, cy));
            gctx.EndFigure(true);
        }
        context.DrawGeometry(redZone, null, wedge);

        // --- Scale arc outline ---
        var arcPts = new Point[65];
        for (int i = 0; i <= 64; i++)
        {
            double th = Math.PI * (1.0 - i / 64.0);
            arcPts[i] = PointHelper(cx, cy, r, th);
        }
        for (int i = 0; i < 64; i++)
            context.DrawLine(new Pen(new SolidColorBrush(InkC), Math.Max(1.5, r * 0.012)),
                             arcPts[i], arcPts[i + 1]);

        // --- Tick marks and labels (-20, -10, -7, -5, -3, -2, -1, 0, +1, +2, +3) ---
        double[] tickValues = { -20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3 };
        foreach (double tv in tickValues)
        {
            if (tv < Minimum || tv > Maximum) continue;
            double t = Normalized(tv);
            double th = Theta(tv);
            var tickPen = new Pen(new SolidColorBrush(InkC), Math.Max(1, r * 0.015));
            double innerR = r * 0.86;
            double outerR = r * 1.0;
            context.DrawLine(tickPen, PointHelper(cx, cy, innerR, th),
                             PointHelper(cx, cy, outerR, th));

            double labelFontSize = Math.Max(8, r * 0.09);
            var ft = new FormattedText(
                tv > 0 ? "+" + tv.ToString("0") : tv.ToString("0"),
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
                labelFontSize,
                new SolidColorBrush(tv >= 0 ? RedLabelC : InkC))
            { TextAlignment = TextAlignment.Center };
            var center = PointHelper(cx, cy, r * 0.68, th);
            context.DrawText(ft, center - new Vector(ft.Width / 2, ft.Height / 2));
        }

        // --- Needle (dark, tapered) ---
        double needleTipR = r * 0.94;
        var needleTip = PointHelper(cx, cy, needleTipR, Theta(Value));
        context.DrawLine(new Pen(needle, Math.Max(2, r * 0.025)), new Point(cx, cy), needleTip);
        context.DrawEllipse(needle, null, new Point(cx, cy), r * 0.05, r * 0.05);

        // --- Peak-hold needle (faint, sticks at peak) ---
        if (_peakHold > Minimum)
        {
            var peakPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 32, 30)), Math.Max(1.5, r * 0.02));
            var peakTip = PointHelper(cx, cy, needleTipR * 0.9, Theta(_peakHold));
            context.DrawLine(peakPen, new Point(cx, cy), peakTip);
        }

        // --- "VU" label on the face ---
        double vuFontSize = Math.Max(12, r * 0.16);
        var vuFt = new FormattedText(
            Label,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
            vuFontSize,
            new SolidColorBrush(InkC))
        { TextAlignment = TextAlignment.Center };
        context.DrawText(vuFt, new Point(cx - vuFt.Width / 2, cy + r * 0.12));

        // --- Peak jewel lamp (lower right) ---
        var jewelCenter = new Point(cx + r * 0.78, cy + r * 0.05);
        bool lit = IsPeakLit || Value >= 0;
        context.DrawEllipse(new SolidColorBrush(JewelDarkC), null, jewelCenter, r * 0.06, r * 0.06);
        if (lit)
        {
            context.DrawEllipse(new SolidColorBrush(JewelGlowC), null, jewelCenter, r * 0.045, r * 0.045);
            context.DrawEllipse(new SolidColorBrush(JewelLitC), null, jewelCenter, r * 0.022, r * 0.022);
        }
        else
        {
            context.DrawEllipse(new SolidColorBrush(JewelLitC), null, jewelCenter, r * 0.022, r * 0.022);
        }

        // --- ValueText readout below the gauge ---
        if (!string.IsNullOrEmpty(ValueText))
        {
            double valueFontSize = Math.Max(10, r * 0.12);
            var valueFt = new FormattedText(
                ValueText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
                valueFontSize,
                new SolidColorBrush(ReadoutInkC))
            { TextAlignment = TextAlignment.Center };
            context.DrawText(valueFt, new Point(cx - valueFt.Width / 2, bounds.Height * 0.96 - valueFt.Height));
        }
    }

    /// <summary>Decay the peak-hold value toward the current value (call each UI tick).</summary>
    public void TickPeakHold(double decayPerSecond)
    {
        if (_peakHold > Value)
        {
            _peakHold = Math.Max(Value, _peakHold - decayPerSecond * 0.05); // ~5% per tick at 250ms
            InvalidateVisual();
        }
        else if (_peakHold < Value)
        {
            _peakHold = Value;
            InvalidateVisual();
        }
    }

    private static Point PointHelper(double cx, double cy, double r, double theta)
    {
        return new Point(cx + r * Math.Cos(theta), cy - r * Math.Sin(theta));
    }
}
