using System.IO;

namespace VisionWorkbench.Services;

public sealed class VisionAssetPathService
{
    private readonly VisionRuntimePaths _runtimePaths;

    public VisionAssetPathService(VisionRuntimePaths runtimePaths)
    {
        _runtimePaths = runtimePaths;
    }

    public string GetRootFullPath()
    {
        return _runtimePaths.RootDirectory;
    }

    public string GetFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(GetRootFullPath(), relativePath));
    }

    public string GetRelativePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        var root = GetRootFullPath();
        var path = Path.GetFullPath(fullPath);
        return Path.GetRelativePath(root, path);
    }

    public string GetProductCameraDirectory(string productModelId, string cameraId)
    {
        return Path.Combine(
            GetProductDirectory(productModelId),
            SanitizePathSegment(cameraId),
            "alignment");
    }

    public string GetProductDirectory(string productModelId)
    {
        return Path.Combine(GetRootFullPath(), GetProductRelativeDirectory(productModelId));
    }

    public string GetProductRelativeDirectory(string productModelId)
    {
        return Path.Combine("Products", SanitizePathSegment(productModelId));
    }

    public string GetReferenceImageRelativePath(string productModelId, string cameraId)
    {
        return Path.Combine("Products", SanitizePathSegment(productModelId), SanitizePathSegment(cameraId), "alignment", "reference.png");
    }

    public string GetTemplateRelativePath(string productModelId, string cameraId)
    {
        return Path.Combine("Products", SanitizePathSegment(productModelId), SanitizePathSegment(cameraId), "alignment", "template.align.json");
    }

    public string GetAlignmentTemplateDatabasePath()
    {
        return _runtimePaths.AlignmentTemplateDatabasePath;
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
}
