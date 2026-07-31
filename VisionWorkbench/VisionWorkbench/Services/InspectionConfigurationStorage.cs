using System.IO;
using System.Text.Json;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class InspectionConfigurationStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; }

    public InspectionConfigurationStorage(string? filePath = null)
    {
        FilePath = filePath ?? new VisionRuntimePaths().InspectionConfigurationPath;
    }

    public InspectionWorkspaceConfiguration Load()
    {
        if (!File.Exists(FilePath))
        {
            return CreateDefault();
        }

        var json = File.ReadAllText(FilePath);
        var configuration = JsonSerializer.Deserialize<InspectionWorkspaceConfiguration>(json, SerializerOptions);
        if (!JsonHasProperty(json, nameof(InspectionWorkspaceConfiguration.SchemaVersion)) &&
            configuration != null)
        {
            configuration.SchemaVersion = 0;
        }

        return Normalize(configuration);
    }

    public void Save(InspectionWorkspaceConfiguration configuration)
    {
        var normalized = Normalize(configuration);
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(normalized, SerializerOptions));
    }

    private static InspectionWorkspaceConfiguration CreateDefault()
    {
        var product = new ProductModelDefinition();
        var tasks = Enumerable.Range(1, 6)
            .Select(index =>
            {
                var task = new InspectionTaskDefinition
                {
                    Id = $"clip-presence-cam-{index:00}",
                    Name = $"CAM {index:00} 分类任务",
                    ProductModelId = product.Id,
                    CameraId = $"CAM {index:00}",
                    Kind = InspectionTaskKind.Classification
                };
                task.EnsureClipVectorSet();
                return task;
            })
            .ToList();

        return new InspectionWorkspaceConfiguration
        {
            SchemaVersion = InspectionWorkspaceConfiguration.CurrentSchemaVersion,
            SelectedProductModelId = product.Id,
            ProductModels = [product],
            Tasks = tasks
        };
    }

    private static InspectionWorkspaceConfiguration Normalize(InspectionWorkspaceConfiguration? configuration)
    {
        configuration ??= CreateDefault();
        configuration.ProductModels ??= [];
        configuration.Alignments ??= [];
        configuration.Tasks ??= [];
        if (string.IsNullOrWhiteSpace(configuration.ImageArchiveRootDirectory))
        {
            configuration.ImageArchiveRootDirectory = InspectionWorkspaceConfiguration.DefaultImageArchiveRootDirectory;
        }

        configuration.SecondaryBoard ??= new SecondaryBoardSettings();
        configuration.SecondaryBoard.Normalize();
        configuration.RoiOverlay ??= new RoiOverlaySettings();
        configuration.RoiOverlay.Normalize();

        if (configuration.SchemaVersion < InspectionWorkspaceConfiguration.CurrentSchemaVersion)
        {
            MigrateConfiguration(configuration);
        }
        else
        {
            configuration.SchemaVersion = InspectionWorkspaceConfiguration.CurrentSchemaVersion;
        }

        if (configuration.ProductModels.Count == 0)
        {
            configuration.ProductModels.Add(new ProductModelDefinition());
        }

        if (string.IsNullOrWhiteSpace(configuration.SelectedProductModelId) ||
            configuration.ProductModels.All(product =>
                !string.Equals(product.Id, configuration.SelectedProductModelId, StringComparison.OrdinalIgnoreCase)))
        {
            configuration.SelectedProductModelId = configuration.ProductModels[0].Id;
        }

        foreach (var task in configuration.Tasks)
        {
            task.Roi ??= new RoiRegion();
            task.EnsureMeasurementOptions();
        }

        foreach (var task in configuration.Tasks.Where(task => task.Kind == InspectionTaskKind.Classification))
        {
            task.EnsureClipVectorSet();
        }

        foreach (var alignment in configuration.Alignments)
        {
            alignment.AlignmentMode = string.IsNullOrWhiteSpace(alignment.AlignmentMode) ? "整图匹配" : alignment.AlignmentMode;
            alignment.AlignmentMethod = string.IsNullOrWhiteSpace(alignment.AlignmentMethod) ? "NCC+AKAZE" : alignment.AlignmentMethod;
            alignment.FeatureMethod = string.IsNullOrWhiteSpace(alignment.FeatureMethod) ? "Orb" : alignment.FeatureMethod;
            alignment.TransformModel = string.IsNullOrWhiteSpace(alignment.TransformModel) ? "AffinePartial" : alignment.TransformModel;
            alignment.MaxLongSide = alignment.MaxLongSide <= 0 ? 1600 : alignment.MaxLongSide;
            alignment.MaxFeatures = alignment.MaxFeatures <= 0 ? 5000 : alignment.MaxFeatures;
            if (alignment.DescriptorRows > 0 && alignment.DescriptorCols > 0)
            {
                alignment.RegisteredFeatureMethod = string.IsNullOrWhiteSpace(alignment.RegisteredFeatureMethod)
                    ? alignment.FeatureMethod
                    : alignment.RegisteredFeatureMethod;
                alignment.RegisteredMaxLongSide = alignment.RegisteredMaxLongSide <= 0
                    ? alignment.MaxLongSide
                    : alignment.RegisteredMaxLongSide;
                alignment.RegisteredMaxFeatures = alignment.RegisteredMaxFeatures <= 0
                    ? alignment.MaxFeatures
                    : alignment.RegisteredMaxFeatures;
            }

            alignment.LoweRatio = alignment.LoweRatio <= 0 ? 0.75 : alignment.LoweRatio;
            alignment.MinGoodMatches = alignment.MinGoodMatches <= 0 ? 12 : alignment.MinGoodMatches;
            alignment.MinInliers = alignment.MinInliers <= 0 ? 8 : alignment.MinInliers;
            alignment.MinInlierRatio = alignment.MinInlierRatio <= 0 ? 0.30 : alignment.MinInlierRatio;
            alignment.RansacReprojectionThreshold = alignment.RansacReprojectionThreshold <= 0 ? 3.0 : alignment.RansacReprojectionThreshold;
            alignment.MaxReprojectionRmse = alignment.MaxReprojectionRmse <= 0 ? 4.0 : alignment.MaxReprojectionRmse;
            alignment.NormalizeEffectiveAlignmentRegions();
        }

        return configuration;
    }

    private static void MigrateConfiguration(InspectionWorkspaceConfiguration configuration)
    {
        if (configuration.SchemaVersion < 2)
        {
            foreach (var task in configuration.Tasks)
            {
                if (task.Roi == null || task.Roi.IsFullImage)
                {
                    continue;
                }

                task.Roi.X += task.Roi.Width / 2.0;
                task.Roi.Y += task.Roi.Height / 2.0;
            }
        }

        if (configuration.SchemaVersion < 4)
        {
            foreach (var task in configuration.Tasks)
            {
                task.Measurement ??= new MeasurementOptions();
                var measurement = task.Measurement;
                var scale = measurement.PixelToMillimeterScale <= 0 ? 1 : measurement.PixelToMillimeterScale;
                measurement.PixelToMillimeterScale = scale;
                measurement.MinDistanceMm = Math.Max(0, measurement.MinDistancePx * scale);
                measurement.MaxDistanceMm = measurement.MaxDistancePx > 0
                    ? Math.Max(measurement.MinDistanceMm, measurement.MaxDistancePx * scale)
                    : Math.Max(measurement.MinDistanceMm, 100_000);
            }
        }

        if (configuration.SchemaVersion < 6)
        {
            configuration.AutoStartInspection = true;
        }

        configuration.SecondaryBoard ??= new SecondaryBoardSettings();
        configuration.SecondaryBoard.Normalize();
        configuration.RoiOverlay ??= new RoiOverlaySettings();
        configuration.RoiOverlay.Normalize();
        configuration.SchemaVersion = InspectionWorkspaceConfiguration.CurrentSchemaVersion;
    }

    private static bool JsonHasProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty(propertyName, out _);
    }
}
