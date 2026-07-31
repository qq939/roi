namespace VisionWorkbench.Services.Clip;

public interface IClipClassificationService : IDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<ClipVectorSetBuildResult> BuildVectorSetAsync(
        ClipBuildVectorSetRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<int> AddSamplesAsync(
        ClipSampleMaintenanceRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ClipClassificationResult> ClassifyAsync(
        ClipClassificationRequest request,
        CancellationToken cancellationToken = default);
}
