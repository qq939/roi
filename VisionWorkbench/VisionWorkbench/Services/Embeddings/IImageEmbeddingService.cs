namespace VisionWorkbench.Services.Embeddings;

public interface IImageEmbeddingService : IDisposable
{
    string BackboneType { get; }

    string ModelName { get; }

    string Pretrained { get; }

    ValueTask<float[]> EncodeImageAsync(string imagePath, CancellationToken cancellationToken = default);
}
