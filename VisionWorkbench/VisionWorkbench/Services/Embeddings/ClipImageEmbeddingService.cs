using ClipInspect.Onnx;

namespace VisionWorkbench.Services.Embeddings;

public sealed class ClipImageEmbeddingService : IImageEmbeddingService
{
    private readonly OnnxClipImageEncoder _encoder;

    public ClipImageEmbeddingService(
        string modelPath,
        string modelName = "ViT-B-32",
        string pretrained = "laion2b_s34b_b79k")
    {
        ModelName = modelName;
        Pretrained = pretrained;
        _encoder = new OnnxClipImageEncoder(modelPath);
    }

    public string BackboneType => "CLIP";

    public string ModelName { get; }

    public string Pretrained { get; }

    public ValueTask<float[]> EncodeImageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        return _encoder.EncodeImageAsync(imagePath, cancellationToken);
    }

    public void Dispose()
    {
        _encoder.Dispose();
    }
}
