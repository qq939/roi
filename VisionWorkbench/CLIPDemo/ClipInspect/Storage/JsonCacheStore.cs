using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipInspect.Storage;

public sealed class JsonCacheStore : ICacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public async ValueTask<ClipCache> LoadAsync(string cachePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(cachePath);
        var raw = await JsonSerializer.DeserializeAsync<RawCache>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Could not read cache: {cachePath}");

        var okItems = raw.OkItems ?? raw.Items ?? [];
        return new ClipCache
        {
            ProductId = raw.ProductId ?? Path.GetFileNameWithoutExtension(cachePath),
            ModelName = raw.ModelName ?? "",
            Pretrained = raw.Pretrained ?? "",
            FeatureDim = raw.FeatureDim,
            TopK = raw.TopK <= 0 ? 3 : raw.TopK,
            Threshold = raw.Threshold,
            TextWeight = Math.Clamp(raw.TextWeight ?? 0.2f, 0, 1),
            OkItems = okItems.Where(IsValidImageItem).ToArray(),
            NgItems = (raw.NgItems ?? []).Where(IsValidImageItem).ToArray(),
            OkTextItems = (raw.OkTextItems ?? []).Where(IsValidTextItem).ToArray(),
            NgTextItems = (raw.NgTextItems ?? []).Where(IsValidTextItem).ToArray()
        };
    }

    public async ValueTask SaveAsync(string cachePath, ClipCache cache, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var raw = new RawCache
        {
            ProductId = cache.ProductId,
            ModelName = cache.ModelName,
            Pretrained = cache.Pretrained,
            FeatureDim = cache.FeatureDim,
            TopK = cache.TopK,
            Threshold = cache.Threshold,
            TextWeight = cache.TextWeight,
            Items = cache.OkItems.ToArray(),
            NgItems = cache.NgItems.ToArray(),
            OkTextItems = cache.OkTextItems.ToArray(),
            NgTextItems = cache.NgTextItems.ToArray()
        };

        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, raw, JsonOptions, cancellationToken);
    }

    private static bool IsValidImageItem(ImageCacheItem item)
    {
        return !string.IsNullOrWhiteSpace(item.ImagePath) && item.Feature.Length > 0;
    }

    private static bool IsValidTextItem(TextCacheItem item)
    {
        return !string.IsNullOrWhiteSpace(item.Prompt) && item.Feature.Length > 0;
    }

    private sealed class RawCache
    {
        public string? ProductId { get; init; }
        public string? ModelName { get; init; }
        public string? Pretrained { get; init; }
        public int FeatureDim { get; init; }
        public int TopK { get; init; }
        public float Threshold { get; init; }
        public float? TextWeight { get; init; }
        public ImageCacheItem[]? Items { get; init; }
        public ImageCacheItem[]? OkItems { get; init; }
        public ImageCacheItem[]? NgItems { get; init; }
        public TextCacheItem[]? OkTextItems { get; init; }
        public TextCacheItem[]? NgTextItems { get; init; }
    }
}
