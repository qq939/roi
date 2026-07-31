namespace ClipInspect.Storage;

public interface ICacheStore
{
    ValueTask<ClipCache> LoadAsync(string cachePath, CancellationToken cancellationToken = default);
    ValueTask SaveAsync(string cachePath, ClipCache cache, CancellationToken cancellationToken = default);
}
