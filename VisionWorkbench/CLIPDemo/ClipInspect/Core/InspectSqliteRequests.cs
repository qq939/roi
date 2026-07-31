namespace ClipInspect.Core;

public sealed class InspectSqliteFeatureRequest
{
    public required string DatabasePath { get; init; }
    public required string ProductId { get; init; }
    public required float[] QueryFeature { get; init; }
    public string? QueryImagePath { get; init; }
    public int? TopK { get; init; }
    public float? Threshold { get; init; }
}

public sealed class InspectSqliteImageRequest
{
    public required string DatabasePath { get; init; }
    public required string ProductId { get; init; }
    public required string ImagePath { get; init; }
    public int? TopK { get; init; }
    public float? Threshold { get; init; }
}
