using System.Diagnostics;
using OpenCvSharp;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.Services;

public sealed class InspectionTaskExecutionService
{
    private readonly IClipClassificationService _clipClassificationService;
    private readonly ClipFrameImageMaterializer _clipFrameImageMaterializer;
    private readonly Measurement1DService _measurementService;

    public InspectionTaskExecutionService(
        IClipClassificationService clipClassificationService,
        ClipFrameImageMaterializer? clipFrameImageMaterializer = null,
        Measurement1DService? measurementService = null)
    {
        _clipClassificationService = clipClassificationService;
        _clipFrameImageMaterializer = clipFrameImageMaterializer ?? new ClipFrameImageMaterializer();
        _measurementService = measurementService ?? new Measurement1DService();
    }

    public async ValueTask<InspectionTaskExecutionResult> ExecuteAsync(
        InspectionTaskDefinition task,
        Mat frame,
        CancellationToken cancellationToken = default)
    {
        return task.Kind switch
        {
            InspectionTaskKind.Classification => await ExecuteClassificationAsync(task, frame, cancellationToken),
            InspectionTaskKind.Measurement => ExecuteMeasurement(task, frame),
            _ => throw new NotSupportedException($"Unsupported inspection task kind: {task.Kind}")
        };
    }

    public async ValueTask<InspectionTaskExecutionResult> ExecuteImageAsync(
        InspectionTaskDefinition task,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        return task.Kind switch
        {
            InspectionTaskKind.Classification => await ExecuteClassificationImageAsync(task, imagePath, cancellationToken),
            InspectionTaskKind.Measurement => ExecuteMeasurementImage(task, imagePath),
            _ => throw new NotSupportedException($"Unsupported inspection task kind: {task.Kind}")
        };
    }

    private async ValueTask<InspectionTaskExecutionResult> ExecuteClassificationAsync(
        InspectionTaskDefinition task,
        Mat frame,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var vectorSet = task.EnsureClipVectorSet();
        var imagePath = _clipFrameImageMaterializer.SaveFrame(frame, vectorSet.VectorSetId, task.Roi);
        return await ExecuteClassificationImageCoreAsync(task, imagePath, stopwatch, cancellationToken);
    }

    private async ValueTask<InspectionTaskExecutionResult> ExecuteClassificationImageAsync(
        InspectionTaskDefinition task,
        string imagePath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        return await ExecuteClassificationImageCoreAsync(task, imagePath, stopwatch, cancellationToken);
    }

    private async ValueTask<InspectionTaskExecutionResult> ExecuteClassificationImageCoreAsync(
        InspectionTaskDefinition task,
        string imagePath,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var vectorSet = task.EnsureClipVectorSet();
        var result = await _clipClassificationService.ClassifyAsync(new ClipClassificationRequest
        {
            VectorSet = vectorSet,
            ImagePath = imagePath
        }, cancellationToken);
        stopwatch.Stop();
        var topOk = result.TopOk.FirstOrDefault();
        var topNg = result.TopNg.FirstOrDefault();

        return new InspectionTaskExecutionResult
        {
            TaskId = task.Id,
            TaskName = task.Name,
            Kind = task.Kind,
            Judgment = result.Judgment,
            Score = result.OkScore,
            Threshold = result.Threshold,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Detail =
                $"OK均值 {result.OkScore:0.0000}, NG均值 {(result.NgScore?.ToString("0.0000") ?? "--")}, " +
                $"Top1 OK {(topOk?.Similarity.ToString("0.0000") ?? "--")}, Top1 NG {(topNg?.Similarity.ToString("0.0000") ?? "--")}, TopK {result.TopK}",
            VectorSetId = result.VectorSetId,
            ImagePath = result.ImagePath,
            OkScore = result.OkScore,
            NgScore = result.NgScore,
            Margin = result.Margin,
            TopK = result.TopK,
            OkSampleCount = result.OkSampleCount,
            NgSampleCount = result.NgSampleCount,
            TopOkImagePath = topOk?.ImagePath,
            TopOkSimilarity = topOk?.Similarity,
            TopNgImagePath = topNg?.ImagePath,
            TopNgSimilarity = topNg?.Similarity
        };
    }

    private InspectionTaskExecutionResult ExecuteMeasurement(
        InspectionTaskDefinition task,
        Mat frame)
    {
        var stopwatch = Stopwatch.StartNew();
        using var crop = ClipFrameImageMaterializer.CropFrame(frame, task.Roi);
        return ExecuteMeasurementCore(task, crop, null, stopwatch);
    }

    private InspectionTaskExecutionResult ExecuteMeasurementImage(
        InspectionTaskDefinition task,
        string imagePath)
    {
        var stopwatch = Stopwatch.StartNew();
        using var crop = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (crop.Empty())
        {
            throw new InvalidOperationException($"测量图像读取失败：{imagePath}");
        }

        return ExecuteMeasurementCore(task, crop, imagePath, stopwatch);
    }

    private InspectionTaskExecutionResult ExecuteMeasurementCore(
        InspectionTaskDefinition task,
        Mat crop,
        string? imagePath,
        Stopwatch stopwatch)
    {
        var result = _measurementService.Measure(crop, task.EnsureMeasurementOptions());
        stopwatch.Stop();
        var detail = result.DistancePx > 0
            ? $"距离 {result.DistanceMm:0.00} mm / {result.DistancePx:0.00} px, E1 {result.FirstEdgeIndex:0.00}, E2 {result.SecondEdgeIndex:0.00}"
            : result.FailureReason;

        return new InspectionTaskExecutionResult
        {
            TaskId = task.Id,
            TaskName = task.Name,
            Kind = task.Kind,
            Judgment = result.Judgment,
            Score = (float?)result.DistanceMm,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Detail = detail,
            ImagePath = imagePath,
            DistancePx = result.DistancePx > 0 ? result.DistancePx : null,
            DistanceMm = result.DistanceMm > 0 ? result.DistanceMm : null,
            FirstEdgeIndex = result.DistancePx > 0 ? result.FirstEdgeIndex : null,
            SecondEdgeIndex = result.DistancePx > 0 ? result.SecondEdgeIndex : null,
            FailureReason = result.FailureReason
        };
    }
}
