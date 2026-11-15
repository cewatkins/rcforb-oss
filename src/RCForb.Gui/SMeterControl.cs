using Avalonia;
using Avalonia.Media;

namespace RCForb.Gui;

/// <summary>
/// An analog S-meter (signal-strength meter) in the style of a classic radio
/// receiver gauge: cream face, brass bezel, S1..S9 scale with 6 dB steps,
/// red/yellow/green zones, and a tapered needle. The needle position is driven
/// by a normalised 0..1 value; the readout text is supplied by the caller
/// (typically the server's calibrated "S9 + 11" label).
/// </summary>
public sealed class SMeterControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SMeterControl, double>(nameof(Value), 0.0);
    public static readonly StyledProperty<string> ReadoutProperty =
        AvaloniaProperty.Register<SMeterControl, string>(nameof(Readout), "");
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SMeterControl, string>(nameof(Label), "S");

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Readout { get => GetValue(ReadoutProperty); set => SetValue(ReadoutProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    // Analog palette (echoes the vintage S-meter reference).
    private static readonly Avalonia.Media.Color BezelFillC = Avalonia.Media.Color.Parse("#231F19");
    private static readonly Avalonia.Media.Color BrassC = Avalonia.Media.Color.Parse("#9C8A55");
    private static readonly Avalonia.Media.Color RimC = Avalonia.Media.Color.Parse("#C9B77E");
    private static readonly Avalonia.Media.Color FaceCenterC = Avalonia.Media.Color.Parse("#E8D7A8");
    private static readonly Avalonia.Media.Color FaceEdgeC = Avalonia.Media.Color.Parse("#D9BE85");
    private static readonly Avalonia.Media.Color FaceCornerC = Avalonia.Media.Color.Parse("#B89A5E");
    private static readonly Avalonia.Media.Color InkC = Avalonia.Media.Color.Parse("#1A1712");
    private static readonly Avalonia.Media.Color RedZoneC = Avalonia.Media.Color.Parse("#ff4444");
    private static readonly Avalonia.Media.Color YellowZoneC = Avalonia.Media.Color.Parse("#ffcc00");
    private static readonly Avalonia.Media.Color GreenZoneC = Avalonia.Media.Color.Parse("#00ff00");
    private static readonly Avalonia.Media.Color NeedleC = Avalonia.Media.Color.Parse("#A879F0"); // violet, ties to waveform
    private static readonly Avalonia.Media.Color ReadoutC = Avalonia.Media.Color.Parse("#E4E6EA");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == ReadoutProperty ||
            change.Property == LabelProperty)
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

        double v = Math.Clamp(Value, 0.0, 1.0);
        // S1 at 150 deg (left), S9+ at 0 deg (right): 9 units x 15 deg = 135 deg span.
        double thetaValue = (1.0 - v) * (150.0 * Math.PI / 180.0);

        double bezelPad = 10.0;
        var housingRect = new Avalonia.Rect(cx - r - bezelPad, cy - r - bezelPad, (r + bezelPad) * 2, r + bezelPad * 2 + 6);

        // Housing (dark brown bezel)
        context.DrawRectangle(new Avalonia.Media.SolidColorBrush(BezelFillC),
            new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(BrassC), 1.5), housingRect, 6, 6);
        var rimPen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(RimC), 1.0);
        context.DrawLine(rimPen, new Avalonia.Point(housingRect.X + 6, housingRect.Y + 1),
                                 new Avalonia.Point(housingRect.X + housingRect.Width - 6, housingRect.Y + 1));

        // Cream face (clipped to upper semicircle)
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
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(FaceCenterC, 0.0));
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(FaceEdgeC, 0.72));
            gradient.GradientStops.Add(new Avalonia.Media.GradientStop(FaceCornerC, 1.0));
            context.DrawEllipse(gradient, null, new Avalonia.Point(cx, cy), r, r);
        }

        // Color zones: red S1-S3, yellow S3-S6, green S6-S9+ (each S-unit ~15 deg)
        // S1 at 150deg, S9 at 30deg. Zones by angle.
        DrawZoneArc(context, cx, cy, r, 150.0, 120.0, RedZoneC);    // S1-S3
        DrawZoneArc(context, cx, cy, r, 120.0, 75.0, YellowZoneC);  // S3-S6
        DrawZoneArc(context, cx, cy, r, 75.0, 0.0, GreenZoneC);     // S6-S9+

        // Scale arc outline
        var arcPen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(InkC), Math.Max(1.5, r * 0.012));
        {
            var pts = new Avalonia.Point[65];
            for (int i = 0; i <= 64; i++)
            {
                double th = (150.0 - 150.0 * (double)i / 64.0) * Math.PI / 180.0;
                pts[i] = new Avalonia.Point(cx + r * Math.Cos(th), cy - r * Math.Sin(th));
            }
            for (int i = 0; i < 64; i++)
                context.DrawLine(arcPen, pts[i], pts[i + 1]);
        }

        // Tick marks: S1..S9 at 15 deg steps
        for (int s = 1; s <= 9; s++)
        {
            double angleDeg = 150.0 - (s - 1) * 15.0;
            double th = angleDeg * Math.PI / 180.0;
            var tickPen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(InkC), Math.Max(1, r * 0.015));
            var p1 = new Avalonia.Point(cx + r * 0.86 * Math.Cos(th), cy - r * 0.86 * Math.Sin(th));
            var p2 = new Avalonia.Point(cx + r * 1.0 * Math.Cos(th), cy - r * 1.0 * Math.Sin(th));
            context.DrawLine(tickPen, p1, p2);

            // S-unit label
            double fontSize = Math.Max(9, r * 0.11);
            var ft = new Avalonia.Media.FormattedText(
                s.ToString(),
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
                fontSize,
                new Avalonia.Media.SolidColorBrush(InkC))
            { TextAlignment = Avalonia.Media.TextAlignment.Center };
            var center = new Avalonia.Point(cx + r * 0.70 * Math.Cos(th), cy - r * 0.70 * Math.Sin(th));
            context.DrawText(ft, center - new Avalonia.Vector(ft.Width / 2, ft.Height / 2));

            // +dB label below (S2..S9: +6..+48)
            if (s >= 2)
            {
                var dbFt = new Avalonia.Media.FormattedText(
                    "+" + ((s - 1) * 6).ToString(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    Avalonia.Media.FlowDirection.LeftToRight,
                    new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Normal),
                    Math.Max(7, r * 0.08),
                    new Avalonia.Media.SolidColorBrush(InkC))
                { TextAlignment = Avalonia.Media.TextAlignment.Center };
                var dbCenter = new Avalonia.Point(cx + r * 0.55 * Math.Cos(th), cy - r * 0.55 * Math.Sin(th));
                context.DrawText(dbFt, dbCenter - new Avalonia.Vector(dbFt.Width / 2, dbFt.Height / 2));
            }
        }

        // Needle (violet, ties to waveform accent)
        var needleBrush = new Avalonia.Media.SolidColorBrush(NeedleC);
        var needlePen = new Avalonia.Media.Pen(needleBrush, Math.Max(2, r * 0.025));
        double needleTipR = r * 0.94;
        var needleTip = new Avalonia.Point(cx + needleTipR * Math.Cos(thetaValue), cy - needleTipR * Math.Sin(thetaValue));
        context.DrawLine(needlePen, new Avalonia.Point(cx, cy), needleTip);
        context.DrawEllipse(needleBrush, null, new Avalonia.Point(cx, cy), r * 0.05, r * 0.05);

        // "S" label on the face
        double sFontSize = Math.Max(12, r * 0.16);
        var sFt = new Avalonia.Media.FormattedText(
            "S",
            System.Globalization.CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight,
            new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
            sFontSize,
            new Avalonia.Media.SolidColorBrush(InkC))
        { TextAlignment = Avalonia.Media.TextAlignment.Center };
        context.DrawText(sFt, new Avalonia.Point(cx - sFt.Width / 2, cy + r * 0.12));

        // Readout below the gauge
        if (!string.IsNullOrEmpty(Readout))
        {
            double roFontSize = Math.Max(11, r * 0.13);
            var roFt = new Avalonia.Media.FormattedText(
                Readout,
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface("Inter", Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
                roFontSize,
                new Avalonia.Media.SolidColorBrush(ReadoutC))
            { TextAlignment = Avalonia.Media.TextAlignment.Center };
            context.DrawText(roFt, new Avalonia.Point(cx - roFt.Width / 2, bounds.Height * 0.96 - roFt.Height));
        }
    }

    private static void DrawZoneArc(Avalonia.Media.DrawingContext context, double cx, double cy, double r,
        double startDeg, double endDeg, Avalonia.Media.Color color)
    {
        // Draw a filled wedge (pie slice) from pivot between two angles.
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        if (e > s) (s, e) = (e, s);
        var geom = new Avalonia.Media.StreamGeometry();
        using (var gctx = geom.Open())
        {
            gctx.BeginFigure(new Avalonia.Point(cx, cy), true);
            gctx.LineTo(new Avalonia.Point(cx + r * Math.Cos(s), cy - r * Math.Sin(s)));
            int segs = 24;
            for (int i = 1; i <= segs; i++)
            {
                double th = s + (e - s) * (double)i / segs;
                gctx.LineTo(new Avalonia.Point(cx + r * Math.Cos(th), cy - r * Math.Sin(th)));
            }
            gctx.LineTo(new Avalonia.Point(cx, cy));
            gctx.EndFigure(true);
        }
        context.DrawGeometry(new Avalonia.Media.SolidColorBrush(color), null, geom);
    }
}