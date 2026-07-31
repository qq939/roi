using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public sealed class CameraTrainingLibraryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly VisionAssetPathService _assetPathService;

    public CameraTrainingLibraryStore(VisionAssetPathService assetPathService)
    {
        _assetPathService = assetPathService;
    }

    public async ValueTask<CameraTrainingLibraryDocument> LoadAsync(
        string productModelId,
        string cameraId,
        IReadOnlyList<InspectionTaskDefinition> tasks,
        CancellationToken cancellationToken = default)
    {
        var path = GetAnnotationsPath(productModelId, cameraId);
        CameraTrainingLibraryDocument? document = null;
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            document = JsonSerializer.Deserialize<CameraTrainingLibraryDocument>(json, SerializerOptions);
        }

        document ??= new CameraTrainingLibraryDocument();
        var changed = Normalize(document, productModelId, cameraId, tasks);
        if (changed)
        {
            await SaveAsync(document, cancellationToken);
        }

        return document;
    }

    public async ValueTask SaveAsync(
        CameraTrainingLibraryDocument document,
        CancellationToken cancellationToken = default)
    {
        Normalize(document, document.ProductModelId, document.CameraId, []);
        var path = GetAnnotationsPath(document.ProductModelId, document.CameraId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public string GetTrainingDirectory(string productModelId, string cameraId)
    {
        return Path.Combine(
            _assetPathService.GetProductDirectory(productModelId),
            SanitizePathSegment(cameraId),
            "training");
    }

    public string GetAnnotationsPath(string productModelId, string cameraId)
    {
        return Path.Combine(GetTrainingDirectory(productModelId, cameraId), "annotations.json");
    }

    public string CreateRawImagePath(
        string productModelId,
        string cameraId,
        string imageId,
        string sourceName)
    {
        return CreateManagedImagePath(productModelId, cameraId, "raw", imageId, sourceName, ".png");
    }

    public string CreateAlignedImagePath(
        string productModelId,
        string cameraId,
        string imageId,
        string sourceName)
    {
        return CreateManagedImagePath(productModelId, cameraId, "aligned", imageId, sourceName, ".png");
    }

    public string CreateCropImagePath(
        string productModelId,
        string cameraId,
        string taskId,
        TrainingLabelState state,
        string imageId,
        string sourceName)
    {
        var directory = Path.Combine(
            GetTrainingDirectory(productModelId, cameraId),
            "crops",
            SanitizePathSegment(taskId),
            state.ToString());
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = imageId;
        }

        var shortId = imageId.Length > 8 ? imageId[..8] : imageId;
        var fileName = $"{SanitizeFileName(baseName)}_{state}_{SanitizeFileName(shortId)}.jpg";
        return Path.Combine(directory, fileName);
    }

    public string GetTaskCropDirectory(string productModelId, string cameraId, string taskId)
    {
        return Path.Combine(
            GetTrainingDirectory(productModelId, cameraId),
            "crops",
            SanitizePathSegment(taskId));
    }

    public string GetFullPath(string relativePath)
    {
        return _assetPathService.GetFullPath(relativePath);
    }

    public string GetRelativePath(string fullPath)
    {
        return _assetPathService.GetRelativePath(fullPath);
    }

    public static TrainingTaskLabel EnsureLabel(
        TrainingImageRecord record,
        string taskId)
    {
        record.Labels ??= [];
        var label = record.Labels.FirstOrDefault(item =>
            string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        if (label != null)
        {
            return label;
        }

        label = new TrainingTaskLabel
        {
            TaskId = taskId,
            State = TrainingLabelState.Unlabeled,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        record.Labels.Add(label);
        return label;
    }

    private string CreateManagedImagePath(
        string productModelId,
        string cameraId,
        string folder,
        string imageId,
        string sourceName,
        string extension)
    {
        var directory = Path.Combine(GetTrainingDirectory(productModelId, cameraId), folder);
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = folder;
        }

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SanitizeFileName(baseName)}_{SanitizeFileName(imageId)}{extension}";
        return Path.Combine(directory, fileName);
    }

    private static bool Normalize(
        CameraTrainingLibraryDocument document,
        string productModelId,
        string cameraId,
        IReadOnlyList<InspectionTaskDefinition> tasks)
    {
        var changed = false;
        if (!string.Equals(document.ProductModelId, productModelId, StringComparison.Ordinal))
        {
            document.ProductModelId = productModelId;
            changed = true;
        }

        if (!string.Equals(document.CameraId, cameraId, StringComparison.Ordinal))
        {
            document.CameraId = cameraId;
            changed = true;
        }

        if (document.SchemaVersion <= 0)
        {
            document.SchemaVersion = 1;
            changed = true;
        }

        document.Images ??= [];
        foreach (var record in document.Images)
        {
            record.ProductModelId = string.IsNullOrWhiteSpace(record.ProductModelId)
                ? productModelId
                : record.ProductModelId;
            record.CameraId = string.IsNullOrWhiteSpace(record.CameraId)
                ? cameraId
                : record.CameraId;
            record.Labels ??= [];
            foreach (var task in tasks)
            {
                var before = record.Labels.Count;
                EnsureLabel(record, task.Id);
                changed |= record.Labels.Count != before;
            }
        }

        return changed;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "image";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
