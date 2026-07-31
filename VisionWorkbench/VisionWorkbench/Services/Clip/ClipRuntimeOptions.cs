using VisionWorkbench.Services;

namespace VisionWorkbench.Services.Clip;

public sealed class ClipRuntimeOptions
{
    public const string ModelFileName = "clip_vit_b32_image.onnx";

    public string ModelPath { get; init; } = new VisionRuntimePaths().ClipModelPath;

    public string DatabasePath { get; init; } = new VisionRuntimePaths().ClipVectorDatabasePath;

    public static ClipRuntimeOptions FromRuntimePaths(VisionRuntimePaths paths)
    {
        return new ClipRuntimeOptions
        {
            ModelPath = paths.ClipModelPath,
            DatabasePath = paths.ClipVectorDatabasePath
        };
    }

    public static IReadOnlyList<string> GetDefaultModelPathCandidates()
    {
        return new VisionRuntimePaths().GetClipModelPathCandidates();
    }
}
