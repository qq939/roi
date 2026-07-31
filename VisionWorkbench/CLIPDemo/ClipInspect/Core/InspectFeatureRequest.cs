namespace ClipInspect.Core;

public sealed class InspectFeatureRequest
{
    public required string CachePath { get; init; }
    public required float[] QueryFeature { get; init; }
    public string? QueryImagePath { get; init; }
    public int? TopK { get; init; }
    public float? Threshold { get; init; }
}
