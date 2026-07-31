namespace ImageBox;

public sealed class OverlayItemEditCompletedEventArgs(
    string id,
    double x,
    double y,
    double width,
    double height,
    double angleDegrees) : EventArgs
{
    public string Id { get; } = id;

    public double X { get; } = x;

    public double Y { get; } = y;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public double AngleDegrees { get; } = angleDegrees;
}
