using ClipInspect.Matching;

namespace ClipInspect.Core;

public sealed class InspectResult
{
    public required string ProductId { get; init; }
    public string? QueryImagePath { get; init; }
    public required InspectionLabel Label { get; init; }
    public required float ImageOkScore { get; init; }
    public float? ImageNgScore { get; init; }
    public float? TextOkScore { get; init; }
    public float? TextNgScore { get; init; }
    public float? ImageMargin { get; init; }
    public float? TextMargin { get; init; }
    public float? Margin { get; init; }
    public required float Threshold { get; init; }
    public required int TopK { get; init; }
    public required int FeatureDim { get; init; }
    public required int OkCacheItems { get; init; }
    public required int NgCacheItems { get; init; }
    public required int OkTextItems { get; init; }
    public required int NgTextItems { get; init; }
    public required float TextWeight { get; init; }
    public required TimingInfo Timing { get; init; }
    public required IReadOnlyList<VectorMatch> TopOk { get; init; }
    public required IReadOnlyList<VectorMatch> TopNg { get; init; }
    public required IReadOnlyList<VectorMatch> TopTextOk { get; init; }
    public required IReadOnlyList<VectorMatch> TopTextNg { get; init; }
}

public sealed class TimingInfo
{
    public required double InferenceMs { get; init; }
    public required double MatchMs { get; init; }
    public double TotalMs => InferenceMs + MatchMs;
}
