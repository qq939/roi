namespace VisionWorkbench.Models.Inspection;

public sealed class RoiRegion
{
    public string Id { get; set; } = "full";

    public string Name { get; set; } = "整图";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public double AngleDegrees { get; set; }

    public bool IsFullImage => Width <= 0 || Height <= 0;
}
