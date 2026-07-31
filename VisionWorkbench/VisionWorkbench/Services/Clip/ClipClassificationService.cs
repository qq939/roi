using System.IO;
using ClipInspect.Core;
using ClipInspect.Matching;
using ClipInspect.Onnx;
using ClipInspect.Storage.Sqlite;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public sealed class ClipClassificationService : IClipClassificationService
{
    private readonly ClipRuntimeOptions _options;
    private readonly SemaphoreSlim _encoderLock = new(1, 1);
    private OnnxClipImageEncoder? _encoder;

    public ClipClassificationService(ClipRuntimeOptions? options = null)
    {
        _options = options ?? new ClipRuntimeOptions();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureModelExists();
        var store = new SqliteVectorStore(_options.DatabasePath);
        await store.InitializeAsync(cancellationToken);
    }

    public async ValueTask<ClipVectorSetBuildResult> BuildVectorSetAsync(
        ClipBuildVectorSetRequest request,
        CancellationToken cancellationToken = default)
    {
        request.VectorSet.EnsureId();
        if (request.OkImagePaths.Count == 0)
        {
            throw new InvalidOperationException("At least one OK image is required to build a CLIP vector set.");
        }

        EnsureModelExists();
        var store = new SqliteVectorStore(_options.DatabasePath);
        var encoder = await GetEncoderAsync(cancellationToken);
        var okFeatures = await EncodeImagesAsync(encoder, request.OkImagePaths, cancellationToken);
        var ngFeatures = await EncodeImagesAsync(encoder, request.NgImagePaths, cancellationToken);
        var featureDim = okFeatures.Count > 0 ? okFeatures[0].Feature.Length : 512;

        await store.CreateOrUpdateProductAsync(new SqliteProductConfig
        {
            ProductId = request.VectorSet.VectorSetId,
            Name = request.VectorSet.DisplayName,
            ModelName = request.VectorSet.ModelName,
            Pretrained = request.VectorSet.Pretrained,
            FeatureDim = featureDim,
            TopK = request.VectorSet.TopK,
            Threshold = request.VectorSet.Threshold,
            TextWeight = 0
        }, cancellationToken);

        foreach (var item in okFeatures)
        {
            await store.AddImageSampleAsync(request.VectorSet.VectorSetId, "OK", item.ImagePath, item.Feature, request.Source, null, cancellationToken);
        }

        foreach (var item in ngFeatures)
        {
            await store.AddImageSampleAsync(request.VectorSet.VectorSetId, "NG", item.ImagePath, item.Feature, request.Source, null, cancellationToken);
        }

        return new ClipVectorSetBuildResult
        {
            VectorSetId = request.VectorSet.VectorSetId,
            OkCount = okFeatures.Count,
            NgCount = ngFeatures.Count,
            FeatureDim = featureDim
        };
    }

    public async ValueTask<int> AddSamplesAsync(
        ClipSampleMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        request.VectorSet.EnsureId();
        if (request.ImagePaths.Count == 0)
        {
            return 0;
        }

        EnsureModelExists();
        var store = new SqliteVectorStore(_options.DatabasePath);
        var product = await store.GetProductAsync(request.VectorSet.VectorSetId, cancellationToken)
            ?? throw new InvalidOperationException($"CLIP vector set does not exist: {request.VectorSet.VectorSetId}");

        var encoder = await GetEncoderAsync(cancellationToken);
        var encoded = await EncodeImagesAsync(encoder, request.ImagePaths, cancellationToken);
        foreach (var item in encoded)
        {
            if (item.Feature.Length != product.FeatureDim)
            {
                throw new InvalidOperationException(
                    $"Feature dim is {item.Feature.Length}, but vector set dim is {product.FeatureDim}.");
            }

            await store.AddImageSampleAsync(
                request.VectorSet.VectorSetId,
                request.Label.ToString(),
                item.ImagePath,
                item.Feature,
                request.Source,
                null,
                cancellationToken);
        }

        return encoded.Count;
    }

    public async ValueTask<ClipClassificationResult> ClassifyAsync(
        ClipClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        request.VectorSet.EnsureId();
        EnsureModelExists();
        if (!File.Exists(request.ImagePath))
        {
            throw new FileNotFoundException("CLIP classify image was not found.", request.ImagePath);
        }

        var store = new SqliteVectorStore(_options.DatabasePath);
        var product = await store.GetProductAsync(request.VectorSet.VectorSetId, cancellationToken);
        if (product == null)
        {
            throw new InvalidOperationException(
                $"模型训练库未创建：{request.VectorSet.VectorSetId}。请先添加OK样本，或在型号管理中创建模板参考图。");
        }

        // 调试日志：记录当前vector set和样本数量
        var allSamples = await store.ListSamplesAsync(request.VectorSet.VectorSetId, cancellationToken);
        var okSamples = allSamples.Where(s => s.Label == "OK" && s.Kind == "Image" && s.Enabled).Count();
        var ngSamples = allSamples.Where(s => s.Label == "NG" && s.Kind == "Image" && s.Enabled).Count();
        AppDiagnostics.Info("clip-classify", $"VectorSet={request.VectorSet.VectorSetId}, OK Samples={okSamples}, NG Samples={ngSamples}");

        var encoder = await GetEncoderAsync(cancellationToken);
        var engine = new ClipInspectionEngine(imageEncoder: encoder);
        var result = await engine.InspectImageFromSqliteAsync(new InspectSqliteImageRequest
        {
            DatabasePath = _options.DatabasePath,
            ProductId = request.VectorSet.VectorSetId,
            ImagePath = request.ImagePath,
            TopK = request.TopK ?? request.VectorSet.TopK,
            Threshold = request.Threshold ?? request.VectorSet.Threshold
        }, cancellationToken);

        // 调试日志：记录分类结果
        AppDiagnostics.Info("clip-classify", 
            $"Result: OkScore={result.ImageOkScore:F4}, NgScore={result.ImageNgScore?.ToString("F4") ?? "null"}, " +
            $"Margin={result.Margin?.ToString("F4") ?? "null"}, Judgment={result.Label}");

        var samples = await store.ListSamplesAsync(request.VectorSet.VectorSetId, cancellationToken);
        var imageSamples = samples.Where(sample => sample.Kind == "Image" && sample.Enabled).ToArray();

        return new ClipClassificationResult
        {
            VectorSetId = request.VectorSet.VectorSetId,
            ImagePath = request.ImagePath,
            Judgment = result.Label == InspectionLabel.OK ? InspectionJudgment.OK : InspectionJudgment.NG,
            OkScore = result.ImageOkScore,
            NgScore = result.ImageNgScore,
            Margin = result.Margin,
            Threshold = result.Threshold,
            TopK = result.TopK,
            InferenceMs = result.Timing.InferenceMs,
            MatchMs = result.Timing.MatchMs,
            TopOk = MapMatches(result.TopOk),
            TopNg = MapMatches(result.TopNg),
            OkSampleCount = imageSamples.Count(sample => string.Equals(sample.Label, "OK", StringComparison.OrdinalIgnoreCase)),
            NgSampleCount = imageSamples.Count(sample => string.Equals(sample.Label, "NG", StringComparison.OrdinalIgnoreCase))
        };
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _encoderLock.Dispose();
    }

    private async ValueTask<OnnxClipImageEncoder> GetEncoderAsync(CancellationToken cancellationToken)
    {
        if (_encoder != null)
        {
            return _encoder;
        }

        await _encoderLock.WaitAsync(cancellationToken);
        try
        {
            _encoder ??= new OnnxClipImageEncoder(_options.ModelPath);
            return _encoder;
        }
        finally
        {
            _encoderLock.Release();
        }
    }

    private void EnsureModelExists()
    {
        if (!File.Exists(_options.ModelPath))
        {
            var candidates = string.Join(Environment.NewLine, ClipRuntimeOptions.GetDefaultModelPathCandidates().Select(path => $"  - {path}"));
            throw new FileNotFoundException(
                $"CLIP ONNX model was not found. Deploy {ClipRuntimeOptions.ModelFileName} to RuntimeData\\Models. Current path: {_options.ModelPath}{Environment.NewLine}Searched:{Environment.NewLine}{candidates}",
                _options.ModelPath);
        }
    }

    private static async ValueTask<IReadOnlyList<EncodedImage>> EncodeImagesAsync(
        OnnxClipImageEncoder encoder,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        var encoded = new List<EncodedImage>(imagePaths.Count);
        foreach (var imagePath in imagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(imagePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("CLIP sample image was not found.", fullPath);
            }

            encoded.Add(new EncodedImage(fullPath, await encoder.EncodeImageAsync(fullPath, cancellationToken)));
        }

        return encoded;
    }

    private static IReadOnlyList<ClipMatchResult> MapMatches(IReadOnlyList<VectorMatch> matches)
    {
        return matches
            .Select(match => new ClipMatchResult
            {
                Rank = match.Rank,
                Similarity = match.Similarity,
                ImagePath = match.ImagePath
            })
            .ToArray();
    }

    private sealed record EncodedImage(string ImagePath, float[] Feature);
}
