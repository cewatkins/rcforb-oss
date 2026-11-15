using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RCForb.Gui;

/// <summary>
/// An oscilloscope-style trace of the most recent received audio samples.
/// Renders without per-frame allocation.
/// </summary>
public sealed class WaveformScope : Control
{
    public static readonly StyledProperty<WaveformTapSink?> SourceProperty =
        AvaloniaProperty.Register<WaveformScope, WaveformTapSink?>(nameof(Source));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<WaveformScope, string>(nameof(Label), "RECEIVED AUDIO");

    public static readonly StyledProperty<Color> TraceColorProperty =
        AvaloniaProperty.Register<WaveformScope, Color>(nameof(TraceColor), Color.Parse("#46b7ff"));

    public WaveformTapSink? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Color TraceColor
    {
        get => GetValue(TraceColorProperty);
        set => SetValue(TraceColorProperty, value);
    }

    private readonly short[] _samples = new short[WaveformTapSink.Capacity];
    private readonly Point[] _points = new Point[WaveformTapSink.Capacity];
    private readonly Pen _tracePen;
    private readonly Pen _glowPen;
    private readonly Pen _gridPen;
    private readonly SolidColorBrush _labelBrush;
    private readonly Typeface _labelTypeface;

    public WaveformScope()
    {
        _tracePen = new Pen(Brushes.Transparent, 1.5);
        _glowPen = new Pen(Brushes.Transparent, 5.0);
        _gridPen = new Pen(Brushes.Transparent, 1.0);
        _labelBrush = new SolidColorBrush();
        _labelTypeface = new Typeface("IBM Plex Mono, JetBrains Mono, Fira Code, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty ||
            change.Property == LabelProperty ||
            change.Property == TraceColorProperty)
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

        var trace = TraceColor;
        var dim = new Color(0x33, trace.R, trace.G, trace.B);
        var grid = Color.Parse("#16202a");
        var panelText = Color.Parse("#7f8f9f");

        _tracePen.Brush = new SolidColorBrush(trace);
        _glowPen.Brush = new SolidColorBrush(trace) { Opacity = 0.22 };
        _gridPen.Brush = new SolidColorBrush(grid);
        _labelBrush.Color = panelText;

        var padding = 6.0;
        var plotTop = padding + 14;
        var plotHeight = Math.Max(10, height - plotTop - padding);
        var midY = plotTop + plotHeight / 2.0;

        // Centre zero line.
        context.DrawLine(_gridPen, new Point(padding, midY), new Point(width - padding, midY));

        var source = Source;
        int count;
        if (source is null || (count = source.ReadRecent(_samples, _samples.Length)) < 2)
        {
            // Flat line.
            context.DrawLine(_tracePen, new Point(padding, midY), new Point(width - padding, midY));
            DrawLabel(context, width, padding, 0);
            return;
        }

        var stepX = (width - padding * 2) / (count - 1);
        var amplitudeScale = plotHeight / 2.0 / 32768.0;

        for (var i = 0; i < count; i++)
        {
            var x = padding + i * stepX;
            var y = midY - _samples[i] * amplitudeScale;
            _points[i] = new Point(x, y);
        }

        var polyline = new Polyline
        {
            Points = _points,
        };

        context.DrawGeometry(null, _glowPen, polyline.DefiningGeometry);
        context.DrawGeometry(null, _tracePen, polyline.DefiningGeometry);

        DrawLabel(context, width, padding, count);
    }

    private void DrawLabel(DrawingContext context, double width, double padding, int samples)
    {
        var spanText = samples > 0
            ? $"{samples / (double)WaveformTapSink.SampleRate * 1000.0:0} ms"
            : "—";
        var text = $"{Label}  {spanText}";
        var layout = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _labelTypeface,
            10,
            Brushes.Transparent)
        {
            TextAlignment = TextAlignment.Left,
        };
        layout.Draw(context, new Point(padding, 0));
    }
}
