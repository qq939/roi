using System.Windows;
using System.Windows.Media;
using ImageBox;
using RoiAlignment.Core;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public static class InspectionRoiGeometry
{
    public static RoiRegion FromCornerPoints(IReadOnlyList<Point> points, RoiRegion? current = null)
    {
        if (points.Count < 4)
        {
            throw new ArgumentException("At least four points are required for a rotated ROI.", nameof(points));
        }

        var xywha = RoiAlignment.Core.RoiGeometry.ToXywha(
            points.Take(4)
                .Select(point => new Point2fDto(point.X, point.Y))
                .ToArray());

        return FromXywha(xywha, current);
    }

    public static RoiRegion FromXywha(Xywha xywha, RoiRegion? current = null)
    {
        return new RoiRegion
        {
            Id = current?.Id ?? Guid.NewGuid().ToString("N"),
            Name = current?.Name ?? "ROI",
            X = xywha.X,
            Y = xywha.Y,
            Width = Math.Max(0, xywha.Width),
            Height = Math.Max(0, xywha.Height),
            AngleDegrees = NormalizeAngle(xywha.AngleDegrees)
        };
    }

    public static Xywha ToXywha(RoiRegion roi)
    {
        return new Xywha(
            roi.X,
            roi.Y,
            Math.Max(0, roi.Width),
            Math.Max(0, roi.Height),
            NormalizeAngle(roi.AngleDegrees));
    }

    public static ImageOverlayItem ToOverlayItem(
        RoiRegion roi,
        Brush stroke,
        Brush? fill,
        double strokeThickness)
    {
        var xywha = ToXywha(roi);
        var left = xywha.X - xywha.Width / 2.0;
        var top = xywha.Y - xywha.Height / 2.0;

        return new ImageOverlayItem
        {
            Kind = Math.Abs(roi.AngleDegrees) > 0.001
                ? ImageOverlayKind.RotatedRectangle
                : ImageOverlayKind.Rectangle,
            X = left,
            Y = top,
            Width = xywha.Width,
            Height = xywha.Height,
            Angle = xywha.AngleDegrees,
            Stroke = stroke,
            Fill = fill,
            StrokeThickness = strokeThickness
        };
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -180)
        {
            angle += 360;
        }

        while (angle > 180)
        {
            angle -= 360;
        }

        return angle;
    }
}
