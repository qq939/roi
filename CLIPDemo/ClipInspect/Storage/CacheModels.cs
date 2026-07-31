namespace ClipInspect.Storage;

public sealed class ClipCache
{
    public string ProductId { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string Pretrained { get; init; } = "";
    public int FeatureDim { get; init; }
    public int TopK { get; init; } = 3;
    public float Threshold { get; init; } = 0.9f;
    public float TextWeight { get; init; } = 0.2f;
    public IReadOnlyList<ImageCacheItem> OkItems { get; init; } = Array.Empty<ImageCacheItem>();
    public IReadOnlyList<ImageCacheItem> NgItems { get; init; } = Array.Empty<ImageCacheItem>();
    public IReadOnlyList<TextCacheItem> OkTextItems { get; init; } = Array.Empty<TextCacheItem>();
    public IReadOnlyList<TextCacheItem> NgTextItems { get; init; } = Array.Empty<TextCacheItem>();
}

public sealed class ImageCacheItem
{
    public string ImagePath { get; init; } = "";
    public float[] Feature { get; init; } = Array.Empty<float>();
}

public sealed class TextCacheItem
{
    public string Prompt { get; init; } = "";
    public float[] Feature { get; init; } = Array.Empty<float>();
}
