using OpenCvSharp;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class Measurement1DService
{
    public Measurement1DResult Measure(Mat roiImage, MeasurementOptions options)
    {
        return Analyze(roiImage, options).ToResult();
    }

    public MeasurementProfileAnalysis Analyze(Mat roiImage, MeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(roiImage);
        ArgumentNullException.ThrowIfNull(options);
        options.Normalize();

        if (roiImage.Empty())
        {
            return MeasurementProfileAnalysis.Fail("ROI图像为空");
        }

        if (roiImage.Width < 3 || roiImage.Height < 2)
        {
            return MeasurementProfileAnalysis.Fail("ROI尺寸不足，无法提取两条边");
        }

        using var gray = ToGray(roiImage);
        var profile = BuildWidthProfile(gray);
        var smoothed = Smooth(profile, options.SmoothWindow);
        var gradient = BuildGradient(smoothed, options.SmoothWindow);

        if (!TryFindEdge(gradient, options.FirstEdgePolarity, 1, options.EdgeThreshold, out var firstEdge))
        {
            return MeasurementProfileAnalysis.Fail(
                "未找到边1",
                profile,
                smoothed,
                gradient);
        }

        var secondStart = Math.Max(1, (int)Math.Ceiling(firstEdge.Index + options.MinEdgeGapPx));
        if (!TryFindEdge(gradient, options.SecondEdgePolarity, secondStart, options.EdgeThreshold, out var secondEdge))
        {
            return MeasurementProfileAnalysis.Fail(
                "未找到边2",
                profile,
                smoothed,
                gradient,
                firstEdge.Index,
                null,
                firstEdge.Strength);
        }

        var distancePx = Math.Abs(secondEdge.Index - firstEdge.Index);
        var distanceMm = distancePx * options.PixelToMillimeterScale;
        var judgment = distanceMm >= options.MinDistanceMm && distanceMm <= options.MaxDistanceMm
            ? InspectionJudgment.OK
            : InspectionJudgment.NG;
        var failure = judgment == InspectionJudgment.OK
            ? null
            : $"距离超限，范围 {options.MinDistanceMm:0.##}-{options.MaxDistanceMm:0.##} mm";

        return new MeasurementProfileAnalysis
        {
            Judgment = judgment,
            FailureReason = failure,
            RawProfile = profile,
            SmoothedProfile = smoothed,
            Gradient = gradient,
            DistancePx = distancePx,
            DistanceMm = distanceMm,
            FirstEdgeIndex = firstEdge.Index,
            SecondEdgeIndex = secondEdge.Index,
            FirstEdgeStrength = firstEdge.Strength,
            SecondEdgeStrength = secondEdge.Strength
        };
    }

    private static Mat ToGray(Mat image)
    {
        if (image.Channels() == 1)
        {
            return image.Clone();
        }

        var gray = new Mat();
        var conversion = image.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY
            : ColorConversionCodes.BGR2GRAY;
        Cv2.CvtColor(image, gray, conversion);
        return gray;
    }

    private static double[] BuildWidthProfile(Mat gray)
    {
        var profile = new double[gray.Width];
        for (var x = 0; x < gray.Width; x++)
        {
            using var column = gray.Col(x);
            profile[x] = Cv2.Mean(column).Val0;
        }

        return profile;
    }

    private static double[] Smooth(IReadOnlyList<double> values, int window)
    {
        window = Math.Max(1, window);
        if (window % 2 == 0)
        {
            window++;
        }

        if (window <= 1 || values.Count == 0)
        {
            return values.ToArray();
        }

        var radius = window / 2;
        var smoothed = new double[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var start = Math.Max(0, index - radius);
            var end = Math.Min(values.Count - 1, index + radius);
            var sum = 0.0;
            for (var i = start; i <= end; i++)
            {
                sum += values[i];
            }

            smoothed[index] = sum / (end - start + 1);
        }

        return smoothed;
    }

    private static double[] BuildGradient(IReadOnlyList<double> values, int smoothWindow)
    {
        var gradient = new double[values.Count];
        smoothWindow = Math.Max(1, smoothWindow);
        if (smoothWindow % 2 == 0)
        {
            smoothWindow++;
        }

        // Keep edge strength on the same scale after smoothing; otherwise a larger
        // SmoothWindow can dilute even a sharp edge below threshold.
        var span = Math.Max(1, (smoothWindow + 1) / 2);
        for (var i = 1; i < values.Count - 1; i++)
        {
            var left = Math.Max(0, i - span);
            var right = Math.Min(values.Count - 1, i + span);
            gradient[i] = (values[right] - values[left]) / 2.0;
        }

        return gradient;
    }

    private static bool TryFindEdge(
        IReadOnlyList<double> gradient,
        MeasurementEdgePolarity polarity,
        int startIndex,
        double threshold,
        out MeasurementEdge edge)
    {
        startIndex = Math.Max(1, startIndex);
        var endIndex = gradient.Count - 2;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var previous = SignedStrength(gradient[i - 1], polarity);
            var current = SignedStrength(gradient[i], polarity);
            var next = SignedStrength(gradient[i + 1], polarity);
            if (current < threshold || current < previous || current < next)
            {
                continue;
            }

            var offset = EstimateSubPixelOffset(previous, current, next);
            edge = new MeasurementEdge(i + offset, current);
            return true;
        }

        edge = default;
        return false;
    }

    private static double SignedStrength(double gradient, MeasurementEdgePolarity polarity)
    {
        return polarity == MeasurementEdgePolarity.BlackToWhite ? gradient : -gradient;
    }

    private static double EstimateSubPixelOffset(double left, double center, double right)
    {
        var denominator = left - (2.0 * center) + right;
        if (Math.Abs(denominator) < 1e-6)
        {
            return 0;
        }

        return Math.Clamp(0.5 * (left - right) / denominator, -1.0, 1.0);
    }

    private readonly record struct MeasurementEdge(double Index, double Strength);
}

public sealed class MeasurementProfileAnalysis
{
    public required InspectionJudgment Judgment { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyList<double> RawProfile { get; init; } = Array.Empty<double>();

    public IReadOnlyList<double> SmoothedProfile { get; init; } = Array.Empty<double>();

    public IReadOnlyList<double> Gradient { get; init; } = Array.Empty<double>();

    public double? DistancePx { get; init; }

    public double? DistanceMm { get; init; }

    public double? FirstEdgeIndex { get; init; }

    public double? SecondEdgeIndex { get; init; }

    public double? FirstEdgeStrength { get; init; }

    public double? SecondEdgeStrength { get; init; }

    public bool HasMeasurement => DistancePx.HasValue && DistanceMm.HasValue;

    public Measurement1DResult ToResult()
    {
        return new Measurement1DResult
        {
            Judgment = Judgment,
            DistancePx = DistancePx ?? 0,
            DistanceMm = DistanceMm ?? 0,
            FirstEdgeIndex = FirstEdgeIndex ?? 0,
            SecondEdgeIndex = SecondEdgeIndex ?? 0,
            FirstEdgeStrength = FirstEdgeStrength ?? 0,
            SecondEdgeStrength = SecondEdgeStrength ?? 0,
            FailureReason = FailureReason
        };
    }

    public static MeasurementProfileAnalysis Fail(
        string reason,
        IReadOnlyList<double>? rawProfile = null,
        IReadOnlyList<double>? smoothedProfile = null,
        IReadOnlyList<double>? gradient = null,
        double? firstEdgeIndex = null,
        double? secondEdgeIndex = null,
        double? firstEdgeStrength = null,
        double? secondEdgeStrength = null)
    {
        return new MeasurementProfileAnalysis
        {
            Judgment = InspectionJudgment.NG,
            FailureReason = reason,
            RawProfile = rawProfile ?? Array.Empty<double>(),
            SmoothedProfile = smoothedProfile ?? Array.Empty<double>(),
            Gradient = gradient ?? Array.Empty<double>(),
            FirstEdgeIndex = firstEdgeIndex,
            SecondEdgeIndex = secondEdgeIndex,
            FirstEdgeStrength = firstEdgeStrength,
            SecondEdgeStrength = secondEdgeStrength
        };
    }
}

public sealed class Measurement1DResult
{
    public required InspectionJudgment Judgment { get; init; }

    public double DistancePx { get; init; }

    public double DistanceMm { get; init; }

    public double FirstEdgeIndex { get; init; }

    public double SecondEdgeIndex { get; init; }

    public double FirstEdgeStrength { get; init; }

    public double SecondEdgeStrength { get; init; }

    public string? FailureReason { get; init; }

    public static Measurement1DResult Fail(string reason)
    {
        return new Measurement1DResult
        {
            Judgment = InspectionJudgment.NG,
            FailureReason = reason
        };
    }
}
