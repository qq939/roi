using OpenCvSharp;
using System.Diagnostics;

namespace RoiAlignment.Core;

public sealed class RoiAligner
{
    private readonly AlignmentOptions _options;

    public RoiAligner()
        : this(new AlignmentOptions())
    {
    }

    public RoiAligner(AlignmentOptions options)
    {
        _options = options;
    }

    public static AlignmentResult Align(AlignmentTemplate template, Mat runtimeImage) =>
        new RoiAligner().AlignImage(template, runtimeImage);

    public static AlignmentResult Align(
        AlignmentTemplate template,
        Mat runtimeImage,
        IReadOnlyList<RoiShape> referenceRois) =>
        new RoiAligner().AlignImage(template, runtimeImage, referenceRois);

    public static AlignmentResult Align(RoiAlignmentProject project, Mat runtimeImage) =>
        new RoiAligner().AlignImage(project.Template, runtimeImage, project.Rois);

    public AlignmentResult AlignImage(AlignmentTemplate template, Mat runtimeImage) =>
        AlignImage(template, runtimeImage, null);

    public AlignmentResult AlignImage(
        AlignmentTemplate template,
        Mat runtimeImage,
        IReadOnlyList<RoiShape>? referenceRois)
    {
        using var preparedTemplate = new PreparedAlignmentTemplate(template);
        return AlignImage(preparedTemplate, runtimeImage, referenceRois);
    }

    public AlignmentResult AlignImage(PreparedAlignmentTemplate preparedTemplate, Mat runtimeImage) =>
        AlignImage(preparedTemplate, runtimeImage, null);

    public AlignmentResult AlignImage(
        PreparedAlignmentTemplate preparedTemplate,
        Mat runtimeImage,
        IReadOnlyList<RoiShape>? referenceRois)
    {
        var totalStopwatch = Stopwatch.StartNew();
        ArgumentNullException.ThrowIfNull(preparedTemplate);
        ArgumentNullException.ThrowIfNull(runtimeImage);
        var template = preparedTemplate.Template;

        if (template.IsEmpty)
        {
            return AlignmentResult.Fail(AlignmentFailureReason.EmptyTemplate, template.TransformModel);
        }

        if (runtimeImage.Empty())
        {
            return AlignmentResult.Fail(AlignmentFailureReason.NoRuntimeFeatures, template.TransformModel);
        }

        if (template.FeatureMethod != _options.FeatureMethod)
        {
            return AlignmentResult.Fail(AlignmentFailureReason.UnsupportedFeatureMethod, template.TransformModel);
        }

        if (template.TransformModel != TransformModel.AffinePartial || _options.TransformModel != TransformModel.AffinePartial)
        {
            return AlignmentResult.Fail(AlignmentFailureReason.UnsupportedTransformModel, template.TransformModel);
        }

        var maxLongSide = template.MaxLongSide > 0 ? template.MaxLongSide : _options.MaxLongSide;
        var maxFeatures = template.MaxFeatures > 0 ? template.MaxFeatures : _options.MaxFeatures;
        using var runtimeProcessed = ImagePreprocessor.ResizeToMaxLongSide(runtimeImage, maxLongSide, out var runtimeScale);
        using var runtimeGray = ImagePreprocessor.ToGray(runtimeProcessed);
        using var runtimeDescriptors = new Mat();

        var featureStopwatch = Stopwatch.StartNew();
        var runtimeKeyPoints = FeatureExtractor.Extract(template.FeatureMethod, runtimeGray, null, runtimeDescriptors, maxFeatures);
        featureStopwatch.Stop();
        if (runtimeKeyPoints.Length == 0 || runtimeDescriptors.Empty())
        {
            return AlignmentResult.Fail(AlignmentFailureReason.NoRuntimeFeatures, template.TransformModel);
        }

        var matcherDescription = CreateMatcherDescription(template.FeatureMethod);

        var matchingStopwatch = Stopwatch.StartNew();
        DMatch[][] knnMatches;
        lock (preparedTemplate.SyncRoot)
        {
            knnMatches = preparedTemplate.Matcher.KnnMatch(runtimeDescriptors, k: 2);
        }

        var rawMatches = knnMatches.Length;
        var goodMatches = knnMatches
            .Where(matches => matches.Length >= 2 && matches[0].Distance < _options.LoweRatio * matches[1].Distance)
            .Select(matches => matches[0])
            .ToArray();
        matchingStopwatch.Stop();

        if (goodMatches.Length < _options.MinGoodMatches)
        {
            totalStopwatch.Stop();
            return AlignmentResult.Fail(
                AlignmentFailureReason.NotEnoughMatches,
                template.TransformModel,
                rawMatches: rawMatches,
                goodMatches: goodMatches.Length,
                matcherDescription: matcherDescription,
                timing: new AlignmentTiming
                {
                    RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                    Matching = matchingStopwatch.Elapsed,
                    Total = totalStopwatch.Elapsed
                });
        }

        var referencePoints = goodMatches
            .Select(match => template.KeyPoints[match.TrainIdx].Point.ToOpenCv())
            .ToArray();
        var runtimePoints = goodMatches
            .Select(match => runtimeKeyPoints[match.QueryIdx].Pt)
            .ToArray();

        using var inlierMask = new Mat();
        using var referencePointMat = Mat.FromArray(referencePoints);
        using var runtimePointMat = Mat.FromArray(runtimePoints);

        var transformStopwatch = Stopwatch.StartNew();
        var transform = Cv2.EstimateAffinePartial2D(
            referencePointMat,
            runtimePointMat,
            inlierMask,
            RobustEstimationAlgorithms.RANSAC,
            _options.RansacReprojectionThreshold);
        transformStopwatch.Stop();

        if (transform is null || transform.Empty())
        {
            transform?.Dispose();
            totalStopwatch.Stop();
            return AlignmentResult.Fail(
                AlignmentFailureReason.TransformEstimationFailed,
                template.TransformModel,
                rawMatches: rawMatches,
                goodMatches: goodMatches.Length,
                matcherDescription: matcherDescription,
                timing: new AlignmentTiming
                {
                    RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                    Matching = matchingStopwatch.Elapsed,
                    TransformEstimation = transformStopwatch.Elapsed,
                    Total = totalStopwatch.Elapsed
                });
        }

        using (transform)
        {
            var inliers = CountInliers(inlierMask);
            var inlierRatio = goodMatches.Length == 0 ? 0 : (double)inliers / goodMatches.Length;
            var rmse = ComputeAffineRmse(transform, referencePoints, runtimePoints, inlierMask);

            if (inliers < _options.MinInliers)
            {
                totalStopwatch.Stop();
                return AlignmentResult.Fail(
                    AlignmentFailureReason.NotEnoughInliers,
                    template.TransformModel,
                    rawMatches,
                    goodMatches.Length,
                    inliers,
                    inlierRatio,
                    rmse,
                    matcherDescription,
                    new AlignmentTiming
                    {
                        RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                        Matching = matchingStopwatch.Elapsed,
                        TransformEstimation = transformStopwatch.Elapsed,
                        Total = totalStopwatch.Elapsed
                    });
            }

            if (inlierRatio < _options.MinInlierRatio)
            {
                totalStopwatch.Stop();
                return AlignmentResult.Fail(
                    AlignmentFailureReason.InlierRatioTooLow,
                    template.TransformModel,
                    rawMatches,
                    goodMatches.Length,
                    inliers,
                    inlierRatio,
                    rmse,
                    matcherDescription,
                    new AlignmentTiming
                    {
                        RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                        Matching = matchingStopwatch.Elapsed,
                        TransformEstimation = transformStopwatch.Elapsed,
                        Total = totalStopwatch.Elapsed
                    });
            }

            if (rmse > _options.MaxReprojectionRmse)
            {
                totalStopwatch.Stop();
                return AlignmentResult.Fail(
                    AlignmentFailureReason.ReprojectionErrorTooHigh,
                    template.TransformModel,
                    rawMatches,
                    goodMatches.Length,
                    inliers,
                    inlierRatio,
                    rmse,
                    matcherDescription,
                    new AlignmentTiming
                    {
                        RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                        Matching = matchingStopwatch.Elapsed,
                        TransformEstimation = transformStopwatch.Elapsed,
                        Total = totalStopwatch.Elapsed
                    });
            }

            using var originalTransform = ConvertTransformToOriginalCoordinates(
                transform,
                template.ProcessingScale <= 0 ? 1.0 : template.ProcessingScale,
                runtimeScale <= 0 ? 1.0 : runtimeScale);
            var transformData = TransformData.FromMat(template.TransformModel, originalTransform);
            var result = new AlignmentResult
            {
                Success = true,
                FailureReason = AlignmentFailureReason.None,
                TransformModel = template.TransformModel,
                Transform = transformData,
                RawMatches = rawMatches,
                GoodMatches = goodMatches.Length,
                Inliers = inliers,
                InlierRatio = inlierRatio,
                ReprojectionRmse = rmse,
                Confidence = ComputeConfidence(inlierRatio, rmse),
                MatcherDescription = matcherDescription
            };

            if (referenceRois is null)
            {
                totalStopwatch.Stop();
                return result with
                {
                    Timing = new AlignmentTiming
                    {
                        RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                        Matching = matchingStopwatch.Elapsed,
                        TransformEstimation = transformStopwatch.Elapsed,
                        Total = totalStopwatch.Elapsed
                    }
                };
            }

            var roiStopwatch = Stopwatch.StartNew();
            var alignedRois = result.TransformRois(referenceRois);
            roiStopwatch.Stop();
            totalStopwatch.Stop();

            return result with
            {
                AlignedRois = alignedRois,
                Timing = new AlignmentTiming
                {
                    RuntimeFeatureExtraction = featureStopwatch.Elapsed,
                    Matching = matchingStopwatch.Elapsed,
                    TransformEstimation = transformStopwatch.Elapsed,
                    RoiTransform = roiStopwatch.Elapsed,
                    Total = totalStopwatch.Elapsed
                }
            };
        }
    }

    private static int CountInliers(Mat inlierMask)
    {
        var count = 0;
        for (var i = 0; i < inlierMask.Rows; i++)
        {
            if (inlierMask.At<byte>(i, 0) != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static double ComputeAffineRmse(
        Mat transform,
        IReadOnlyList<Point2f> referencePoints,
        IReadOnlyList<Point2f> runtimePoints,
        Mat inlierMask)
    {
        var m00 = transform.At<double>(0, 0);
        var m01 = transform.At<double>(0, 1);
        var m02 = transform.At<double>(0, 2);
        var m10 = transform.At<double>(1, 0);
        var m11 = transform.At<double>(1, 1);
        var m12 = transform.At<double>(1, 2);

        var sumSquared = 0.0;
        var count = 0;
        for (var i = 0; i < referencePoints.Count; i++)
        {
            if (inlierMask.At<byte>(i, 0) == 0)
            {
                continue;
            }

            var projectedX = referencePoints[i].X * m00 + referencePoints[i].Y * m01 + m02;
            var projectedY = referencePoints[i].X * m10 + referencePoints[i].Y * m11 + m12;
            var dx = projectedX - runtimePoints[i].X;
            var dy = projectedY - runtimePoints[i].Y;
            sumSquared += dx * dx + dy * dy;
            count++;
        }

        return count == 0 ? double.PositiveInfinity : Math.Sqrt(sumSquared / count);
    }

    internal static DescriptorMatcher CreateMatcher(FeatureMethod method)
    {
        if (method == FeatureMethod.Sift)
        {
            return DescriptorMatcher.Create("FlannBased");
        }

        return new BFMatcher(FeatureExtractor.GetMatcherNorm(method), crossCheck: false);
    }

    private static string CreateMatcherDescription(FeatureMethod method)
    {
        if (method == FeatureMethod.Sift)
        {
            return "FlannBasedMatcher.KnnMatch(k=2) + Lowe ratio";
        }

        var normType = FeatureExtractor.GetMatcherNorm(method);
        return $"BFMatcher.KnnMatch(k=2, norm={normType}) + Lowe ratio";
    }

    private static Mat ConvertTransformToOriginalCoordinates(
        Mat scaledTransform,
        double referenceScale,
        double runtimeScale)
    {
        var original = new Mat(2, 3, MatType.CV_64FC1);
        var refScale = Math.Max(referenceScale, 0.000001);
        var runScale = Math.Max(runtimeScale, 0.000001);
        var factor = refScale / runScale;
        scaledTransform.ConvertTo(original, MatType.CV_64FC1);

        original.Set(0, 0, original.At<double>(0, 0) * factor);
        original.Set(0, 1, original.At<double>(0, 1) * factor);
        original.Set(0, 2, original.At<double>(0, 2) / runScale);
        original.Set(1, 0, original.At<double>(1, 0) * factor);
        original.Set(1, 1, original.At<double>(1, 1) * factor);
        original.Set(1, 2, original.At<double>(1, 2) / runScale);
        return original;
    }

    private double ComputeConfidence(double inlierRatio, double rmse)
    {
        var rmseScore = Math.Max(0, 1.0 - rmse / Math.Max(_options.MaxReprojectionRmse, 0.001));
        return Math.Clamp((inlierRatio + rmseScore) / 2.0, 0, 1);
    }
}

public sealed class PreparedAlignmentTemplate : IDisposable
{
    private readonly Mat _referenceDescriptors;
    private bool _disposed;

    public PreparedAlignmentTemplate(AlignmentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.IsEmpty)
        {
            throw new ArgumentException("Alignment template cannot be empty.", nameof(template));
        }

        Template = template;
        _referenceDescriptors = OpenCvMatData.ToMat(template.Descriptors);
        Matcher = RoiAligner.CreateMatcher(template.FeatureMethod);
        Matcher.Add([_referenceDescriptors]);
        Matcher.Train();
    }

    public AlignmentTemplate Template { get; }

    internal DescriptorMatcher Matcher { get; }

    internal object SyncRoot { get; } = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Matcher.Dispose();
        _referenceDescriptors.Dispose();
        _disposed = true;
    }
}

public sealed record AlignmentResult
{
    public bool Success { get; init; }
    public AlignmentFailureReason FailureReason { get; init; }
    public TransformModel TransformModel { get; init; }
    public TransformData? Transform { get; init; }
    public int RawMatches { get; init; }
    public int GoodMatches { get; init; }
    public int Inliers { get; init; }
    public double InlierRatio { get; init; }
    public double ReprojectionRmse { get; init; }
    public double Confidence { get; init; }
    public string MatcherDescription { get; init; } = "";
    public AlignmentTiming Timing { get; init; } = new();
    public IReadOnlyList<RoiShape> AlignedRois { get; init; } = [];

    public IReadOnlyList<RoiShape> TransformRois(IReadOnlyList<RoiShape> referenceRois)
    {
        ArgumentNullException.ThrowIfNull(referenceRois);
        if (!Success || Transform is null)
        {
            throw new InvalidOperationException("Cannot transform ROIs when alignment failed.");
        }

        return referenceRois.Select(TransformRoi).ToArray();
    }

    internal static AlignmentResult Fail(
        AlignmentFailureReason reason,
        TransformModel transformModel,
        int rawMatches = 0,
        int goodMatches = 0,
        int inliers = 0,
        double inlierRatio = 0,
        double reprojectionRmse = double.PositiveInfinity,
        string matcherDescription = "",
        AlignmentTiming? timing = null) => new()
        {
            Success = false,
            FailureReason = reason,
            TransformModel = transformModel,
            RawMatches = rawMatches,
            GoodMatches = goodMatches,
            Inliers = inliers,
            InlierRatio = inlierRatio,
            ReprojectionRmse = reprojectionRmse,
            Confidence = 0,
            MatcherDescription = matcherDescription,
            Timing = timing ?? new AlignmentTiming()
        };

    private RoiShape TransformRoi(RoiShape roi)
    {
        var transformedPoints = roi.Points
            .Select(point => TransformPoint(point, Transform!))
            .ToArray();

        if (transformedPoints.Any(point =>
                double.IsNaN(point.X) ||
                double.IsNaN(point.Y) ||
                double.IsInfinity(point.X) ||
                double.IsInfinity(point.Y)) ||
            RoiGeometry.PolygonArea(transformedPoints) <= 0.000001)
        {
            throw new InvalidOperationException($"ROI '{roi.Name}' cannot be transformed by the alignment result.");
        }

        return new RoiShape
        {
            Name = roi.Name,
            Kind = roi.Kind,
            Points = transformedPoints,
            Tags = roi.Tags
        };
    }

    private static Point2fDto TransformPoint(Point2fDto point, TransformData transform)
    {
        if (transform.Model is TransformModel.AffinePartial or TransformModel.Affine)
        {
            var values = transform.Values;
            var x = point.X * values[0] + point.Y * values[1] + values[2];
            var y = point.X * values[3] + point.Y * values[4] + values[5];
            return new Point2fDto(x, y);
        }

        if (transform.Model == TransformModel.Homography)
        {
            var values = transform.Values;
            var denominator = point.X * values[6] + point.Y * values[7] + values[8];
            var x = (point.X * values[0] + point.Y * values[1] + values[2]) / denominator;
            var y = (point.X * values[3] + point.Y * values[4] + values[5]) / denominator;
            return new Point2fDto(x, y);
        }

        throw new NotSupportedException($"Unsupported transform model: {transform.Model}");
    }
}
