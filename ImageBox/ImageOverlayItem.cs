using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ImageBox;

public sealed class ImageOverlayItem
{
    public string? Id { get; set; }

    public ImageOverlayKind Kind { get; set; } = ImageOverlayKind.Rectangle;

    public string? Text { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public double Radius { get; set; }

    public double Angle { get; set; }

    public IReadOnlyList<Point> Points { get; set; } = [];

    public Brush Stroke { get; set; } = Brushes.LimeGreen;

    public Brush? Fill { get; set; }

    public double StrokeThickness { get; set; } = 2;

    public double FontSize { get; set; } = 16;

    public Brush Foreground { get; set; } = Brushes.LimeGreen;

    public bool IsVisible { get; set; } = true;

    public bool IsSelected { get; set; }

    public bool IsEditable { get; set; }
}
