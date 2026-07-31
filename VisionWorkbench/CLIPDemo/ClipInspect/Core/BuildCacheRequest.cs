namespace ClipInspect.Core;

public sealed class BuildCacheRequest
{
    public required string ProductId { get; init; }
    public required string OutputCachePath { get; init; }
    public required IReadOnlyList<string> OkImagePaths { get; init; }
    public IReadOnlyList<string> NgImagePaths { get; init; } = Array.Empty<string>();
    public int TopK { get; init; } = 3;
    public float Threshold { get; init; } = 0.94f;
    public string ModelName { get; init; } = "ViT-B-32";
    public string Pretrained { get; init; } = "laion2b_s34b_b79k";
}

public sealed class BuildCacheResult
{
    public required string CachePath { get; init; }
    public required string ProductId { get; init; }
    public required int FeatureDim { get; init; }
    public required int OkCount { get; init; }
    public required int NgCount { get; init; }
    public required double EncodeMs { get; init; }
}
