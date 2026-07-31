using System.Diagnostics;
using ClipInspect.Matching;
using ClipInspect.Storage;
using ClipInspect.Storage.Sqlite;

namespace ClipInspect.Core;

public sealed class ClipInspectionEngine
{
    private readonly ICacheStore _cacheStore;
    private readonly IImageFeatureEncoder? _imageEncoder;

    public ClipInspectionEngine(ICacheStore? cacheStore = null, IImageFeatureEncoder? imageEncoder = null)
    {
        _cacheStore = cacheStore ?? new JsonCacheStore();
        _imageEncoder = imageEncoder;
    }

    public async ValueTask<InspectResult> InspectImageAsync(
        InspectImageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_imageEncoder is null)
        {
            throw new InvalidOperationException("No image encoder is configured. Use InspectFeatureAsync with a 512-d feature, or provide an IImageFeatureEncoder implementation.");
        }

        var sw = Stopwatch.StartNew();
        var queryFeature = await _imageEncoder.EncodeImageAsync(request.ImagePath, cancellationToken);
        sw.Stop();

        return await InspectFeatureAsync(
            new InspectFeatureRequest
            {
                CachePath = request.CachePath,
                QueryFeature = queryFeature,
                QueryImagePath = request.ImagePath,
                TopK = request.TopK,
                Threshold = request.Threshold
            },
            sw.Elapsed.TotalMilliseconds,
            cancellationToken);
    }

    public ValueTask<InspectResult> InspectFeatureAsync(
        InspectFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        return InspectFeatureAsync(request, 0, cancellationToken);
    }

    public async ValueTask<InspectResult> InspectImageFromSqliteAsync(
        InspectSqliteImageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_imageEncoder is null)
        {
            throw new InvalidOperationException("No image encoder is configured. Use InspectFeatureFromSqliteAsync with a feature, or provide an IImageFeatureEncoder implementation.");
        }

        var sw = Stopwatch.StartNew();
        var queryFeature = await _imageEncoder.EncodeImageAsync(request.ImagePath, cancellationToken);
        sw.Stop();

        return await InspectFeatureFromSqliteAsync(
            new InspectSqliteFeatureRequest
            {
                DatabasePath = request.DatabasePath,
                ProductId = request.ProductId,
                QueryFeature = queryFeature,
                QueryImagePath = request.ImagePath,
                TopK = request.TopK,
                Threshold = request.Threshold
            },
            sw.Elapsed.TotalMilliseconds,
            cancellationToken);
    }

    public ValueTask<InspectResult> InspectFeatureFromSqliteAsync(
        InspectSqliteFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        return InspectFeatureFromSqliteAsync(request, 0, cancellationToken);
    }

    public async ValueTask<BuildCacheResult> BuildImageCacheAsync(
        BuildCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_imageEncoder is null)
        {
            throw new InvalidOperationException("No image encoder is configured. Provide an IImageFeatureEncoder implementation.");
        }

        if (request.OkImagePaths.Count == 0)
        {
            throw new InvalidOperationException("At least one OK image is required.");
        }

        var sw = Stopwatch.StartNew();
        var okItems = new List<ImageCacheItem>(request.OkImagePaths.Count);
        foreach (var imagePath in request.OkImagePaths)
        {
            var feature = await _imageEncoder.EncodeImageAsync(imagePath, cancellationToken);
            okItems.Add(new ImageCacheItem
            {
                ImagePath = Path.GetFullPath(imagePath),
                Feature = feature
            });
        }

        var ngItems = new List<ImageCacheItem>(request.NgImagePaths.Count);
        foreach (var imagePath in request.NgImagePaths)
        {
            var feature = await _imageEncoder.EncodeImageAsync(imagePath, cancellationToken);
            ngItems.Add(new ImageCacheItem
            {
                ImagePath = Path.GetFullPath(imagePath),
                Feature = feature
            });
        }

        sw.Stop();

        var featureDim = okItems[0].Feature.Length;
        var cache = new ClipCache
        {
            ProductId = request.ProductId,
            ModelName = request.ModelName,
            Pretrained = request.Pretrained,
            FeatureDim = featureDim,
            TopK = Math.Max(1, request.TopK),
            Threshold = request.Threshold,
            TextWeight = 0,
            OkItems = okItems,
            NgItems = ngItems
        };

        await _cacheStore.SaveAsync(request.OutputCachePath, cache, cancellationToken);

        return new BuildCacheResult
        {
            CachePath = request.OutputCachePath,
            ProductId = request.ProductId,
            FeatureDim = featureDim,
            OkCount = okItems.Count,
            NgCount = ngItems.Count,
            EncodeMs = sw.Elapsed.TotalMilliseconds
        };
    }

    public async ValueTask<AppendImageSamplesResult> AppendImageSamplesAsync(
        AppendImageSamplesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_imageEncoder is null)
        {
            throw new InvalidOperationException("No image encoder is configured. Provide an IImageFeatureEncoder implementation.");
        }

        if (request.Label is not InspectionLabel.OK and not InspectionLabel.NG)
        {
            throw new InvalidOperationException("Only OK and NG image samples can be appended.");
        }

        if (request.ImagePaths.Count == 0)
        {
            throw new InvalidOperationException("No image paths were provided.");
        }

        var cache = await _cacheStore.LoadAsync(request.CachePath, cancellationToken);
        var okItems = cache.OkItems.ToList();
        var ngItems = cache.NgItems.ToList();
        var existingPaths = okItems.Concat(ngItems)
            .Select(item => Path.GetFullPath(item.ImagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sw = Stopwatch.StartNew();
        var added = 0;
        foreach (var imagePath in request.ImagePaths)
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!existingPaths.Add(fullPath))
            {
                continue;
            }

            var feature = await _imageEncoder.EncodeImageAsync(fullPath, cancellationToken);
            var item = new ImageCacheItem
            {
                ImagePath = fullPath,
                Feature = feature
            };

            if (request.Label == InspectionLabel.OK)
            {
                okItems.Add(item);
            }
            else
            {
                ngItems.Add(item);
            }

            added++;
        }

        sw.Stop();

        var updated = CopyCache(cache, okItems, ngItems);
        await _cacheStore.SaveAsync(request.CachePath, updated, cancellationToken);

        return new AppendImageSamplesResult
        {
            CachePath = request.CachePath,
            Label = request.Label,
            AddedCount = added,
            OkCount = okItems.Count,
            NgCount = ngItems.Count,
            EncodeMs = sw.Elapsed.TotalMilliseconds
        };
    }

    public async ValueTask<DeleteImageSampleResult> DeleteImageSampleAsync(
        DeleteImageSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Label is not InspectionLabel.OK and not InspectionLabel.NG)
        {
            throw new InvalidOperationException("Only OK and NG image samples can be deleted.");
        }

        var cache = await _cacheStore.LoadAsync(request.CachePath, cancellationToken);
        var targetPath = Path.GetFullPath(request.ImagePath);
        var deleted = false;

        var okItems = cache.OkItems.ToList();
        var ngItems = cache.NgItems.ToList();
        if (request.Label == InspectionLabel.OK)
        {
            deleted = RemoveByPath(okItems, targetPath);
        }
        else
        {
            deleted = RemoveByPath(ngItems, targetPath);
        }

        if (okItems.Count == 0)
        {
            throw new InvalidOperationException("Cache must keep at least one OK image sample.");
        }

        if (deleted)
        {
            var updated = CopyCache(cache, okItems, ngItems);
            await _cacheStore.SaveAsync(request.CachePath, updated, cancellationToken);
        }

        return new DeleteImageSampleResult
        {
            CachePath = request.CachePath,
            Label = request.Label,
            Deleted = deleted,
            OkCount = okItems.Count,
            NgCount = ngItems.Count
        };
    }

    private async ValueTask<InspectResult> InspectFeatureAsync(
        InspectFeatureRequest request,
        double inferenceMs,
        CancellationToken cancellationToken)
    {
        var cache = await _cacheStore.LoadAsync(request.CachePath, cancellationToken);
        if (cache.OkItems.Count == 0)
        {
            throw new InvalidOperationException("Cache has no OK image items.");
        }

        var query = VectorMath.NormalizeCopy(request.QueryFeature);
        if (query.Length != cache.FeatureDim)
        {
            throw new InvalidOperationException($"Query feature dim is {query.Length}, but cache dim is {cache.FeatureDim}.");
        }

        var topK = Math.Clamp(request.TopK ?? cache.TopK, 1, cache.OkItems.Count);
        var threshold = request.Threshold ?? cache.Threshold;

        var matchSw = Stopwatch.StartNew();
        var (imageOkScore, topOk) = TopKMatcher.ScoreImages(query, cache.OkItems, topK);

        float? imageNgScore = null;
        IReadOnlyList<VectorMatch> topNg = Array.Empty<VectorMatch>();
        if (cache.NgItems.Count > 0)
        {
            var ngTopK = Math.Min(topK, cache.NgItems.Count);
            (imageNgScore, topNg) = TopKMatcher.ScoreImages(query, cache.NgItems, ngTopK);
        }

        float? textOkScore = null;
        IReadOnlyList<VectorMatch> topTextOk = Array.Empty<VectorMatch>();
        if (cache.OkTextItems.Count > 0)
        {
            var textTopK = Math.Min(topK, cache.OkTextItems.Count);
            (textOkScore, topTextOk) = TopKMatcher.ScoreTexts(query, cache.OkTextItems, textTopK);
        }

        float? textNgScore = null;
        IReadOnlyList<VectorMatch> topTextNg = Array.Empty<VectorMatch>();
        if (cache.NgTextItems.Count > 0)
        {
            var textTopK = Math.Min(topK, cache.NgTextItems.Count);
            (textNgScore, topTextNg) = TopKMatcher.ScoreTexts(query, cache.NgTextItems, textTopK);
        }

        float? imageMargin = imageNgScore is null ? null : imageOkScore - imageNgScore.Value;
        float? textMargin = textOkScore is null || textNgScore is null ? null : textOkScore.Value - textNgScore.Value;
        var margin = BlendMargins(imageMargin, textMargin, cache.TextWeight);
        var label = margin is null
            ? imageOkScore >= threshold ? InspectionLabel.OK : InspectionLabel.NG
            : imageOkScore >= threshold && margin.Value >= 0 ? InspectionLabel.OK : InspectionLabel.NG;

        matchSw.Stop();

        return new InspectResult
        {
            ProductId = cache.ProductId,
            QueryImagePath = request.QueryImagePath,
            Label = label,
            ImageOkScore = imageOkScore,
            ImageNgScore = imageNgScore,
            TextOkScore = textOkScore,
            TextNgScore = textNgScore,
            ImageMargin = imageMargin,
            TextMargin = textMargin,
            Margin = margin,
            Threshold = threshold,
            TopK = topK,
            FeatureDim = cache.FeatureDim,
            OkCacheItems = cache.OkItems.Count,
            NgCacheItems = cache.NgItems.Count,
            OkTextItems = cache.OkTextItems.Count,
            NgTextItems = cache.NgTextItems.Count,
            TextWeight = cache.TextWeight,
            Timing = new TimingInfo
            {
                InferenceMs = inferenceMs,
                MatchMs = matchSw.Elapsed.TotalMilliseconds
            },
            TopOk = topOk,
            TopNg = topNg,
            TopTextOk = topTextOk,
            TopTextNg = topTextNg
        };
    }

    private async ValueTask<InspectResult> InspectFeatureFromSqliteAsync(
        InspectSqliteFeatureRequest request,
        double inferenceMs,
        CancellationToken cancellationToken)
    {
        var store = new SqliteVectorStore(request.DatabasePath);
        var product = await store.GetProductAsync(request.ProductId, cancellationToken)
            ?? throw new InvalidOperationException($"Product not found in SQLite vector store: {request.ProductId}");

        var query = VectorMath.NormalizeCopy(request.QueryFeature);
        if (query.Length != product.FeatureDim)
        {
            throw new InvalidOperationException($"Query feature dim is {query.Length}, but SQLite product dim is {product.FeatureDim}.");
        }

        var topK = Math.Max(1, request.TopK ?? product.TopK);
        var threshold = request.Threshold ?? product.Threshold;

        var matchSw = Stopwatch.StartNew();
        var okMatches = await store.SearchAsync(product.ProductId, "OK", "Image", query, topK, cancellationToken);
        if (okMatches.Count == 0)
        {
            throw new InvalidOperationException("SQLite product has no OK image samples.");
        }

        var ngMatches = await store.SearchAsync(product.ProductId, "NG", "Image", query, topK, cancellationToken);
        var imageOkScore = Mean(okMatches);
        float? imageNgScore = ngMatches.Count == 0 ? null : Mean(ngMatches);
        float? imageMargin = imageNgScore is null ? null : imageOkScore - imageNgScore.Value;
        var label = imageMargin is null
            ? imageOkScore >= threshold ? InspectionLabel.OK : InspectionLabel.NG
            : imageOkScore >= threshold && imageMargin.Value >= 0 ? InspectionLabel.OK : InspectionLabel.NG;
        matchSw.Stop();

        return new InspectResult
        {
            ProductId = product.ProductId,
            QueryImagePath = request.QueryImagePath,
            Label = label,
            ImageOkScore = imageOkScore,
            ImageNgScore = imageNgScore,
            TextOkScore = null,
            TextNgScore = null,
            ImageMargin = imageMargin,
            TextMargin = null,
            Margin = imageMargin,
            Threshold = threshold,
            TopK = topK,
            FeatureDim = product.FeatureDim,
            OkCacheItems = okMatches.Count,
            NgCacheItems = ngMatches.Count,
            OkTextItems = 0,
            NgTextItems = 0,
            TextWeight = product.TextWeight,
            Timing = new TimingInfo
            {
                InferenceMs = inferenceMs,
                MatchMs = matchSw.Elapsed.TotalMilliseconds
            },
            TopOk = ToVectorMatches(okMatches),
            TopNg = ToVectorMatches(ngMatches),
            TopTextOk = Array.Empty<VectorMatch>(),
            TopTextNg = Array.Empty<VectorMatch>()
        };
    }

    private static float? BlendMargins(float? imageMargin, float? textMargin, float textWeight)
    {
        if (imageMargin is null && textMargin is null)
        {
            return null;
        }

        if (textMargin is null)
        {
            return imageMargin;
        }

        if (imageMargin is null)
        {
            return textMargin;
        }

        var clampedWeight = Math.Clamp(textWeight, 0, 1);
        return (1 - clampedWeight) * imageMargin.Value + clampedWeight * textMargin.Value;
    }

    private static ClipCache CopyCache(
        ClipCache cache,
        IReadOnlyList<ImageCacheItem> okItems,
        IReadOnlyList<ImageCacheItem> ngItems)
    {
        return new ClipCache
        {
            ProductId = cache.ProductId,
            ModelName = cache.ModelName,
            Pretrained = cache.Pretrained,
            FeatureDim = cache.FeatureDim,
            TopK = cache.TopK,
            Threshold = cache.Threshold,
            TextWeight = cache.TextWeight,
            OkItems = okItems.ToArray(),
            NgItems = ngItems.ToArray(),
            OkTextItems = cache.OkTextItems,
            NgTextItems = cache.NgTextItems
        };
    }

    private static bool RemoveByPath(List<ImageCacheItem> items, string targetPath)
    {
        var index = items.FindIndex(item =>
            string.Equals(Path.GetFullPath(item.ImagePath), targetPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        items.RemoveAt(index);
        return true;
    }

    private static float Mean(IReadOnlyList<SqliteVectorSearchResult> matches)
    {
        var sum = 0f;
        for (var i = 0; i < matches.Count; i++)
        {
            sum += matches[i].Similarity;
        }

        return sum / matches.Count;
    }

    private static IReadOnlyList<VectorMatch> ToVectorMatches(IReadOnlyList<SqliteVectorSearchResult> matches)
    {
        var result = new VectorMatch[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            result[i] = new VectorMatch
            {
                Rank = i + 1,
                Similarity = matches[i].Similarity,
                ImagePath = matches[i].Sample.ImagePath,
                Prompt = matches[i].Sample.Prompt
            };
        }

        return result;
    }
}
