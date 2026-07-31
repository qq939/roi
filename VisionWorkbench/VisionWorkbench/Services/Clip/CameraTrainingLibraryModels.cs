using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public enum TrainingLabelState
{
    Unlabeled,
    OK,
    NG,
    Ignored
}

public sealed class TrainingTaskLabel
{
    public string TaskId { get; set; } = string.Empty;

    public TrainingLabelState State { get; set; } = TrainingLabelState.Unlabeled;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TrainingImageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ProductModelId { get; set; } = string.Empty;

    public string CameraId { get; set; } = string.Empty;

    public string RawImageRelativePath { get; set; } = string.Empty;

    public string AlignedImageRelativePath { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public bool IsProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool AlignmentSucceeded { get; set; } = true;

    public string AlignmentMessage { get; set; } = string.Empty;

    public List<TrainingTaskLabel> Labels { get; set; } = [];
}

public sealed class CameraTrainingLibraryDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string ProductModelId { get; set; } = string.Empty;

    public string CameraId { get; set; } = string.Empty;

    public List<TrainingImageRecord> Images { get; set; } = [];
}

public class CameraTrainingImageImportRequest
{
    public required ProductModelDefinition ProductModel { get; init; }

    public required string CameraId { get; init; }

    public required string CameraName { get; init; }

    public required IReadOnlyList<InspectionTaskDefinition> Tasks { get; init; }

    public required IReadOnlyList<CameraAlignmentDefinition> Alignments { get; init; }

    public required string SourceName { get; init; }
}

public sealed class CameraTrainingFileImportRequest : CameraTrainingImageImportRequest
{
    public required string SourceImagePath { get; init; }
}

public sealed class CameraTrainingMatImportRequest : CameraTrainingImageImportRequest
{
    public required OpenCvSharp.Mat SourceImage { get; init; }
}

public sealed class CameraTrainingImageImportResult
{
    public required TrainingImageRecord Record { get; init; }

    public required CameraTrainingLibraryDocument Document { get; init; }
}

public sealed class CameraTrainingVectorSyncRequest
{
    public required ProductModelDefinition ProductModel { get; init; }

    public required string CameraId { get; init; }

    public required IReadOnlyList<InspectionTaskDefinition> Tasks { get; init; }
}

public sealed class CameraTrainingVectorSyncResult
{
    public List<CameraTrainingTaskSyncResult> Tasks { get; } = [];

    public int BuiltCount => Tasks.Count(task => task.Built);

    public int SkippedCount => Tasks.Count(task => task.Skipped);

    public string SummaryText => Tasks.Count == 0
        ? "没有可同步的分类任务。"
        : string.Join(Environment.NewLine, Tasks.Select(task => task.Message));
}

public sealed class CameraTrainingTaskSyncResult
{
    public required string TaskId { get; init; }

    public required string TaskName { get; init; }

    public required string VectorSetId { get; init; }

    public int OkCount { get; init; }

    public int NgCount { get; init; }

    public int IgnoredCount { get; init; }

    public int UnlabeledCount { get; init; }

    public bool Built { get; init; }

    public bool Skipped { get; init; }

    public string Message { get; init; } = string.Empty;
}
