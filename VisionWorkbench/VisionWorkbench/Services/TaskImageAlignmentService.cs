using System.IO;
using System.Windows.Media;
using OpenCvSharp;
using RoiAlignment.Core;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class TaskImageAlignmentService : IDisposable
{
    private readonly VisionAssetPathService _assetPathService;
    private readonly AlignmentTemplateStore _templateStore;
    private readonly Dictionary<string, CachedTemplateRecord?> _templateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CachedTemplateRecord> _retiredTemplates = [];
    private readonly object _cacheLock = new();
    private bool _disposed;

    public TaskImageAlignmentService(VisionAssetPathService assetPathService)
    {
        _assetPathService = assetPathService;
        _templateStore = new AlignmentTemplateStore(assetPathService.GetAlignmentTemplateDatabasePath());
    }

    public TaskImageAlignmentResult AlignToTemplate(CameraAlignmentDefinition definition, ImageSource runtimeImage)
    {
        ThrowIfDisposed();
        try
        {
            using var runtimeMat = MatImageSourceConverter.CreateMat(runtimeImage);
            var result = AlignMatToTemplate(definition, runtimeMat);
            if (!result.Success || result.Image == null)
            {
                return TaskImageAlignmentResult.Fail(result.Message);
            }

            using var aligned = result.Image;
            return TaskImageAlignmentResult.Ok(MatImageSourceConverter.CreateImageSource(aligned), result.Message);
        }
        catch (Exception ex)
        {
            return TaskImageAlignmentResult.Fail(ex.Message);
        }
    }

    public TaskMatAlignmentResult AlignMatToTemplate(CameraAlignmentDefinition definition, Mat runtimeMat)
    {
        ThrowIfDisposed();
        if (!definition.IsEffectiveAlignmentRegionCurrent)
        {
            return TaskMatAlignmentResult.Fail("有效区域已变更，请重新创建模板。");
        }

        if (runtimeMat.Empty())
        {
            return TaskMatAlignmentResult.Fail("运行图像为空");
        }

        try
        {
            using var templateSource = ResolveTemplate(definition);
            if (templateSource == null)
            {
                return TaskMatAlignmentResult.Fail("模板描述符未创建");
            }

            var alignment = new RoiAligner(templateSource.Options).AlignImage(templateSource.PreparedTemplate, runtimeMat);
            if (!alignment.Success || alignment.Transform == null)
            {
                return TaskMatAlignmentResult.Fail(FormatAlignmentFailure(templateSource, alignment));
            }

            var aligned = WarpRuntimeToTemplate(runtimeMat, alignment.Transform, templateSource.PreparedTemplate.Template);
            return TaskMatAlignmentResult.Ok(aligned, FormatAlignmentSuccess(templateSource, alignment));
        }
        catch (Exception ex)
        {
            return TaskMatAlignmentResult.Fail(ex.Message);
        }
    }

    private TemplateSource? ResolveTemplate(CameraAlignmentDefinition definition)
    {
        var sqliteRecord = LoadCachedSqliteTemplate(definition);
        if (sqliteRecord?.Record.Template.IsEmpty == false)
        {
            return new TemplateSource(
                sqliteRecord.PreparedTemplate,
                CreateOptions(sqliteRecord.Record.Template, definition),
                IsLegacy: false,
                OwnsPreparedTemplate: false);
        }

        return ResolveLegacyTemplate(definition);
    }

    private CachedTemplateRecord? LoadCachedSqliteTemplate(CameraAlignmentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ProductModelId) ||
            string.IsNullOrWhiteSpace(definition.CameraId))
        {
            return null;
        }

        var databasePath = _assetPathService.GetAlignmentTemplateDatabasePath();
        if (!File.Exists(databasePath))
        {
            return null;
        }

        var cacheKey = BuildCacheKey(definition.ProductModelId, definition.CameraId);
        var databaseWriteTime = File.GetLastWriteTimeUtc(databasePath);
        lock (_cacheLock)
        {
            if (_templateCache.TryGetValue(cacheKey, out var cached) &&
                cached?.DatabaseWriteTimeUtc == databaseWriteTime)
            {
                return cached;
            }
        }

        var record = _templateStore.Load(definition.ProductModelId, definition.CameraId);
        var next = record == null
            ? null
            : new CachedTemplateRecord(record, new PreparedAlignmentTemplate(record.Template), databaseWriteTime);
        lock (_cacheLock)
        {
            if (_templateCache.TryGetValue(cacheKey, out var previous) &&
                previous != null &&
                !ReferenceEquals(previous, next))
            {
                _retiredTemplates.Add(previous);
            }

            _templateCache[cacheKey] = next;
        }

        return next;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        List<CachedTemplateRecord> templates;
        lock (_cacheLock)
        {
            templates = _templateCache.Values
                .Where(template => template != null)
                .Cast<CachedTemplateRecord>()
                .Concat(_retiredTemplates)
                .Distinct()
                .ToList();
            _templateCache.Clear();
            _retiredTemplates.Clear();
            _disposed = true;
        }

        foreach (var template in templates)
        {
            template.PreparedTemplate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private TemplateSource? ResolveLegacyTemplate(CameraAlignmentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.TemplateRelativePath) ||
            definition.DescriptorRows <= 0 ||
            definition.DescriptorCols <= 0)
        {
            return null;
        }

        var templatePath = _assetPathService.GetFullPath(definition.TemplateRelativePath);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("模板文件不存在", templatePath);
        }

        var template = AlignmentTemplate.Load(templatePath);
        if (template.IsEmpty)
        {
            throw new InvalidOperationException("模板描述符为空");
        }

        return new TemplateSource(
            new PreparedAlignmentTemplate(template),
            CreateOptions(template, definition),
            IsLegacy: true,
            OwnsPreparedTemplate: true);
    }

    private static AlignmentOptions CreateOptions(AlignmentTemplate template, CameraAlignmentDefinition definition)
    {
        // 使用模板中保存的算法配置，但优先使用用户在参数设置中修改的值
        return new AlignmentOptions
        {
            FeatureMethod = template.FeatureMethod,
            TransformModel = template.TransformModel,
            MaxLongSide = template.MaxLongSide > 0 ? template.MaxLongSide : NormalizeInt(definition.MaxLongSide, 1600),
            MaxFeatures = template.MaxFeatures > 0 ? template.MaxFeatures : NormalizeInt(definition.MaxFeatures, 5000),
            LoweRatio = definition.LoweRatio > 0 ? definition.LoweRatio : 0.75,
            MinGoodMatches = definition.MinGoodMatches > 0 ? definition.MinGoodMatches : 12,
            MinInliers = definition.MinInliers > 0 ? definition.MinInliers : 8,
            MinInlierRatio = definition.MinInlierRatio > 0 ? definition.MinInlierRatio : 0.30,
            RansacReprojectionThreshold = definition.RansacReprojectionThreshold > 0 ? definition.RansacReprojectionThreshold : 3.0,
            MaxReprojectionRmse = definition.MaxReprojectionRmse > 0 ? definition.MaxReprojectionRmse : 4.0
        };
    }

    private static string FormatAlignmentSuccess(TemplateSource source, AlignmentResult result)
    {
        var legacy = source.IsLegacy ? "，legacy模板，建议重新创建" : "";
        return
            $"图像已对齐{legacy}。Good={result.GoodMatches}, Inliers={result.Inliers}, " +
            $"Ratio={result.InlierRatio:0.00}, RMSE={result.ReprojectionRmse:0.00}, " +
            $"{FormatTiming(result.Timing)}, {result.MatcherDescription}" +
            FormatAffineParameters(result.Transform);
    }

    private static string FormatAlignmentFailure(TemplateSource source, AlignmentResult result)
    {
        var legacy = source.IsLegacy ? "，legacy模板，建议重新创建" : "";
        return
            $"图像对齐失败：{FormatFailureReason(result.FailureReason)}{legacy}。Good={result.GoodMatches}, " +
            $"Inliers={result.Inliers}, Ratio={result.InlierRatio:0.00}, RMSE={FormatRmse(result.ReprojectionRmse)}, " +
            $"{FormatTiming(result.Timing)}, {result.MatcherDescription}";
    }

    private static string FormatFailureReason(AlignmentFailureReason reason)
    {
        return reason switch
        {
            AlignmentFailureReason.None => "无",
            AlignmentFailureReason.EmptyTemplate => "模板特征为空",
            AlignmentFailureReason.NoRuntimeFeatures => "运行图像特征为空",
            AlignmentFailureReason.NotEnoughMatches => "匹配点不足",
            AlignmentFailureReason.TransformEstimationFailed => "变换估计失败",
            AlignmentFailureReason.NotEnoughInliers => "内点数量不足",
            AlignmentFailureReason.InlierRatioTooLow => "内点比例过低",
            AlignmentFailureReason.ReprojectionErrorTooHigh => "重投影误差过高",
            AlignmentFailureReason.RoiTransformInvalid => "ROI变换无效",
            AlignmentFailureReason.UnsupportedFeatureMethod => "不支持的特征方法",
            AlignmentFailureReason.UnsupportedTransformModel => "不支持的变换模型",
            _ => reason.ToString()
        };
    }

    private static string FormatTiming(AlignmentTiming timing)
    {
        return
            $"Feature={timing.RuntimeFeatureExtraction.TotalMilliseconds:0}ms, " +
            $"Match={timing.Matching.TotalMilliseconds:0}ms, " +
            $"RANSAC={timing.TransformEstimation.TotalMilliseconds:0}ms, " +
            $"Total={timing.Total.TotalMilliseconds:0}ms";
    }

    private static string FormatAffineParameters(TransformData? transform)
    {
        if (transform == null ||
            transform.Model != TransformModel.AffinePartial ||
            transform.Rows != 2 ||
            transform.Cols != 3 ||
            transform.Values.Length < 6)
        {
            return string.Empty;
        }

        var m00 = transform.Values[0];
        var m01 = transform.Values[1];
        var tx = transform.Values[2];
        var m10 = transform.Values[3];
        var m11 = transform.Values[4];
        var ty = transform.Values[5];
        var scaleX = Math.Sqrt(m00 * m00 + m10 * m10);
        var scaleY = Math.Sqrt(m01 * m01 + m11 * m11);
        var scale = (scaleX + scaleY) / 2.0;
        var rotationDegrees = Math.Atan2(m10, m00) * 180.0 / Math.PI;

        return
            $"{Environment.NewLine}仿射参数（模板->运行图）：" +
            $"{Environment.NewLine}平移X={tx:0.00}px, 平移Y={ty:0.00}px" +
            $"{Environment.NewLine}旋转={rotationDegrees:0.00}°, Scale={scale:0.0000}, ScaleX={scaleX:0.0000}, ScaleY={scaleY:0.0000}";
    }

    private static string FormatRmse(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? "--" : value.ToString("0.00");
    }

    private static int NormalizeInt(int value, int fallback) => value > 0 ? value : fallback;

    private static double NormalizeDouble(double value, double fallback) => value > 0 ? value : fallback;

    private static string BuildCacheKey(string productModelId, string cameraId) => $"{productModelId}::{cameraId}";

    private static Mat WarpRuntimeToTemplate(Mat runtimeMat, TransformData transform, AlignmentTemplate template)
    {
        if (transform.Model != TransformModel.AffinePartial ||
            transform.Rows != 2 ||
            transform.Cols != 3 ||
            transform.Values.Length < 6)
        {
            throw new InvalidOperationException("当前只支持 AffinePartial 模板对齐。");
        }

        if (template.ImageWidth <= 0 || template.ImageHeight <= 0)
        {
            throw new InvalidOperationException("模板图像尺寸无效。");
        }

        using var transformMat = new Mat(2, 3, MatType.CV_64FC1);
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                transformMat.Set(row, column, transform.Values[row * 3 + column]);
            }
        }

        using var inverse = new Mat();
        Cv2.InvertAffineTransform(transformMat, inverse);

        var aligned = new Mat();
        Cv2.WarpAffine(
            runtimeMat,
            aligned,
            inverse,
            new Size(template.ImageWidth, template.ImageHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);
        return aligned;
    }

    private sealed record TemplateSource(
        PreparedAlignmentTemplate PreparedTemplate,
        AlignmentOptions Options,
        bool IsLegacy,
        bool OwnsPreparedTemplate) : IDisposable
    {
        public void Dispose()
        {
            if (OwnsPreparedTemplate)
            {
                PreparedTemplate.Dispose();
            }
        }
    }

    private sealed record CachedTemplateRecord(
        AlignmentTemplateRecord Record,
        PreparedAlignmentTemplate PreparedTemplate,
        DateTime DatabaseWriteTimeUtc);
}

public sealed record TaskImageAlignmentResult(bool Success, ImageSource? Image, string Message)
{
    public static TaskImageAlignmentResult Ok(ImageSource image, string message) => new(true, image, message);

    public static TaskImageAlignmentResult Fail(string message) => new(false, null, message);
}

public sealed record TaskMatAlignmentResult(bool Success, Mat? Image, string Message)
{
    public static TaskMatAlignmentResult Ok(Mat image, string message) => new(true, image, message);

    public static TaskMatAlignmentResult Fail(string message) => new(false, null, message);
}
