namespace VisionWorkbench.Models.Inspection;

public sealed class CameraAlignmentDefinition
{
    public string ProductModelId { get; set; } = string.Empty;

    public string CameraId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string ReferenceImageRelativePath { get; set; } = string.Empty;

    public string TemplateRelativePath { get; set; } = string.Empty;

    public string PreviewRelativePath { get; set; } = string.Empty;

    public string FeatureMethod { get; set; } = "Orb";

    public string TransformModel { get; set; } = "AffinePartial";

    public string AlignmentMode { get; set; } = "整图匹配";

    public string AlignmentMethod { get; set; } = "NCC+AKAZE";

    public int MaxLongSide { get; set; } = 1600;

    public int MaxFeatures { get; set; } = 5000;

    public string RegisteredFeatureMethod { get; set; } = string.Empty;

    public string RegisteredAlignmentMethod { get; set; } = string.Empty;

    public int RegisteredMaxLongSide { get; set; }

    public int RegisteredMaxFeatures { get; set; }

    public AlignmentEffectiveRegion? EffectiveAlignmentRegion { get; set; }

    public AlignmentEffectiveRegion? RegisteredEffectiveAlignmentRegion { get; set; }

    public double LoweRatio { get; set; } = 0.75;

    public int MinGoodMatches { get; set; } = 12;

    public int MinInliers { get; set; } = 8;

    public double MinInlierRatio { get; set; } = 0.30;

    public double RansacReprojectionThreshold { get; set; } = 3.0;

    public double MaxReprojectionRmse { get; set; } = 4.0;

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public int KeyPointCount { get; set; }

    public int DescriptorRows { get; set; }

    public int DescriptorCols { get; set; }

    public int DescriptorMatType { get; set; }

    public DateTimeOffset? RegisteredAt { get; set; }

    public bool IsEffectiveAlignmentRegionCurrent =>
        AlignmentEffectiveRegion.AreEquivalent(EffectiveAlignmentRegion, RegisteredEffectiveAlignmentRegion);

    public void NormalizeEffectiveAlignmentRegions()
    {
        EffectiveAlignmentRegion = AlignmentEffectiveRegion.NormalizeOrNull(
            EffectiveAlignmentRegion,
            ImageWidth,
            ImageHeight);
        RegisteredEffectiveAlignmentRegion = AlignmentEffectiveRegion.NormalizeOrNull(
            RegisteredEffectiveAlignmentRegion,
            ImageWidth,
            ImageHeight);
    }

    public void Clear()
    {
        ReferenceImageRelativePath = string.Empty;
        TemplateRelativePath = string.Empty;
        PreviewRelativePath = string.Empty;
        ImageWidth = 0;
        ImageHeight = 0;
        KeyPointCount = 0;
        DescriptorRows = 0;
        DescriptorCols = 0;
        DescriptorMatType = 0;
        RegisteredAt = null;
        RegisteredFeatureMethod = string.Empty;
        RegisteredMaxLongSide = 0;
        RegisteredMaxFeatures = 0;
        EffectiveAlignmentRegion = null;
        RegisteredEffectiveAlignmentRegion = null;
    }
}

public sealed class AlignmentEffectiveRegion
{
    public const double MinimumSize = 16;

    public double Left { get; set; }

    public double Top { get; set; }

    public double Right { get; set; }

    public double Bottom { get; set; }

    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public AlignmentEffectiveRegion Clone() => new()
    {
        Left = Left,
        Top = Top,
        Right = Right,
        Bottom = Bottom
    };

    public static AlignmentEffectiveRegion? NormalizeOrNull(
        AlignmentEffectiveRegion? region,
        int imageWidth,
        int imageHeight)
    {
        if (region == null || imageWidth <= 0 || imageHeight <= 0 ||
            !double.IsFinite(region.Left) || !double.IsFinite(region.Top) ||
            !double.IsFinite(region.Right) || !double.IsFinite(region.Bottom))
        {
            return null;
        }

        var left = Math.Clamp(Math.Min(region.Left, region.Right), 0, imageWidth);
        var top = Math.Clamp(Math.Min(region.Top, region.Bottom), 0, imageHeight);
        var right = Math.Clamp(Math.Max(region.Left, region.Right), 0, imageWidth);
        var bottom = Math.Clamp(Math.Max(region.Top, region.Bottom), 0, imageHeight);
        if (right - left < MinimumSize || bottom - top < MinimumSize)
        {
            return null;
        }

        return new AlignmentEffectiveRegion
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
    }

    public static bool AreEquivalent(
        AlignmentEffectiveRegion? left,
        AlignmentEffectiveRegion? right,
        double tolerance = 0.01)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }

        return Math.Abs(left.Left - right.Left) <= tolerance &&
               Math.Abs(left.Top - right.Top) <= tolerance &&
               Math.Abs(left.Right - right.Right) <= tolerance &&
               Math.Abs(left.Bottom - right.Bottom) <= tolerance;
    }
}
