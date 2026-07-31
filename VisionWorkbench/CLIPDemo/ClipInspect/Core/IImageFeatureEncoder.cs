namespace ClipInspect.Core;

public interface IImageFeatureEncoder
{
    ValueTask<float[]> EncodeImageAsync(string imagePath, CancellationToken cancellationToken = default);
}
