using System;
using Avalonia;
using Avalonia.Media;

namespace RCForb.Gui;

/// <summary>
/// A bearing compass in the style of the RCForb client: a rotating dial marked in
/// degrees (0..360) with a fixed lubber line at top and a needle that points to the
/// current bearing. Receive-side only (no license required) — the bearing comes from
/// the server's state or a manual setting.
/// </summary>
public sealed class CompassControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> BearingProperty =
        AvaloniaProperty.Register<CompassControl, double>(nameof(Bearing), 0.0);

    /// <summary>Current bearing in degrees, 0..360. 0 = North (top).</summary>
    public double Bearing { get => GetValue(BearingProperty); set => SetValue(BearingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BearingProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var cx = bounds.Width / 2.0;
        var cy = bounds.Height / 2.0;
        var r = Math.Min(bounds.Width, bounds.Height) * 0.42;
        if (r <= 0) return;

        var ink = new SolidColorBrush(Color.Parse("#1a1712"));
        var red = new SolidColorBrush(Color.Parse("#c8201e"));
        var white = new SolidColorBrush(Color.Parse("#ffffff"));
        var grey = new SolidColorBrush(Color.Parse("#808080"));

        double bearing = Bearing;
        // Normalize to 0..360
        bearing = ((bearing % 360.0) + 360.0) % 360.0;

        // --- Outer bezel ring ---
        context.DrawEllipse(null, new Pen(ink, 2), new Point(cx, cy), r, r);
        context.DrawEllipse(null, new Pen(grey, 1), new Point(cx, cy), r * 0.92, r * 0.92);

        // --- Degree ticks and labels every 30 degrees ---
        for (int i = 0; i < 12; i++)
        {
            double deg = i * 30.0;
            // Rotate so 0 deg is at top: standard math angle = 90 - deg
            double th = Math.PI * (90.0 - deg) / 180.0;
            double innerR = r * 0.82;
            double outerR = r * 0.92;
            var p1 = new Point(cx + innerR * Math.Cos(th), cy - innerR * Math.Sin(th));
            var p2 = new Point(cx + outerR * Math.Cos(th), cy - outerR * Math.Sin(th));
            context.DrawLine(new Pen(ink, i % 3 == 0 ? 2 : 1), p1, p2);

            // Cardinal labels N/E/S/W at 0/90/180/270
            string label = deg switch
            {
                0 => "N",
                90 => "E",
                180 => "S",
                270 => "W",
                _ => ""
            };
            if (label.Length > 0)
            {
                double labelR = r * 0.68;
                var lp = new Point(cx + labelR * Math.Cos(th), cy - labelR * Math.Sin(th));
                var ft = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
                    Math.Max(10, r * 0.13),
                    ink)
                { TextAlignment = TextAlignment.Center };
                context.DrawText(ft, lp - new Vector(ft.Width / 2, ft.Height / 2));
            }
        }

        // --- Center pivot ---
        context.DrawEllipse(ink, null, new Point(cx, cy), r * 0.04, r * 0.04);
        context.DrawEllipse(white, null, new Point(cx, cy), r * 0.02, r * 0.02);

        // --- Needle: points to bearing ---
        double needleTh = Math.PI * (90.0 - bearing) / 180.0;
        double needleR = r * 0.72;
        var tip = new Point(cx + needleR * Math.Cos(needleTh), cy - needleR * Math.Sin(needleTh));
        context.DrawLine(new Pen(red, Math.Max(2, r * 0.02)), new Point(cx, cy), tip);
        context.DrawEllipse(red, null, tip, r * 0.03, r * 0.03);

        // --- Lubber line at top (fixed reference) ---
        context.DrawLine(new Pen(ink, 2), new Point(cx, cy - r * 0.95), new Point(cx, cy - r * 0.88));
    }
}
