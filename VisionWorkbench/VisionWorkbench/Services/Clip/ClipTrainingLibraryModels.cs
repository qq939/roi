using System.IO;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public enum ClipSampleInputMode
{
    RoiImage,
    FullImageAlignedRoi
}

public sealed class ClipTrainingLibrarySummary
{
    public required string VectorSetId { get; init; }

    public required bool VectorSetExists { get; init; }

    public int FeatureDim { get; init; }

    public int TopK { get; init; }

    public float Threshold { get; init; }

    public IReadOnlyList<ClipTrainingSampleInfo> OkSamples { get; init; } = Array.Empty<ClipTrainingSampleInfo>();

    public IReadOnlyList<ClipTrainingSampleInfo> NgSamples { get; init; } = Array.Empty<ClipTrainingSampleInfo>();
}

public sealed class ClipTrainingSampleInfo
{
    public required long Id { get; init; }

    public required InspectionJudgment Label { get; init; }

    public bool Enabled { get; init; } = true;

    public required string ImagePath { get; init; }

    public required string Source { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public bool IsManagedFile { get; init; }

    public string FileName => Path.GetFileName(ImagePath);

    public string CreatedAtText => CreatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
}

public sealed class ClipTrainingAddSamplesRequest
{
    public required ProductModelDefinition ProductModel { get; init; }

    public required InspectionTaskDefinition Task { get; init; }

    public required IReadOnlyList<string> CameraIdCandidates { get; init; }

    public required IReadOnlyList<CameraAlignmentDefinition> Alignments { get; init; }

    public required InspectionJudgment Label { get; init; }

    public required ClipSampleInputMode InputMode { get; init; }

    public required IReadOnlyList<string> SourceImagePaths { get; init; }
}

public sealed class ClipTrainingAddSamplesResult
{
    public required int AddedCount { get; init; }

    public required IReadOnlyList<string> PreparedImagePaths { get; init; }

    public required ClipTrainingLibrarySummary Summary { get; init; }
}

public sealed class ClipTrainingTemplateSeedResult
{
    public required bool Added { get; init; }

    public string? PreparedImagePath { get; init; }

    public string? Message { get; init; }

    public required ClipTrainingLibrarySummary Summary { get; init; }
}

public sealed class ClipTrainingClassifyRequest
{
    public required ProductModelDefinition ProductModel { get; init; }

    public required InspectionTaskDefinition Task { get; init; }

    public required IReadOnlyList<string> CameraIdCandidates { get; init; }

    public required IReadOnlyList<CameraAlignmentDefinition> Alignments { get; init; }

    public required ClipSampleInputMode InputMode { get; init; }

    public required string SourceImagePath { get; init; }
}

public sealed class ClipTrainingClassifyResult
{
    public required string PreparedImagePath { get; init; }

    public required ClipClassificationResult Classification { get; init; }
}
