using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace RoiAlignment.Core;

public enum FeatureMethod
{
    Sift,
    Akaze,
    Orb
}

public enum TransformModel
{
    AffinePartial,
    Affine,
    Homography
}

public enum RoiKind
{
    Rectangle,
    RotatedRectangle,
    Polygon
}

public enum AlignmentFailureReason
{
    None,
    EmptyTemplate,
    NoRuntimeFeatures,
    NotEnoughMatches,
    TransformEstimationFailed,
    NotEnoughInliers,
    InlierRatioTooLow,
    ReprojectionErrorTooHigh,
    RoiTransformInvalid,
    UnsupportedFeatureMethod,
    UnsupportedTransformModel
}

public readonly record struct Point2fDto(double X, double Y)
{
    public Point2f ToOpenCv() => new((float)X, (float)Y);

    public static Point2fDto FromOpenCv(Point2f point) => new(point.X, point.Y);
}

public readonly record struct Xywha(
    double X,
    double Y,
    double Width,
    double Height,
    double AngleDegrees);

public sealed class KeyPointDto
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Size { get; init; }
    public double Angle { get; init; }
    public double Response { get; init; }
    public int Octave { get; init; }
    public int ClassId { get; init; }

    public Point2fDto Point => new(X, Y);

    public static KeyPointDto FromOpenCv(KeyPoint keyPoint) => new()
    {
        X = keyPoint.Pt.X,
        Y = keyPoint.Pt.Y,
        Size = keyPoint.Size,
        Angle = keyPoint.Angle,
        Response = keyPoint.Response,
        Octave = keyPoint.Octave,
        ClassId = keyPoint.ClassId
    };

    public KeyPoint ToOpenCv() => new(
        (float)X,
        (float)Y,
        (float)Size,
        (float)Angle,
        (float)Response,
        Octave,
        ClassId);
}

public sealed class DescriptorData
{
    public static DescriptorData Empty { get; } = new();

    public int Rows { get; init; }
    public int Cols { get; init; }
    public int MatType { get; init; }
    public string DataBase64 { get; init; } = "";

    [JsonIgnore]
    public bool IsEmpty => Rows <= 0 || Cols <= 0 || string.IsNullOrEmpty(DataBase64);
}

public sealed class TransformData
{
    public TransformModel Model { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public double[] Values { get; init; } = [];

    public static TransformData FromMat(TransformModel model, Mat transform)
    {
        using var transform64 = new Mat();
        transform.ConvertTo(transform64, MatType.CV_64FC1);

        var values = new double[transform64.Rows * transform64.Cols];
        var index = 0;
        for (var row = 0; row < transform64.Rows; row++)
        {
            for (var col = 0; col < transform64.Cols; col++)
            {
                values[index++] = transform64.At<double>(row, col);
            }
        }

        return new TransformData
        {
            Model = model,
            Rows = transform64.Rows,
            Cols = transform64.Cols,
            Values = values
        };
    }
}

public sealed class TemplateMetadata
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Notes { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
}

public sealed class AlignmentTemplate
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "alignment-template";
    public string? Name { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public double ProcessingScale { get; init; } = 1.0;
    public int MaxLongSide { get; init; }
    public int MaxFeatures { get; init; }
    public FeatureMethod FeatureMethod { get; init; }
    public TransformModel TransformModel { get; init; }
    public IReadOnlyList<KeyPointDto> KeyPoints { get; init; } = [];
    public DescriptorData Descriptors { get; init; } = DescriptorData.Empty;
    public TemplateMetadata Metadata { get; init; } = new();

    [JsonIgnore]
    public bool IsEmpty => KeyPoints.Count == 0 || Descriptors.IsEmpty;

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static AlignmentTemplate Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AlignmentTemplate>(json, JsonOptions)
            ?? throw new InvalidOperationException("Alignment template file is empty or invalid.");
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public sealed class RoiAlignmentProject
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "roi-alignment-project";
    public AlignmentTemplate Template { get; init; } = new();
    public IReadOnlyList<RoiShape> Rois { get; init; } = [];
    public TemplateMetadata Metadata { get; init; } = new();

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, AlignmentTemplate.JsonOptions);
        File.WriteAllText(path, json);
    }

    public static RoiAlignmentProject Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RoiAlignmentProject>(json, AlignmentTemplate.JsonOptions)
            ?? throw new InvalidOperationException("ROI alignment project file is empty or invalid.");
    }
}

public sealed class AlignmentOptions
{
    public FeatureMethod FeatureMethod { get; init; } = FeatureMethod.Sift;
    public TransformModel TransformModel { get; init; } = TransformModel.AffinePartial;
    public int MaxLongSide { get; init; } = 1600;
    public int MaxFeatures { get; init; } = 5000;
    public double LoweRatio { get; init; } = 0.75;
    public int MinGoodMatches { get; init; } = 12;
    public int MinInliers { get; init; } = 8;
    public double MinInlierRatio { get; init; } = 0.30;
    public double MaxReprojectionRmse { get; init; } = 4.0;
    public double RansacReprojectionThreshold { get; init; } = 3.0;
}

public sealed record AlignmentTiming
{
    public TimeSpan RuntimeFeatureExtraction { get; init; }
    public TimeSpan Matching { get; init; }
    public TimeSpan TransformEstimation { get; init; }
    public TimeSpan RoiTransform { get; init; }
    public TimeSpan Total { get; init; }
}

public sealed class RoiShape
{
    public string Name { get; init; } = "";
    public RoiKind Kind { get; init; }
    public IReadOnlyList<Point2fDto> Points { get; init; } = [];
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    [JsonIgnore]
    public Xywha Bounds => ToXywha();

    public static RoiShape FromXywha(string name, Xywha xywha) => new()
    {
        Name = name,
        Kind = RoiKind.RotatedRectangle,
        Points = RoiGeometry.FromXywha(xywha)
    };

    public Xywha ToXywha() => RoiGeometry.ToXywha(Points);
}
