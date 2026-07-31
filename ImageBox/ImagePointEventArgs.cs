using System.Windows;

namespace ImageBox;

public sealed class ImagePointEventArgs(Point imagePoint) : EventArgs
{
    public Point ImagePoint { get; } = imagePoint;
}
