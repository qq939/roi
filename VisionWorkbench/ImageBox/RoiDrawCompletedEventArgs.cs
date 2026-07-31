using System.Windows;

namespace ImageBox;

public sealed class RoiDrawCompletedEventArgs(ImageBoxInteractionMode mode, IReadOnlyList<Point> points) : EventArgs
{
    public ImageBoxInteractionMode Mode { get; } = mode;

    public IReadOnlyList<Point> Points { get; } = points;
}
