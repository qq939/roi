using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace RoiAlignment.Core;

internal static class FeatureExtractor
{
    public static KeyPoint[] Extract(
        FeatureMethod method,
        Mat grayImage,
        Mat? mask,
        Mat descriptors,
        int maxFeatures = 0)
    {
        return method switch
        {
            FeatureMethod.Sift => ExtractSift(grayImage, mask, descriptors, maxFeatures),
            FeatureMethod.Akaze => ExtractAkaze(grayImage, mask, descriptors, maxFeatures),
            FeatureMethod.Orb => ExtractOrb(grayImage, mask, descriptors, maxFeatures),
            _ => throw new NotSupportedException($"Unsupported feature method: {method}")
        };
    }

    public static NormTypes GetMatcherNorm(FeatureMethod method) => method switch
    {
        FeatureMethod.Sift => NormTypes.L2,
        FeatureMethod.Akaze => NormTypes.Hamming,
        FeatureMethod.Orb => NormTypes.Hamming,
        _ => throw new NotSupportedException($"Unsupported feature method: {method}")
    };

    private static KeyPoint[] ExtractSift(Mat grayImage, Mat? mask, Mat descriptors, int maxFeatures)
    {
        using var sift = maxFeatures > 0 ? SIFT.Create(nFeatures: maxFeatures) : SIFT.Create();
        sift.DetectAndCompute(grayImage, mask ?? new Mat(), out var keyPoints, descriptors);
        return keyPoints;
    }

    private static KeyPoint[] ExtractAkaze(Mat grayImage, Mat? mask, Mat descriptors, int maxFeatures)
    {
        using var akaze = AKAZE.Create();
        akaze.DetectAndCompute(grayImage, mask ?? new Mat(), out var keyPoints, descriptors);
        return LimitFeatures(keyPoints, descriptors, maxFeatures);
    }

    private static KeyPoint[] ExtractOrb(Mat grayImage, Mat? mask, Mat descriptors, int maxFeatures)
    {
        using var orb = maxFeatures > 0 ? ORB.Create(nFeatures: maxFeatures) : ORB.Create();
        orb.DetectAndCompute(grayImage, mask ?? new Mat(), out var keyPoints, descriptors);
        return keyPoints;
    }

    private static KeyPoint[] LimitFeatures(KeyPoint[] keyPoints, Mat descriptors, int maxFeatures)
    {
        if (maxFeatures <= 0 || keyPoints.Length <= maxFeatures || descriptors.Empty())
        {
            return keyPoints;
        }

        var selected = keyPoints
            .Select((keyPoint, index) => new { KeyPoint = keyPoint, Index = index })
            .OrderByDescending(item => item.KeyPoint.Response)
            .Take(maxFeatures)
            .ToArray();
        var limitedDescriptors = new Mat(selected.Length, descriptors.Cols, descriptors.Type());
        for (var row = 0; row < selected.Length; row++)
        {
            descriptors.Row(selected[row].Index).CopyTo(limitedDescriptors.Row(row));
        }

        limitedDescriptors.CopyTo(descriptors);
        limitedDescriptors.Dispose();
        return selected.Select(item => item.KeyPoint).ToArray();
    }
}

internal static class ImagePreprocessor
{
    public static Mat ResizeToMaxLongSide(Mat image, int maxLongSide, out double scale)
    {
        scale = 1.0;
        if (image.Empty())
        {
            return new Mat();
        }

        if (maxLongSide <= 0)
        {
            return image.Clone();
        }

        var longSide = Math.Max(image.Width, image.Height);
        if (longSide <= maxLongSide)
        {
            return image.Clone();
        }

        scale = (double)maxLongSide / longSide;
        var resized = new Mat();
        Cv2.Resize(
            image,
            resized,
            new Size(
                Math.Max(1, (int)Math.Round(image.Width * scale)),
                Math.Max(1, (int)Math.Round(image.Height * scale))),
            0,
            0,
            InterpolationFlags.Linear);
        return resized;
    }

    public static Mat ToGray(Mat image)
    {
        if (image.Empty())
        {
            return new Mat();
        }

        if (image.Channels() == 1)
        {
            return image.Clone();
        }

        var gray = new Mat();
        var code = image.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY
            : ColorConversionCodes.BGR2GRAY;
        Cv2.CvtColor(image, gray, code);
        return gray;
    }
}
