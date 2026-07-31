using OpenCvSharp;

namespace RoiAlignment.Core;

public sealed class AlignmentTemplateBuilder
{
    private readonly Mat _referenceImage;
    private FeatureMethod _featureMethod = FeatureMethod.Sift;
    private TransformModel _transformModel = TransformModel.AffinePartial;
    private int _maxLongSide = 1600;
    private int _maxFeatures = 5000;
    private Mat? _registrationMask;
    private string? _name;
    private TemplateMetadata _metadata = new();

    private AlignmentTemplateBuilder(Mat referenceImage)
    {
        if (referenceImage.Empty())
        {
            throw new ArgumentException("Reference image cannot be empty.", nameof(referenceImage));
        }

        _referenceImage = referenceImage.Clone();
    }

    public static AlignmentTemplateBuilder FromImage(Mat referenceImage) => new(referenceImage);

    public AlignmentTemplateBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    public AlignmentTemplateBuilder UseSift()
    {
        _featureMethod = FeatureMethod.Sift;
        return this;
    }

    public AlignmentTemplateBuilder UseAkaze()
    {
        _featureMethod = FeatureMethod.Akaze;
        return this;
    }

    public AlignmentTemplateBuilder UseOrb()
    {
        _featureMethod = FeatureMethod.Orb;
        return this;
    }

    public AlignmentTemplateBuilder UseFeatureMethod(FeatureMethod method)
    {
        _featureMethod = method;
        return this;
    }

    public AlignmentTemplateBuilder WithMaxLongSide(int maxLongSide)
    {
        _maxLongSide = maxLongSide;
        return this;
    }

    public AlignmentTemplateBuilder WithMaxFeatures(int maxFeatures)
    {
        _maxFeatures = maxFeatures;
        return this;
    }

    public AlignmentTemplateBuilder UseAffinePartial()
    {
        _transformModel = TransformModel.AffinePartial;
        return this;
    }

    public AlignmentTemplateBuilder WithRegistrationMask(Mat? mask)
    {
        _registrationMask?.Dispose();
        _registrationMask = mask?.Clone();
        return this;
    }

    public AlignmentTemplateBuilder WithMetadata(TemplateMetadata metadata)
    {
        _metadata = metadata;
        return this;
    }

    public AlignmentTemplate Build()
    {
        using var processed = ImagePreprocessor.ResizeToMaxLongSide(_referenceImage, _maxLongSide, out var scale);
        using var gray = ImagePreprocessor.ToGray(processed);
        using var descriptors = new Mat();
        using var scaledMask = ScaleMask(_registrationMask, processed.Size());
        var keyPoints = FeatureExtractor.Extract(_featureMethod, gray, scaledMask, descriptors, _maxFeatures);

        return new AlignmentTemplate
        {
            Name = _name,
            ImageWidth = _referenceImage.Width,
            ImageHeight = _referenceImage.Height,
            ProcessingScale = scale,
            MaxLongSide = _maxLongSide,
            MaxFeatures = _maxFeatures,
            FeatureMethod = _featureMethod,
            TransformModel = _transformModel,
            KeyPoints = keyPoints.Select(KeyPointDto.FromOpenCv).ToArray(),
            Descriptors = OpenCvMatData.FromMat(descriptors),
            Metadata = _metadata
        };
    }

    private static Mat? ScaleMask(Mat? mask, Size targetSize)
    {
        if (mask == null || mask.Empty())
        {
            return null;
        }

        if (mask.Size() == targetSize)
        {
            return mask.Clone();
        }

        var resized = new Mat();
        Cv2.Resize(mask, resized, targetSize, 0, 0, InterpolationFlags.Nearest);
        return resized;
    }
}

public sealed class RoiAlignmentProjectBuilder
{
    private readonly AlignmentTemplateBuilder _templateBuilder;
    private IReadOnlyList<RoiShape> _rois = [];
    private TemplateMetadata _metadata = new();

    private RoiAlignmentProjectBuilder(Mat referenceImage)
    {
        _templateBuilder = AlignmentTemplateBuilder.FromImage(referenceImage);
    }

    public static RoiAlignmentProjectBuilder FromImage(Mat referenceImage) => new(referenceImage);

    public RoiAlignmentProjectBuilder Named(string name)
    {
        _templateBuilder.Named(name);
        return this;
    }

    public RoiAlignmentProjectBuilder UseSift()
    {
        _templateBuilder.UseSift();
        return this;
    }

    public RoiAlignmentProjectBuilder UseAkaze()
    {
        _templateBuilder.UseAkaze();
        return this;
    }

    public RoiAlignmentProjectBuilder UseOrb()
    {
        _templateBuilder.UseOrb();
        return this;
    }

    public RoiAlignmentProjectBuilder UseFeatureMethod(FeatureMethod method)
    {
        _templateBuilder.UseFeatureMethod(method);
        return this;
    }

    public RoiAlignmentProjectBuilder WithMaxLongSide(int maxLongSide)
    {
        _templateBuilder.WithMaxLongSide(maxLongSide);
        return this;
    }

    public RoiAlignmentProjectBuilder WithMaxFeatures(int maxFeatures)
    {
        _templateBuilder.WithMaxFeatures(maxFeatures);
        return this;
    }

    public RoiAlignmentProjectBuilder UseAffinePartial()
    {
        _templateBuilder.UseAffinePartial();
        return this;
    }

    public RoiAlignmentProjectBuilder WithRegistrationMask(Mat? mask)
    {
        _templateBuilder.WithRegistrationMask(mask);
        return this;
    }

    public RoiAlignmentProjectBuilder WithRois(IReadOnlyList<RoiShape> rois)
    {
        _rois = rois;
        return this;
    }

    public RoiAlignmentProjectBuilder WithMetadata(TemplateMetadata metadata)
    {
        _metadata = metadata;
        return this;
    }

    public RoiAlignmentProject Build() => new()
    {
        Template = _templateBuilder.Build(),
        Rois = _rois,
        Metadata = _metadata
    };
}
