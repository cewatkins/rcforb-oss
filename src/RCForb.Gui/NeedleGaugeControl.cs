using Avalonia;

namespace RCForb.Gui;

public sealed class NeedleGaugeControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Value), 0.0);
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Minimum), -40.0);
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, double>(nameof(Maximum), 3.0);
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, string>(nameof(Label), "VU");
    public static readonly StyledProperty<string> ValueTextProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, string>(nameof(ValueText), "");
    public static readonly StyledProperty<Avalonia.Media.IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(NeedleBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(TrackBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(TickBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(TextBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> GreenZoneBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(GreenZoneBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> YellowZoneBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(YellowZoneBrush), null);
    public static readonly StyledProperty<Avalonia.Media.IBrush?> RedZoneBrushProperty =
        AvaloniaProperty.Register<NeedleGaugeControl, Avalonia.Media.IBrush?>(nameof(RedZoneBrush), null);

    // Default brush values (per spec)
    private static Avalonia.Media.IBrush DefaultNeedleBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 240, 160, 48));
    private static Avalonia.Media.IBrush DefaultTrackBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 42, 46, 54));
    private static Avalonia.Media.IBrush DefaultTickBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 154, 160, 166));
    private static Avalonia.Media.IBrush DefaultTextBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 232, 234, 237));
    private static Avalonia.Media.IBrush DefaultGreenZoneBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 126, 231, 135));
    private static Avalonia.Media.IBrush DefaultYellowZoneBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 209, 102));
    private static Avalonia.Media.IBrush DefaultRedZoneBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 107, 107));

    // CLR wrappers (get AND set, bindings need setters)
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public Avalonia.Media.IBrush? NeedleBrush { get => GetValue(NeedleBrushProperty); set => SetValue(NeedleBrushProperty, value); }
    public Avalonia.Media.IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Avalonia.Media.IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public Avalonia.Media.IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Avalonia.Media.IBrush? GreenZoneBrush { get => GetValue(GreenZoneBrushProperty); set => SetValue(GreenZoneBrushProperty, value); }
    public Avalonia.Media.IBrush? YellowZoneBrush { get => GetValue(YellowZoneBrushProperty); set => SetValue(YellowZoneBrushProperty, value); }
    public Avalonia.Media.IBrush? RedZoneBrush { get => GetValue(RedZoneBrushProperty); set => SetValue(RedZoneBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty ||
            change.Property == MaximumProperty || change.Property == LabelProperty ||
            change.Property == ValueTextProperty || change.Property == NeedleBrushProperty ||
            change.Property == TrackBrushProperty || change.Property == TickBrushProperty ||
            change.Property == TextBrushProperty || change.Property == GreenZoneBrushProperty ||
            change.Property == YellowZoneBrushProperty || change.Property == RedZoneBrushProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(Avalonia.Media.DrawingContext context)
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

        double normalized = (Value - Minimum) / range;
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        double thetaValue = Math.PI * (1.0 - normalized);

        // Analog VU palette (echoes the vintage cream-faced gauge).
        var bezelFill  = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#231F19"));
        var brass      = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9C8A55"));
        var rim        = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C9B77E"));
        var faceCenter = Avalonia.Media.Color.Parse("#E8D7A8");
        var faceEdge   = Avalonia.Media.Color.Parse("#D9BE85");
        var faceCorner = Avalonia.Media.Color.Parse("#B89A5E");
        var darkInk    = Avalonia.Media.Color.Parse("#1A1712");
        var redWedge   = Avalonia.Media.Color.Parse("#C8201E");
        var redLabel   = Avalonia.Media.Color.Parse("#C0281F");
        var vuInk      = Avalonia.Media.Color.Parse("#2A2A25");
        var jewelDark  = Avalonia.Media.Color.Parse("#8B1A1A");
        var jewelLight = Avalonia.Media.Color.Parse("#C0392B");
        var needleInk  = Avalonia.Media.Color.Parse("#141210");
        var readoutInk = Avalonia.Media.Color.Parse("#E4E6EA");

        double bezelPad = 10.0;
        var housingRect = new Avalonia.Rect(cx - r - bezelPad, cy - r - bezelPad, (r + bezelPad) * 2, r + bezelPad * 2 + 6);

        // --- Housing (dark brown bezel) ---
        context.DrawRectangle(bezelFill, new Avalonia.Media.Pen(brass, 1.5), housingRect, 6, 6);
        var rimPen = new Avalonia.Media.Pen(rim, 1.0);
        context.DrawLine(rimPen, new Avalonia.Point(housingRect.X + 6, housingRect.Y + 1),
                                 new Avalonia.Point(housingRect.X + housingRect.Width - 6, housingRect.Y + 1));

        // --- Cream face (clipped to upper semicircle) ---
        var clipRect = new Avalonia.Rect(cx - r, cy - r, r * 2, r);
        using (context.PushClip(clipRect))
        {
            var gradient = new Avalonia.Media.RadialGradientBrush
            {
                Center = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                GradientOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                RadiusX = new Avalonia.RelativeScalar(0.95, Avalonia.RelativeUnit.Relative),
                RadiusY = new Avalonia.RelativeScalar(0.95, Avalonia.RelativeUnit.Relative),
            };
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(faceCenter, 0.0));
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(faceEdge, 0.72));
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(faceCorner, 1.0));
            context.DrawEllipse(gradient, null, new Avalonia.Point(cx, cy), r, r);
        }

        // --- Red overload wedge (0 .. +3) as a filled pie slice from the pivot ---
        double t0 = (0.0 - Minimum) / range;
        double t3 = (3.0 - Minimum) / range;
        if (t3 > t0)
        {
            double th0 = Math.PI * (1.0 - t0);
            double th3 = Math.PI * (1.0 - t3);
            var wedgeGeom = new Avalonia.Media.StreamGeometry();
            using (var gctx = wedgeGeom.Open())
            {
                gctx.BeginFigure(new Avalonia.Point(cx, cy), true);
                gctx.LineTo(PointHelper(cx, cy, r, th0));
                int segs = 24;
                for (int i = 1; i <= segs; i++)
                {
                    double th = th0 + (th3 - th0) * (double)i / segs;
                    gctx.LineTo(PointHelper(cx, cy, r, th));
                }
                gctx.LineTo(new Avalonia.Point(cx, cy));
                gctx.EndFigure(true);
            }
            context.DrawGeometry(new Avalonia.Media.SolidColorBrush(redWedge), null, wedgeGeom);
        }

        // --- Scale arc (semicircle outline) ---
        var arcPen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(darkInk), Math.Max(1.5, r * 0.012));
        {
            var pts = new Avalonia.Point[65];
            for (int i = 0; i <= 64; i++)
            {
                double th = Math.PI * (1.0 - (double)i / 64.0);
                pts[i] = PointHelper(cx, cy, r, th);
            }
            for (int i = 0; i < 64; i++)
                context.DrawLine(arcPen, pts[i], pts[i + 1]);
        }

        // --- Tick marks ---
        double[] tickValues = { -40.0, -30.0, -20.0, -10.0, -5.0, 0.0, 3.0 };
        foreach (double tv in tickValues)
        {
            if (tv < Minimum || tv > Maximum) continue;
            double t = (tv - Minimum) / range;
            double th = Math.PI * (1.0 - t);
            var tickPen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(darkInk), Math.Max(1, r * 0.015));
            double innerR = r * 0.86;
            double outerR = r * 1.0;
            var p1 = PointHelper(cx, cy, innerR, th);
            var p2 = PointHelper(cx, cy, outerR, th);
            context.DrawLine(tickPen, p1, p2);
        }

        // --- Tick labels ---
        double labelFontSize = Math.Max(9, r * 0.10);
        double[] labelValues = { -40, -30, -20, -10, -5, 0, 3 };
        foreach (double tv in labelValues)
        {
            if (tv < Minimum || tv > Maximum) continue;
            double t = (tv - Minimum) / range;
            double th = Math.PI * (1.0 - t);
            var ft = new Avalonia.Media.FormattedText(
                FormatTick(tv),
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
                labelFontSize,
                new Avalonia.Media.SolidColorBrush((tv >= 0) ? redLabel : darkInk))
            {
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            };
            var center = PointHelper(cx, cy, r * 0.70, th);
            context.DrawText(ft, center - new Avalonia.Vector(ft.Width / 2, ft.Height / 2));
        }

        // --- Needle (dark, tapered) ---
        var needlePen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(needleInk), Math.Max(2, r * 0.025));
        double needleTipR = r * 0.94;
        var needleTip = PointHelper(cx, cy, needleTipR, thetaValue);
        context.DrawLine(needlePen, new Avalonia.Point(cx, cy), needleTip);
        context.DrawEllipse(new Avalonia.Media.SolidColorBrush(needleInk), null, new Avalonia.Point(cx, cy), r * 0.05, r * 0.05);

        // --- "VU" label on the face ---
        double vuFontSize = Math.Max(12, r * 0.16);
        var vuFt = new Avalonia.Media.FormattedText(
            "VU",
            System.Globalization.CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight,
            new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
            vuFontSize,
            new Avalonia.Media.SolidColorBrush(vuInk))
        {
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };
        context.DrawText(vuFt, new Avalonia.Point(cx - vuFt.Width / 2, cy + r * 0.12));

        // --- Peak jewel lamp (lower right) ---
        var jewelCenter = new Avalonia.Point(cx + r * 0.78, cy + r * 0.05);
        context.DrawEllipse(new Avalonia.Media.SolidColorBrush(jewelDark), null, jewelCenter, r * 0.06, r * 0.06);
        context.DrawEllipse(new Avalonia.Media.SolidColorBrush(jewelLight), null, jewelCenter, r * 0.03, r * 0.03);

        // --- ValueText readout below the gauge ---
        if (!string.IsNullOrEmpty(ValueText))
        {
            double valueFontSize = Math.Max(10, r * 0.12);
            var valueFt = new Avalonia.Media.FormattedText(
                ValueText,
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
                valueFontSize,
                new Avalonia.Media.SolidColorBrush(readoutInk))
            {
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            };
            context.DrawText(valueFt, new Avalonia.Point(cx - valueFt.Width / 2, bounds.Height * 0.96 - valueFt.Height));
        }
    }

    /// <summary>
    /// Formats a tick value as a signed integer without decimal places, e.g. -40, 0, +3.
    /// </summary>
    private static string FormatTick(double v)
    {
        if (v > 0) return "+" + v.ToString("0");
        if (v < 0) return "-" + Math.Abs(v).ToString("0");
        return "0";
    }

    /// <summary>
    /// Draws a thick arc (rounded caps) approximated by ~64 straight segments between
    /// startTheta and endTheta around (cx, cy) at radius r. Segments are drawn via
    /// DrawLine so each step is treated independently. If endTheta &lt; startTheta the
    /// endpoints are swapped to sweep the shorter positive-angle path.
    /// </summary>
    private static void DrawArc(double cx, double cy, double r, double startTheta, double endTheta, double thickness, Avalonia.Media.IBrush brush, Avalonia.Media.DrawingContext ctx)
    {
        if (endTheta <= startTheta) (startTheta, endTheta) = (endTheta, startTheta);
        int segments = 64;
        var pen = new Avalonia.Media.Pen(brush, thickness, lineCap: Avalonia.Media.PenLineCap.Round);
        double prevTheta = startTheta;
        var prevPoint = PointHelper(cx, cy, r, startTheta);
        for (int i = 1; i <= segments; i++)
        {
            double t = (double)i / segments;
            double th = startTheta + (endTheta - startTheta) * t;
            var pt = PointHelper(cx, cy, r, th);
            ctx.DrawLine(pen, prevPoint, pt);
            prevPoint = pt;
            prevTheta = th;
        }
    }

    /// <summary>
    /// Converts a screen angle theta (radians) to a point on the circle:
    /// x = cx + R*cos(theta), y = cy - R*sin(theta).
    /// </summary>
    private static Avalonia.Point PointHelper(double cx, double cy, double r, double theta)
    {
        return new Avalonia.Point(cx + r * Math.Cos(theta), cy - r * Math.Sin(theta));
    }
}
