using System.Windows;
using System.Windows.Media;
using ImageBox;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public enum RoiOverlayJudgment
{
    Default,
    OK,
    NG
}

public static class RoiOverlayVisualFactory
{
    private static readonly Brush DefaultStroke = Frozen("#FFFF00");
    private static readonly Brush DirectionStroke = Frozen("#FF38BDF8");

    public static ImageOverlayItem CreateRoiOverlay(
        RoiRegion roi,
        InspectionWorkspaceConfiguration configuration,
        string taskName,
        RoiOverlayJudgment judgment,
        double strokeThickness)
    {
        configuration.RoiOverlay ??= new RoiOverlaySettings();
        configuration.RoiOverlay.Normalize();
        var stroke = judgment switch
        {
            RoiOverlayJudgment.OK => UiBrushes.Success,
            RoiOverlayJudgment.NG => UiBrushes.Danger,
            _ => DefaultStroke
        };
        var overlay = InspectionRoiGeometry.ToOverlayItem(
            roi,
            stroke,
            CreateFill(stroke, configuration.RoiOverlay.FillOpacity),
            strokeThickness);
        ApplyLabel(overlay, configuration.RoiOverlay, taskName);
        if (judgment != RoiOverlayJudgment.Default)
        {
            overlay.LabelBackground = stroke;
        }

        return overlay;
    }

    public static IReadOnlyList<ImageOverlayItem> CreateMeasurementOverlays(
        RoiRegion roi,
        double? firstEdgeIndex,
        double? secondEdgeIndex,
        Brush stroke,
        string idPrefix,
        bool includeDirectionArrow)
    {
        var overlays = new List<ImageOverlayItem>();
        if (includeDirectionArrow)
        {
            overlays.AddRange(CreateMeasurementDirectionArrow(roi, $"{idPrefix}-direction"));
        }

        if (firstEdgeIndex is { } firstEdge)
        {
            overlays.Add(CreateMeasurementLine(roi, firstEdge, stroke, $"{idPrefix}-edge1"));
            overlays.Add(CreateMeasurementText(roi, firstEdge, "E1", stroke, $"{idPrefix}-edge1-text"));
        }

        if (secondEdgeIndex is { } secondEdge)
        {
            overlays.Add(CreateMeasurementLine(roi, secondEdge, stroke, $"{idPrefix}-edge2"));
            overlays.Add(CreateMeasurementText(roi, secondEdge, "E2", stroke, $"{idPrefix}-edge2-text"));
        }

        return overlays;
    }

    public static Brush CreateLabelBackground(RoiOverlaySettings settings)
    {
        settings.Normalize();
        return Frozen(settings.LabelBackgroundColor);
    }

    private static void ApplyLabel(ImageOverlayItem overlay, RoiOverlaySettings settings, string taskName)
    {
        overlay.Text = taskName;
        overlay.LabelBackground = CreateLabelBackground(settings);
        overlay.LabelForeground = Brushes.White;
        overlay.LabelFontSize = settings.LabelFontSize;
    }

    private static IEnumerable<ImageOverlayItem> CreateMeasurementDirectionArrow(RoiRegion roi, string idPrefix)
    {
        if (roi.Width < 8 || roi.Height < 4)
        {
            return [];
        }

        var margin = Math.Min(roi.Width * 0.12, 18);
        var startX = -roi.Width / 2.0 + margin;
        var endX = roi.Width / 2.0 - margin;
        if (endX - startX < 6)
        {
            startX = -roi.Width / 2.0;
            endX = roi.Width / 2.0;
        }

        var headLength = Math.Clamp(Math.Min(roi.Width * 0.12, roi.Height * 0.32), 5, 18);
        var headHalfHeight = Math.Clamp(headLength * 0.45, 3, Math.Max(3, roi.Height * 0.35));
        var tip = TransformRoiLocalPoint(roi, endX, 0);
        var shaftStart = TransformRoiLocalPoint(roi, startX, 0);
        var headA = TransformRoiLocalPoint(roi, endX - headLength, -headHalfHeight);
        var headB = TransformRoiLocalPoint(roi, endX - headLength, headHalfHeight);

        return
        [
            new ImageOverlayItem
            {
                Id = $"{idPrefix}-shaft",
                Kind = ImageOverlayKind.Line,
                Points = [shaftStart, tip],
                Stroke = DirectionStroke,
                StrokeThickness = 2.2
            },
            new ImageOverlayItem
            {
                Id = $"{idPrefix}-head-a",
                Kind = ImageOverlayKind.Line,
                Points = [headA, tip],
                Stroke = DirectionStroke,
                StrokeThickness = 2.2
            },
            new ImageOverlayItem
            {
                Id = $"{idPrefix}-head-b",
                Kind = ImageOverlayKind.Line,
                Points = [headB, tip],
                Stroke = DirectionStroke,
                StrokeThickness = 2.2
            }
        ];
    }

    private static ImageOverlayItem CreateMeasurementLine(
        RoiRegion roi,
        double edgeIndex,
        Brush stroke,
        string id)
    {
        var edge = Math.Clamp(edgeIndex, 0, Math.Max(1, roi.Width));
        var localX = edge - roi.Width / 2.0;
        var start = TransformRoiLocalPoint(roi, localX, -roi.Height / 2.0);
        var end = TransformRoiLocalPoint(roi, localX, roi.Height / 2.0);
        return new ImageOverlayItem
        {
            Id = id,
            Kind = ImageOverlayKind.Line,
            Points = [start, end],
            Stroke = stroke,
            StrokeThickness = 2.6
        };
    }

    private static ImageOverlayItem CreateMeasurementText(
        RoiRegion roi,
        double edgeIndex,
        string text,
        Brush foreground,
        string id)
    {
        var edge = Math.Clamp(edgeIndex, 0, Math.Max(1, roi.Width));
        var point = TransformRoiLocalPoint(roi, edge - roi.Width / 2.0, -roi.Height / 2.0);
        return new ImageOverlayItem
        {
            Id = id,
            Kind = ImageOverlayKind.Text,
            X = point.X + 4,
            Y = point.Y - 18,
            Text = text,
            Foreground = foreground,
            FontSize = 14
        };
    }

    private static Point TransformRoiLocalPoint(RoiRegion roi, double localX, double localY)
    {
        var radians = roi.AngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point(
            roi.X + localX * cos - localY * sin,
            roi.Y + localX * sin + localY * cos);
    }

    public static Brush CreateFill(Brush stroke, double opacity)
    {
        if (opacity <= 0)
        {
            return Brushes.Transparent;
        }

        if (stroke is SolidColorBrush solid)
        {
            var brush = new SolidColorBrush(solid.Color)
            {
                Opacity = Math.Clamp(opacity, 0, 1)
            };
            brush.Freeze();
            return brush;
        }

        var clone = stroke.Clone();
        clone.Opacity = Math.Clamp(opacity, 0, 1);
        clone.Freeze();
        return clone;
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
