namespace ClipInspect.Core;

public sealed class AppendImageSamplesRequest
{
    public required string CachePath { get; init; }
    public required InspectionLabel Label { get; init; }
    public required IReadOnlyList<string> ImagePaths { get; init; }
}

public sealed class AppendImageSamplesResult
{
    public required string CachePath { get; init; }
    public required InspectionLabel Label { get; init; }
    public required int AddedCount { get; init; }
    public required int OkCount { get; init; }
    public required int NgCount { get; init; }
    public required double EncodeMs { get; init; }
}

public sealed class DeleteImageSampleRequest
{
    public required string CachePath { get; init; }
    public required InspectionLabel Label { get; init; }
    public required string ImagePath { get; init; }
}

public sealed class DeleteImageSampleResult
{
    public required string CachePath { get; init; }
    public required InspectionLabel Label { get; init; }
    public required bool Deleted { get; init; }
    public required int OkCount { get; init; }
    public required int NgCount { get; init; }
}
