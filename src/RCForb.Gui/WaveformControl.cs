namespace RCForb.Gui;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

public sealed class WaveformControl : Avalonia.Controls.Control
{
    private const int MaxSamples = 1_000_000;

    public static readonly StyledProperty<short[]?> SamplesProperty =
        AvaloniaProperty.Register<WaveformControl, short[]?>(nameof(Samples));
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<WaveformControl, double>(nameof(StrokeThickness), 1.5);
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(FillBrush));
    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(BaselineBrush));

    public short[]? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty)!;
        set => SetValue(StrokeThicknessProperty, value);
    }
    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }
    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var centerY = bounds.Height / 2.0;

        // Baseline: thin horizontal line at the vertical center.
        var baselineBrush = BaselineBrush ?? new SolidColorBrush(Colors.Gray, 0.25);
        var baselinePen = new Pen(baselineBrush, 1.0);
        context.DrawLine(baselinePen, new Point(0, centerY), new Point(bounds.Width, centerY));

        var samples = Samples;
        if (samples is null || samples.Length < 2 || samples.Length > MaxSamples)
            return;
        int n = samples.Length;

        var points = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            double x = bounds.Width * i / (n - 1);
            double y = centerY - (samples[i] / 32768.0) * (centerY * 0.92);
            if (!double.IsFinite(y))
                continue;
            points.Add(new Point(x, y));
        }

        // Stroke polyline through the sample points.
        if (points.Count >= 2)
        {
            var polyLine = new PolyLineSegment(points);
            var figure = new PathFigure
            {
                StartPoint = points[0],
                Segments = new PathSegments { polyLine },
                IsClosed = false,
                IsFilled = false
            };
            var geometry = new PathGeometry { Figures = new PathFigures { figure } };
            var strokeBrush = Stroke ?? new SolidColorBrush(Color.FromRgb(240, 160, 48));
            var pen = new Pen(strokeBrush, StrokeThickness)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            context.DrawGeometry(null, pen, geometry);
        }

        // Optional subtle fill beneath the waveform.
        if (points.Count >= 2 && FillBrush is not null)
        {
            var fillBrush = FillBrush;
            if (fillBrush is ISolidColorBrush scb)
                fillBrush = new SolidColorBrush(scb.Color, 0.18);

            var baselinePt = new Point(bounds.Width, centerY);
            var leftPt = new Point(0, centerY);
            var fillPoints = new List<Point>(points) { baselinePt, leftPt };
            var fillPolyLine = new PolyLineSegment(fillPoints);
            var fillFigure = new PathFigure
            {
                StartPoint = points[0],
                Segments = new PathSegments { fillPolyLine },
                IsClosed = true,
                IsFilled = true
            };
            var fillGeometry = new PathGeometry { Figures = new PathFigures { fillFigure } };
            context.DrawGeometry(fillBrush, null, fillGeometry);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SamplesProperty ||
            change.Property == StrokeProperty ||
            change.Property == StrokeThicknessProperty ||
            change.Property == FillBrushProperty ||
            change.Property == BaselineBrushProperty)
        {
            InvalidateVisual();
        }
    }
}
