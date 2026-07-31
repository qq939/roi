using System.Windows.Media;

namespace VisionWorkbench.Models;

public sealed class InspectionResultRow
{
    public required string CameraName { get; init; }

    public required string Item { get; init; }

    public required string Value { get; init; }

    public string DetailText { get; init; } = string.Empty;

    public required string Result { get; init; }

    public Brush ResultBrush => Result == "OK" ? UiBrushes.Success : UiBrushes.Danger;
}
