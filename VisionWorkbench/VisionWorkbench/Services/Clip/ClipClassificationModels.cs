using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public sealed class ClipClassificationRequest
{
    public required ClipVectorSetDefinition VectorSet { get; init; }

    public required string ImagePath { get; init; }

    public int? TopK { get; init; }

    public float? Threshold { get; init; }
}

public sealed class ClipBuildVectorSetRequest
{
    public required ClipVectorSetDefinition VectorSet { get; init; }

    public required IReadOnlyList<string> OkImagePaths { get; init; }

    public IReadOnlyList<string> NgImagePaths { get; init; } = Array.Empty<string>();

    public string Source { get; init; } = "Build";
}

public sealed class ClipSampleMaintenanceRequest
{
    public required ClipVectorSetDefinition VectorSet { get; init; }

    public required InspectionJudgment Label { get; init; }

    public required IReadOnlyList<string> ImagePaths { get; init; }

    public string Source { get; init; } = "VisionWorkbench";
}

public sealed class ClipClassificationResult
{
    public required string VectorSetId { get; init; }

    public required string ImagePath { get; init; }

    public required InspectionJudgment Judgment { get; init; }

    public required float OkScore { get; init; }

    public float? NgScore { get; init; }

    public float? Margin { get; init; }

    public required float Threshold { get; init; }

    public required int TopK { get; init; }

    public required double InferenceMs { get; init; }

    public required double MatchMs { get; init; }

    public double TotalMs => InferenceMs + MatchMs;

    public required IReadOnlyList<ClipMatchResult> TopOk { get; init; }

    public required IReadOnlyList<ClipMatchResult> TopNg { get; init; }

    public int OkSampleCount { get; init; }

    public int NgSampleCount { get; init; }
}

public sealed class ClipMatchResult
{
    public required int Rank { get; init; }

    public required float Similarity { get; init; }

    public string? ImagePath { get; init; }
}

public sealed class ClipVectorSetBuildResult
{
    public required string VectorSetId { get; init; }

    public required int OkCount { get; init; }

    public required int NgCount { get; init; }

    public required int FeatureDim { get; init; }
}
