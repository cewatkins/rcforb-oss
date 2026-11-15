using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Metadata;

namespace RCForb.Gui;

/// <summary>
/// A glowing arc instrument meter with ticks, labels and a thin needle.
/// Renders without per-frame allocation.
/// </summary>
public sealed class ArcMeter : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ArcMeter, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ArcMeter, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ArcMeter, double>(nameof(Value), 0.0);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ArcMeter, string>(nameof(Title), "");

    public static readonly StyledProperty<string> DigitalReadoutProperty =
        AvaloniaProperty.Register<ArcMeter, string>(nameof(DigitalReadout), "");

    public static readonly StyledProperty<IReadOnlyList<ArcMeterTick>> TicksProperty =
        AvaloniaProperty.Register<ArcMeter, IReadOnlyList<ArcMeterTick>>(nameof(Ticks), []);

    public static readonly StyledProperty<bool> RestingProperty =
        AvaloniaProperty.Register<ArcMeter, bool>(nameof(Resting), false);

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<ArcMeter, Color>(nameof(AccentColor), Color.Parse("#46b7ff"));

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string DigitalReadout
    {
        get => GetValue(DigitalReadoutProperty);
        set => SetValue(DigitalReadoutProperty, value);
    }

    [Content]
    public IReadOnlyList<ArcMeterTick> Ticks
    {
        get => GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    public bool Resting
    {
        get => GetValue(RestingProperty);
        set => SetValue(RestingProperty, value);
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    private static readonly Point _zero = new(0, 0);

    private readonly Pen _tickPen;
    private readonly Pen _needlePen;
    private readonly Pen _needleGlowPen;
    private readonly Pen _arcPen;
    private readonly Pen _arcGlowPen;
    private readonly SolidColorBrush _labelBrush;
    private readonly SolidColorBrush _digitalBrush;
    private readonly SolidColorBrush _restingBrush;
    private readonly Typeface _labelTypeface;
    private readonly Typeface _digitalTypeface;

    public ArcMeter()
    {
        _tickPen = new Pen(Brushes.Transparent, 1.0);
        _needlePen = new Pen(Brushes.Transparent, 1.5);
        _needleGlowPen = new Pen(Brushes.Transparent, 4.0);
        _arcPen = new Pen(Brushes.Transparent, 2.0);
        _arcGlowPen = new Pen(Brushes.Transparent, 8.0);
        _labelBrush = new SolidColorBrush();
        _digitalBrush = new SolidColorBrush();
        _restingBrush = new SolidColorBrush();
        _labelTypeface = new Typeface("IBM Plex Sans, Noto Sans, Segoe UI, Cantarell, sans-serif");
        _digitalTypeface = new Typeface("IBM Plex Mono, JetBrains Mono, Fira Code, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty ||
            change.Property == MinimumProperty ||
            change.Property == MaximumProperty ||
            change.Property == DigitalReadoutProperty ||
            change.Property == RestingProperty ||
            change.Property == AccentColorProperty ||
            change.Property == TicksProperty ||
            change.Property == TitleProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        if (width <= 10 || height <= 10)
        {
            return;
        }

        var accent = AccentColor;
        var muted = new Color(0x55, accent.R, accent.G, accent.B);
        var dim = new Color(0x99, accent.R, accent.G, accent.B);
        var panelText = Color.Parse("#7f8f9f");
        var text = Color.Parse("#dfe7ef");

        _tickPen.Brush = new SolidColorBrush(dim);
        _needlePen.Brush = new SolidColorBrush(text);
        _needleGlowPen.Brush = new SolidColorBrush(accent) { Opacity = 0.35 };
        _arcPen.Brush = new SolidColorBrush(muted);
        _arcGlowPen.Brush = new SolidColorBrush(accent) { Opacity = 0.12 };
        _labelBrush.Color = panelText;
        _digitalBrush.Color = accent;
        _restingBrush.Color = panelText;

        // Arc geometry: 210 degrees total, opening at the bottom.
        const double startAngle = 195.0;
        const double sweep = -210.0;
        const double tickInsetRatio = 0.78;
        const double labelInsetRatio = 0.62;

        var padding = 8.0;
        var size = Math.Min(width, height * 1.9) - padding * 2;
        var radius = size / 2.0;
        var centre = new Point(width / 2.0, height - padding - radius * 0.25);

        var arcRect = new Rect(
            centre.X - radius,
            centre.Y - radius,
            radius * 2,
            radius * 2);

        // Outer glow ring.
        context.DrawArc(_arcGlowPen, arcRect, startAngle, sweep);
        // Scale arc.
        context.DrawArc(_arcPen, arcRect, startAngle, sweep);

        var ticks = Ticks ?? (IReadOnlyList<ArcMeterTick>)[];
        foreach (var tick in ticks)
        {
            var t = Map(tick.Value);
            var a = Angle(startAngle, sweep, t);
            var rad = a * Math.PI / 180.0;
            var c = Math.Cos(rad);
            var s = Math.Sin(rad);

            var outer = new Point(centre.X + c * radius, centre.Y - s * radius);
            var inner = new Point(centre.X + c * radius * tickInsetRatio, centre.Y - s * radius * tickInsetRatio);
            context.DrawLine(_tickPen, outer, inner);

            if (!string.IsNullOrEmpty(tick.Label))
            {
                var labelPos = new Point(
                    centre.X + c * radius * labelInsetRatio,
                    centre.Y - s * radius * labelInsetRatio);
                var textLayout = CreateText(tick.Label, 11, _labelTypeface, TextAlignment.Center);
                var offset = new Point(labelPos.X - textLayout.Width / 2, labelPos.Y - textLayout.Height / 2);
                textLayout.Draw(context, offset);
            }
        }

        // Needle.
        var needleT = Map(Value);
        var needleAngle = Angle(startAngle, sweep, needleT);
        var needleRad = needleAngle * Math.PI / 180.0;
        var needleLen = radius * 0.72;
        var needleTip = new Point(
            centre.X + Math.Cos(needleRad) * needleLen,
            centre.Y - Math.Sin(needleRad) * needleLen);

        context.DrawLine(_needleGlowPen, centre, needleTip);
        context.DrawLine(_needlePen, centre, needleTip);

        // Cap.
        var capBrush = new SolidColorBrush(accent);
        var capGlow = new SolidColorBrush(accent) { Opacity = 0.25 };
        context.DrawEllipse(capGlow, null, centre, 5, 5);
        context.DrawEllipse(capBrush, null, centre, 2.5, 2.5);

        // Title.
        if (!string.IsNullOrEmpty(Title))
        {
            var titleLayout = CreateText(Title, 11, _labelTypeface, TextAlignment.Center);
            titleLayout.Draw(context, new Point((width - titleLayout.Width) / 2, 0));
        }

        // Digital readout / resting caption.
        var readoutText = Resting ? "not connected" : DigitalReadout;
        var readoutBrush = Resting ? _restingBrush : _digitalBrush;
        var readoutLayout = CreateText(readoutText, 13, _digitalTypeface, TextAlignment.Center);
        var readoutY = centre.Y + radius * 0.15;
        readoutLayout.Draw(context, new Point((width - readoutLayout.Width) / 2, readoutY));
    }

    private double Map(double value)
    {
        var min = Minimum;
        var max = Maximum;
        if (max <= min)
        {
            return 0;
        }

        return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
    }

    private static double Angle(double start, double sweep, double t)
        => start + sweep * t;

    private FormattedText CreateText(string text, double size, Typeface typeface, TextAlignment alignment)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brushes.Transparent)
        {
            TextAlignment = alignment,
        };
    }
}

public sealed class ArcMeterTick
{
    public double Value { get; set; }
    public string? Label { get; set; }

    public ArcMeterTick(double value, string? label = null)
    {
        Value = value;
        Label = label;
    }
}
