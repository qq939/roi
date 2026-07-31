namespace VisionWorkbench.Models.Inspection;

public sealed class ClipVectorSetDefinition
{
    public const float DefaultThreshold = 0.94f;
    public const float MinimumThreshold = 0.001f;

    public string VectorSetId { get; set; } = string.Empty;

    public string ProductModelId { get; set; } = string.Empty;

    public string CameraId { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string BackboneType { get; set; } = "CLIP";

    public int TopK { get; set; } = 3;

    public float Threshold { get; set; } = DefaultThreshold;

    public string ModelName { get; set; } = "ViT-B-32";

    public string Pretrained { get; set; } = "laion2b_s34b_b79k";

    public static string BuildId(string productModelId, string cameraId, string taskId)
    {
        return $"{Normalize(productModelId)}__{Normalize(cameraId)}__{Normalize(taskId)}";
    }

    public void EnsureId()
    {
        if (string.IsNullOrWhiteSpace(VectorSetId))
        {
            VectorSetId = BuildId(ProductModelId, CameraId, TaskId);
        }

        Normalize();
    }

    public void Normalize()
    {
        if (TopK < 1)
        {
            TopK = 1;
        }

        Threshold = NormalizeThreshold(Threshold);
    }

    public static float NormalizeThreshold(float threshold)
    {
        return threshold is >= MinimumThreshold and <= 1
            ? threshold
            : DefaultThreshold;
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().Replace(' ', '-').ToLowerInvariant();
    }
}
