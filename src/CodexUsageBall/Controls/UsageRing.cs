using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace CodexUsageBall.Controls;

public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush),
        typeof(MediaBrush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(MediaBrushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(MediaBrush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(MediaBrushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public MediaBrush RingBrush
    {
        get => (MediaBrush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public MediaBrush TrackBrush
    {
        get => (MediaBrush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var thickness = Math.Max(1d, StrokeThickness);
        var radius = Math.Max(0d, Math.Min(ActualWidth, ActualHeight) / 2d - thickness / 2d);
        var center = new WpfPoint(ActualWidth / 2d, ActualHeight / 2d);
        var trackPen = new MediaPen(TrackBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var ringPen = new MediaPen(RingBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var progress = Math.Clamp(Progress, 0d, 100d);
        if (progress <= 0.01d)
        {
            return;
        }

        if (progress >= 99.99d)
        {
            drawingContext.DrawEllipse(null, ringPen, center, radius, radius);
            return;
        }

        var startAngle = -90d;
        var endAngle = startAngle + 360d * progress / 100d;
        var startPoint = PointOnCircle(center, radius, startAngle);
        var endPoint = PointOnCircle(center, radius, endAngle);

        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment(
            endPoint,
            new WpfSize(radius, radius),
            0d,
            progress > 50d,
            SweepDirection.Clockwise,
            true));

        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        drawingContext.DrawGeometry(null, ringPen, geometry);
    }

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new WpfPoint(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
