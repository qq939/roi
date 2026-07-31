using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class InspectionTaskExecutionResult
{
    public required string TaskId { get; init; }

    public required string TaskName { get; init; }

    public required InspectionTaskKind Kind { get; init; }

    public required InspectionJudgment Judgment { get; init; }

    public float? Score { get; init; }

    public float? Threshold { get; init; }

    public double ElapsedMs { get; init; }

    public string? Detail { get; init; }

    public string? VectorSetId { get; init; }

    public string? ImagePath { get; init; }

    public float? OkScore { get; init; }

    public float? NgScore { get; init; }

    public float? Margin { get; init; }

    public int? TopK { get; init; }

    public int? OkSampleCount { get; init; }

    public int? NgSampleCount { get; init; }

    public string? TopOkImagePath { get; init; }

    public float? TopOkSimilarity { get; init; }

    public string? TopNgImagePath { get; init; }

    public float? TopNgSimilarity { get; init; }

    public double? DistancePx { get; init; }

    public double? DistanceMm { get; init; }

    public double? FirstEdgeIndex { get; init; }

    public double? SecondEdgeIndex { get; init; }

    public string? FailureReason { get; init; }
}
