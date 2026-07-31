namespace ClipInspect.Core;

public sealed class InspectImageRequest
{
    public required string CachePath { get; init; }
    public required string ImagePath { get; init; }
    public int? TopK { get; init; }
    public float? Threshold { get; init; }
}
