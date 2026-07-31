using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenCvSharp;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.Services;

public enum InspectionTriggerSource
{
    Manual,
    Di
}

public sealed class InspectionCycleCallbacks
{
    public required Action<string, string> Log { get; init; }

    public required Action<string> SetAlarm { get; init; }

    public required Action ClearAlarm { get; init; }

    public Action<CameraViewModel>? SelectCamera { get; init; }

    public Func<CancellationToken, Task>? RefreshUiBeforeTaskAsync { get; init; }

    public Action? ResultsChanged { get; init; }

    public Action? SummaryChanged { get; init; }
}

public sealed record InspectionCycleRequest(
    InspectionTriggerSource Source,
    bool IsRunning,
    string ProductCode,
    string SerialNumber);

public sealed class InspectionCycleService
{
    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly ObservableCollection<CameraViewModel> _cameras;
    private readonly ObservableCollection<InspectionResultRow> _results;
    private readonly CameraAcquisitionService _cameraService;
    private readonly InspectionTaskExecutionService _taskExecutionService;
    private readonly TaskImageAlignmentService _alignmentService;
    private readonly ClipTrainingLibraryService _clipTrainingLibraryService;
    private readonly InspectionImageArchiveService _imageArchiveService;
    private readonly InspectionResultStore _resultStore;
    private readonly Mt3aModbusTcpIoClient _ioModule;
    private readonly BarcodeScannerSerialPortService _barcodeScanner;
    private readonly InspectionCycleCallbacks _callbacks;
    private readonly S3UploadService? _s3UploadService;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private long _cycleSequence;

    public InspectionCycleService(
        InspectionWorkspaceConfiguration configuration,
        ObservableCollection<CameraViewModel> cameras,
        ObservableCollection<InspectionResultRow> results,
        CameraAcquisitionService cameraService,
        InspectionTaskExecutionService taskExecutionService,
        TaskImageAlignmentService alignmentService,
        ClipTrainingLibraryService clipTrainingLibraryService,
        InspectionImageArchiveService imageArchiveService,
        InspectionResultStore resultStore,
        Mt3aModbusTcpIoClient ioModule,
        BarcodeScannerSerialPortService barcodeScanner,
        InspectionCycleCallbacks callbacks,
        S3UploadService? s3UploadService = null)
    {
        _configuration = configuration;
        _cameras = cameras;
        _results = results;
        _cameraService = cameraService;
        _taskExecutionService = taskExecutionService;
        _alignmentService = alignmentService;
        _clipTrainingLibraryService = clipTrainingLibraryService;
        _imageArchiveService = imageArchiveService;
        _resultStore = resultStore;
        _ioModule = ioModule;
        _barcodeScanner = barcodeScanner;
        _callbacks = callbacks;
        _s3UploadService = s3UploadService;
    }

    public async Task RunAsync(InspectionCycleRequest request, CancellationToken cancellationToken)
    {
        var cycleId = Interlocked.Increment(ref _cycleSequence);
        var stopwatch = Stopwatch.StartNew();
        var sourceText = FormatTriggerSource(request.Source);
        Log("EVENT", $"收到{sourceText}触发，运行状态={request.IsRunning}");
        AppDiagnostics.Info("inspection", $"Cycle {cycleId} requested. Source={request.Source}, IsRunning={request.IsRunning}");

        if (!request.IsRunning)
        {
            SetAlarm("设备未启动，触发已跳过");
            Log("WARN", "检测跳过：设备未启动");
            AppDiagnostics.Info("inspection", $"Cycle {cycleId} skipped because system is stopped. Source={request.Source}");
            return;
        }

        if (request.Source == InspectionTriggerSource.Manual)
        {
            RecordManualTriggerOptionalDeviceStatus(cycleId);
        }

        if (!await _cycleLock.WaitAsync(0, cancellationToken))
        {
            SetAlarm("检测任务忙，触发已跳过");
            Log("WARN", "检测跳过：上一轮任务仍在执行");
            AppDiagnostics.Warn("inspection", $"Cycle {cycleId} skipped because another cycle is busy.");
            return;
        }

        try
        {
            _results.Clear();
            var productCode = request.ProductCode.Trim();
            if (string.IsNullOrWhiteSpace(productCode))
            {
                SetAlarm("成品号为空");
                Log("WARN", "检测跳过：成品号为空");
                AppDiagnostics.Warn("inspection", $"Cycle {cycleId} skipped because product code is empty.");
                return;
            }

            var productModel = ResolveProductModel(productCode);
            if (productModel == null)
            {
                SetAlarm($"未找到成品型号：{productCode}");
                Log("WARN", $"检测跳过：未找到成品型号 {productCode}");
                AppDiagnostics.Warn("inspection", $"Cycle {cycleId} skipped because product model was not found. ProductCode={productCode}");
                return;
            }

            _configuration.SelectedProductModelId = productModel.Id;
            var serialNumber = request.SerialNumber.Trim();
            var archiveTimestamp = DateTime.Now;
            var cameras = SelectInspectionCameras(cycleId);
            Log("EVENT", $"本轮检测相机：{cameras.Length} 个");
            if (cameras.Length == 0)
            {
                SetAlarm("没有启用并配置好的相机");
                Log("WARN", "检测跳过：没有启用并配置好的相机");
                AppDiagnostics.Warn("inspection", $"Cycle {cycleId} skipped because no cameras were selected.");
                return;
            }

            Log("INFO", $"检测开始。来源={sourceText}，成品号={productCode}，序列号={serialNumber}，相机={cameras.Length}");
            AppDiagnostics.Info(
                "inspection",
                $"Cycle {cycleId} started. Source={request.Source}, Product={productCode}, ModelId={productModel.Id}, Serial={serialNumber}, Cameras={string.Join(", ", cameras.Select(camera => camera.Name))}");

            var captures = new List<(CameraViewModel Camera, CameraCaptureResult Capture, string RawImagePath)>();
            var hasProcessingError = false;
            try
            {
                foreach (var camera in cameras)
                {
                    try
                    {
                        var capture = await CaptureCameraAsync(camera, cancellationToken);
                        var imagePath = await Task.Run(
                            () => _imageArchiveService.SaveCameraFrame(capture.Frame, productCode, serialNumber, camera.Name, archiveTimestamp, camera.BuildOriginalImageWatermarkOptions()),
                            cancellationToken);
                        Log("INFO", $"{camera.Name} 原图已保存");
                        AppDiagnostics.Info("inspection", $"Raw image archived. Camera={camera.Name}, Path={imagePath}");
                        captures.Add((camera, capture, imagePath));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hasProcessingError = true;
                        SetAlarm($"{camera.Name} 拍照失败：{ex.Message}");
                        AppDiagnostics.Warn(
                            "inspection",
                            $"Cycle {cycleId} continues after capture failure. Camera={camera.Name}, Error={ex.Message}");
                    }
                }

                if (captures.Count == 0)
                {
                    SetAlarm("所有相机拍照失败");
                    Log("WARN", "检测跳过：所有相机拍照失败");
                    AppDiagnostics.Warn("inspection", $"Cycle {cycleId} skipped task execution because all camera captures failed.");
                }
                else
                {
                    Log("INFO", "相机图像已刷新，开始执行检测任务");
                    AppDiagnostics.Info("inspection", $"Cycle {cycleId} camera frames refreshed before task execution.");
                    foreach (var item in captures)
                    {
                        Log("INFO", $"{item.Camera.Name} 任务执行开始");
                        var cameraTasksOk = await ExecuteCameraTasksAsync(
                            item.Camera,
                            item.Capture.Frame,
                            productModel,
                            productCode,
                            serialNumber,
                            item.RawImagePath,
                            cycleId,
                            archiveTimestamp,
                            cancellationToken);
                        hasProcessingError |= !cameraTasksOk;
                    }
                }

                if (!hasProcessingError)
                {
                    ClearAlarm();
                }
            }
            finally
            {
                foreach (var item in captures)
                {
                    item.Capture.Dispose();
                }
            }

            _callbacks.ResultsChanged?.Invoke();
            _callbacks.SummaryChanged?.Invoke();
            Log("INFO", "检测完成");
            AppDiagnostics.Info("inspection", $"Cycle {cycleId} finished. Source={request.Source}, ElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Warn("inspection", $"Cycle {cycleId} canceled. Source={request.Source}");
            throw;
        }
        catch (Exception ex)
        {
            SetAlarm($"检测失败：{ex.Message}");
            Log("WARN", $"检测失败：{ex.Message}");
            AppDiagnostics.Error("inspection", $"Cycle {cycleId} failed after {stopwatch.ElapsedMilliseconds} ms. Source={request.Source}", ex);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    public async Task RunSingleCameraFrameAsync(
        CameraViewModel camera,
        Mat frame,
        string productCode,
        string serialNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(frame);

        var cycleId = Interlocked.Increment(ref _cycleSequence);
        var stopwatch = Stopwatch.StartNew();
        Log("EVENT", $"{camera.Name} 单相机检测触发");
        AppDiagnostics.Info("inspection", $"Single camera cycle {cycleId} requested. Camera={camera.Name}, ProductCode={productCode}");

        if (!await _cycleLock.WaitAsync(0, cancellationToken))
        {
            SetAlarm("检测任务忙，单相机检测已跳过");
            Log("WARN", $"{camera.Name} 单相机检测跳过：上一轮任务仍在执行");
            AppDiagnostics.Warn("inspection", $"Single camera cycle {cycleId} skipped because another cycle is busy.");
            return;
        }

        try
        {
            _results.Clear();
            if (frame.Empty())
            {
                SetAlarm($"{camera.Name} 当前图像为空");
                Log("WARN", $"{camera.Name} 单相机检测跳过：当前图像为空");
                return;
            }

            var normalizedProductCode = productCode.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
            {
                SetAlarm("成品号为空");
                Log("WARN", $"{camera.Name} 单相机检测跳过：成品号为空");
                return;
            }

            var productModel = ResolveProductModel(normalizedProductCode);
            if (productModel == null)
            {
                SetAlarm($"未找到成品型号：{normalizedProductCode}");
                Log("WARN", $"{camera.Name} 单相机检测跳过：未找到成品型号 {normalizedProductCode}");
                return;
            }

            _configuration.SelectedProductModelId = productModel.Id;
            var archiveTimestamp = DateTime.Now;
            var rawImagePath = await Task.Run(
                () => _imageArchiveService.SaveCameraFrame(frame, normalizedProductCode, serialNumber, camera.Name, archiveTimestamp, camera.BuildOriginalImageWatermarkOptions()),
                cancellationToken);
            Log("INFO", $"{camera.Name} 当前图像已保存");

            var completed = await ExecuteCameraTasksAsync(
                camera,
                frame,
                productModel,
                normalizedProductCode,
                serialNumber.Trim(),
                rawImagePath,
                cycleId,
                archiveTimestamp,
                cancellationToken);

            _callbacks.ResultsChanged?.Invoke();
            _callbacks.SummaryChanged?.Invoke();
            if (completed)
            {
                ClearAlarm();
            }

            Log("INFO", $"{camera.Name} 单相机检测完成");
            AppDiagnostics.Info("inspection", $"Single camera cycle {cycleId} finished. Camera={camera.Name}, ElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Warn("inspection", $"Single camera cycle {cycleId} canceled. Camera={camera.Name}");
            throw;
        }
        catch (Exception ex)
        {
            SetAlarm($"{camera.Name} 单相机检测失败：{ex.Message}");
            Log("WARN", $"{camera.Name} 单相机检测失败：{ex.Message}");
            AppDiagnostics.Error("inspection", $"Single camera cycle {cycleId} failed after {stopwatch.ElapsedMilliseconds} ms. Camera={camera.Name}", ex);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private CameraViewModel[] SelectInspectionCameras(long cycleId)
    {
        var selected = new List<CameraViewModel>();
        foreach (var camera in _cameras)
        {
            var skipReason = GetInspectionCameraSkipReason(camera);
            var selectedText = skipReason == null ? "已选择" : $"跳过：{skipReason}";
            Log("EVENT", $"{camera.Name} {selectedText}");
            AppDiagnostics.Info("inspection", $"Cycle {cycleId} camera evaluation: {selectedText}. {FormatCameraForTrace(camera)}");
            if (skipReason == null)
            {
                selected.Add(camera);
            }
        }

        return selected.ToArray();
    }

    private static string? GetInspectionCameraSkipReason(CameraViewModel camera)
    {
        if (!camera.IsEnabled)
        {
            return "已停用";
        }

        if (!camera.HasExplicitAcquisitionTarget)
        {
            return "未配置设备";
        }

        return null;
    }

    private void RecordManualTriggerOptionalDeviceStatus(long cycleId)
    {
        if (!_ioModule.IsConnected)
        {
            Log("EVENT", $"手动触发忽略IO离线：{_ioModule.StatusText}");
            AppDiagnostics.Info("inspection", $"Cycle {cycleId} manual trigger ignored IO offline. Status={_ioModule.StatusText}");
        }

        if (!_barcodeScanner.IsConnected)
        {
            Log("EVENT", $"手动触发忽略扫码枪离线：{_barcodeScanner.StatusText}");
            AppDiagnostics.Info("inspection", $"Cycle {cycleId} manual trigger ignored scanner offline. Status={_barcodeScanner.StatusText}");
        }
    }

    private async Task<CameraCaptureResult> CaptureCameraAsync(
        CameraViewModel camera,
        CancellationToken cancellationToken)
    {
        camera.IsBusy = true;
        try
        {
            Log("INFO", $"{camera.Name} 开始拍照");
            AppDiagnostics.Info("inspection", $"Capture start. Camera={FormatCameraForTrace(camera)}");
            var capture = await CaptureWithReconnectAsync(camera, cancellationToken);
            _callbacks.SelectCamera?.Invoke(camera);

            camera.SetInspectionSourceFromCapture(capture.Image, capture.Frame, capture.DisplayName, capture.ReportedFps);
            camera.IsConnected = true;
            Log("INFO", $"{camera.Name} 拍照完成 {capture.Image.Width:0}x{capture.Image.Height:0}，正在刷新显示");
            AppDiagnostics.Info("inspection", $"Capture completed. Camera={camera.Name}, Display={capture.DisplayName}, Fps={capture.ReportedFps:0.##}");
            if (_callbacks.RefreshUiBeforeTaskAsync != null)
            {
                await _callbacks.RefreshUiBeforeTaskAsync(cancellationToken);
            }

            Log("INFO", $"{camera.Name} 显示已刷新");
            AppDiagnostics.Info("inspection", $"Display refreshed after capture. Camera={camera.Name}");
            return capture;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Warn("inspection", $"Capture canceled. Camera={camera.Name}");
            throw;
        }
        catch (Exception ex)
        {
            camera.IsConnected = false;
            _results.Add(new InspectionResultRow
            {
                CameraName = camera.Name,
                Item = "采集",
                Value = "失败",
                DetailText = ex.Message,
                Result = "NG"
            });
            Log("WARN", $"{camera.Name} 拍照失败：{ex.Message}");
            AppDiagnostics.Error("inspection", $"Capture failed. Camera={FormatCameraForTrace(camera)}", ex);
            throw;
        }
        finally
        {
            camera.IsBusy = false;
        }
    }

    private async Task<CameraCaptureResult> CaptureWithReconnectAsync(
        CameraViewModel camera,
        CancellationToken cancellationToken)
    {
        if (!camera.IsConnected)
        {
            await ConnectCameraForCaptureAsync(camera, "未连接，拍照前尝试连接", cancellationToken);
        }

        try
        {
            return await _cameraService.CaptureAsync(camera, cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("no active camera session", StringComparison.OrdinalIgnoreCase))
        {
            camera.IsConnected = false;
            Log("WARN", $"{camera.Name} 相机会话不存在，重新连接后重试拍照");
            AppDiagnostics.Warn("inspection", $"Camera session missing. Reconnecting before retry. Camera={FormatCameraForTrace(camera)}");
            await ConnectCameraForCaptureAsync(camera, "会话不存在，重新连接", cancellationToken);
            return await _cameraService.CaptureAsync(camera, cancellationToken);
        }
    }

    private async Task ConnectCameraForCaptureAsync(
        CameraViewModel camera,
        string reason,
        CancellationToken cancellationToken)
    {
        Log("INFO", $"{camera.Name} {reason}");
        AppDiagnostics.Info("inspection", $"Connecting camera before capture. Reason={reason}. {FormatCameraForTrace(camera)}");
        var result = await _cameraService.TryConnectAsync(camera, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message, result.Exception);
        }

        camera.IsConnected = true;
        camera.LastFrameInfo = "已连接";
        Log("INFO", $"{camera.Name} 已连接");
    }

    private async Task<bool> ExecuteCameraTasksAsync(
        CameraViewModel camera,
        Mat frame,
        ProductModelDefinition productModel,
        string archiveProductCode,
        string serialNumber,
        string rawImagePath,
        long cycleId,
        DateTime archiveTimestamp,
        CancellationToken cancellationToken)
    {
        var productModelId = productModel.Id;
        var tasks = _configuration.Tasks
            .Where(task =>
                task.Enabled &&
                string.Equals(task.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(task.CameraId, camera.ConfigurationId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(task.CameraId, camera.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        camera.Overlays.Clear();
        if (tasks.Length == 0)
        {
            Log("INFO", $"{camera.Name} 已拍照，但没有配置检测任务");
            AppDiagnostics.Info("inspection", $"No tasks configured. Camera={camera.Name}, ProductModelId={productModelId}");

            // 无任务时也保存原图和渲染图（本地 + AWS）
            string? rawPath = null;
            string? renderedPath = null;
            try
            {
                rawPath = await Task.Run(
                    () => _imageArchiveService.SaveCameraFrame(
                        frame,
                        productModel.Id,
                        serialNumber,
                        camera.Name,
                        archiveTimestamp,
                        camera.BuildOriginalImageWatermarkOptions(),
                        null),
                    cancellationToken);
                Log("INFO", $"{camera.Name} 原图已保存（无任务）：{rawPath}");
                AppDiagnostics.Info("inspection", $"No-task raw image archived. Camera={camera.Name}, Path={rawPath}");

                renderedPath = await Task.Run(
                    () => _imageArchiveService.SaveRenderedResult(
                        frame,
                        productModel.Id,
                        serialNumber,
                        camera.Name,
                        archiveTimestamp,
                        [],
                        null,
                        "无任务"),
                    cancellationToken);
                Log("INFO", $"{camera.Name} 渲染图已保存（无任务）：{renderedPath}");
                AppDiagnostics.Info("inspection", $"No-task rendered image archived. Camera={camera.Name}, Path={renderedPath}");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"{camera.Name} 无任务图片保存失败：{ex.Message}");
                AppDiagnostics.Error("inspection", $"No-task image archive failed. Camera={camera.Name}", ex);
            }

            if (rawPath != null && renderedPath != null)
            {
                _ = UploadImagePairToS3Async(rawPath, renderedPath, serialNumber, camera.Name, cancellationToken);
            }

            return true;
        }

        AppDiagnostics.Info(
            "inspection",
            $"Executing tasks. Camera={camera.Name}, ProductModelId={productModelId}, TaskCount={tasks.Length}, Tasks={string.Join(", ", tasks.Select(task => task.Name))}");

        Mat alignedFrame;
        try
        {
            alignedFrame = await AlignInspectionFrameAsync(camera, frame, tasks, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var task in tasks)
            {
                await RecordInspectionResultAsync(
                    cycleId,
                    productModel.Id,
                    serialNumber,
                    camera,
                    task,
                    rawImagePath,
                    null,
                    null,
                    ex.Message,
                    cancellationToken);
            }

            var renderedPath = await Task.Run(
                () => _imageArchiveService.SaveRenderedResult(
                    frame,
                    archiveProductCode,
                    serialNumber,
                    camera.Name,
                    archiveTimestamp,
                    [],
                    InspectionJudgment.NG,
                    "ALIGNMENT NG"),
                cancellationToken);
            Log("INFO", $"{camera.Name} 结果渲染图已保存：{renderedPath}");
            AppDiagnostics.Info("inspection", $"Alignment-failed result image archived. Camera={camera.Name}, Path={renderedPath}");

            // 对齐失败时只上传原图，不上传渲染图
            _ = UploadRawImageToS3Async(rawImagePath, serialNumber, camera.Name, cancellationToken);

            return false;
        }

        using var alignedFrameOwner = alignedFrame;
        camera.Frame = MatImageSourceConverter.CreateImageSource(alignedFrame);
        camera.LastFrameInfo = $"{DateTime.Now:HH:mm:ss}  {alignedFrame.Width:0}x{alignedFrame.Height:0}  已对齐";
        Log("INFO", $"{camera.Name} 对齐图已显示：{alignedFrame.Width}x{alignedFrame.Height}，任务 {tasks.Length} 个");
        var allTasksCompleted = true;
        var overallJudgment = InspectionJudgment.OK;
        var renderAnnotations = new List<InspectionRenderAnnotation>(tasks.Length);
        var taskResults = new List<(InspectionTaskDefinition Task, InspectionTaskExecutionResult? Result, string? CropPath)>();
        foreach (var task in tasks)
        {
            string? cropPath = null;
            InspectionTaskExecutionResult? result = null;
            try
            {
                AppDiagnostics.Info("inspection", $"Task start. Camera={camera.Name}, Task={task.Name}, TaskId={task.Id}");
                using var cropMat = ClipFrameImageMaterializer.CropFrame(alignedFrame, task.Roi);
                var tempCropPath = Path.Combine(Path.GetTempPath(), $"vision_crop_{Guid.NewGuid():N}.jpg");
                Cv2.ImWrite(tempCropPath, cropMat);
                cropPath = tempCropPath;
                Log("INFO", $"{camera.Name} {task.Name} Crop已生成");
                await EnsureClassificationLibraryReadyAsync(productModel, camera, task, cancellationToken);
                result = await _taskExecutionService.ExecuteImageAsync(task, cropPath, cancellationToken);
                
                if (result.Judgment == InspectionJudgment.NG)
                {
                    overallJudgment = InspectionJudgment.NG;
                }

                renderAnnotations.Add(CreateRenderAnnotation(task, result));
                Log(result.Judgment == InspectionJudgment.OK ? "INFO" : "NG", $"{camera.Name} {result.TaskName} 判定={result.Judgment}");
                AppDiagnostics.Info(
                    "inspection",
                    $"Task completed. Camera={camera.Name}, Task={result.TaskName}, Judgment={result.Judgment}, Score={result.Score?.ToString("0.0000") ?? "null"}, ElapsedMs={result.ElapsedMs:0}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppDiagnostics.Warn("inspection", $"Task canceled. Camera={camera.Name}, Task={task.Name}");
                throw;
            }
            catch (Exception ex)
            {
                allTasksCompleted = false;
                overallJudgment = InspectionJudgment.NG;
                renderAnnotations.Add(CreateRenderAnnotation(task, null));
                SetAlarm($"{camera.Name} {task.Name} 执行失败：{ex.Message}");
                Log("WARN", $"{camera.Name} {task.Name} 执行失败：{ex.Message}");
                AppDiagnostics.Error("inspection", $"Task failed. Camera={camera.Name}, Task={task.Name}, TaskId={task.Id}", ex);
            }
            
            taskResults.Add((task, result, cropPath));
        }

        foreach (var item in taskResults)
        {
            var task = item.Task;
            var result = item.Result;
            var cropPath = item.CropPath;
            try
            {
                if (!string.IsNullOrEmpty(cropPath) && File.Exists(cropPath))
                {
                    var finalCropPath = await Task.Run(
                        () => _imageArchiveService.SaveTaskCrop(
                            alignedFrame,
                            archiveProductCode,
                            serialNumber,
                            camera.Name,
                            task,
                            archiveTimestamp,
                            result?.Judgment),
                        cancellationToken);
                    AppDiagnostics.Info("inspection", $"Task crop archived. Camera={camera.Name}, Task={task.Name}, Path={finalCropPath}");
                    Log("INFO", $"{camera.Name} {task.Name} Crop已保存：{finalCropPath}");
                    
                    if (result != null)
                    {
                        await RecordInspectionResultAsync(
                            cycleId,
                            productModel.Id,
                            serialNumber,
                            camera,
                            task,
                            rawImagePath,
                            finalCropPath,
                            result,
                            null,
                            cancellationToken);
                        _results.Add(new InspectionResultRow
                        {
                            CameraName = camera.Name,
                            Item = result.TaskName,
                            Value = FormatInspectionValue(result),
                            DetailText = FormatInspectionDetail(result),
                            Result = result.Judgment.ToString()
                        });
                        AddRuntimeOverlays(camera, task, result);
                    }
                    else
                    {
                        await RecordInspectionResultAsync(
                            cycleId,
                            productModel.Id,
                            serialNumber,
                            camera,
                            task,
                            rawImagePath,
                            finalCropPath,
                            null,
                            "执行失败",
                            cancellationToken);
                        _results.Add(new InspectionResultRow
                        {
                            CameraName = camera.Name,
                            Item = task.Name,
                            Value = "失败",
                            DetailText = "执行失败",
                            Result = "NG"
                        });
                        AddRuntimeOverlays(camera, task, null);
                    }
                    
                    try { File.Delete(cropPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Warn("inspection", $"Save crop failed. Camera={camera.Name}, Task={task.Name}, Error={ex.Message}");
            }
        }

        var renderedResultPath = await Task.Run(
            () => _imageArchiveService.SaveRenderedResult(
                alignedFrame,
                archiveProductCode,
                serialNumber,
                camera.Name,
                archiveTimestamp,
                renderAnnotations,
                overallJudgment),
            cancellationToken);
        Log("INFO", $"{camera.Name} 结果渲染图已保存：{renderedResultPath}");
        AppDiagnostics.Info("inspection", $"Result image archived. Camera={camera.Name}, Path={renderedResultPath}, TaskCount={renderAnnotations.Count}");

        _ = UploadImagePairToS3Async(rawImagePath, renderedResultPath, serialNumber, camera.Name, cancellationToken);

        return allTasksCompleted;
    }

    private static InspectionRenderAnnotation CreateRenderAnnotation(
        InspectionTaskDefinition task,
        InspectionTaskExecutionResult? result)
    {
        var judgment = result?.Judgment ?? InspectionJudgment.NG;
        var resultText = result == null
            ? "NG"
            : result.Kind == InspectionTaskKind.Measurement
                ? result.DistanceMm.HasValue
                    ? $"{judgment} {result.DistanceMm.Value:0.00}mm"
                    : "NG"
                : GetClassificationDisplayScore(result) is { } score
                    ? $"{judgment} {score:0.000}"
                    : judgment.ToString();
        var taskName = string.IsNullOrWhiteSpace(task.Name) ? task.Id : task.Name;
        return new InspectionRenderAnnotation(
            task.Roi,
            judgment,
            resultText,
            taskName,
            result?.FirstEdgeIndex,
            result?.SecondEdgeIndex);
    }

    private void AddRuntimeOverlays(
        CameraViewModel camera,
        InspectionTaskDefinition task,
        InspectionTaskExecutionResult? result)
    {
        var isOk = result?.Judgment == InspectionJudgment.OK;
        var judgment = isOk ? RoiOverlayJudgment.OK : RoiOverlayJudgment.NG;
        var stroke = isOk ? UiBrushes.Success : UiBrushes.Danger;
        var roiOverlay = RoiOverlayVisualFactory.CreateRoiOverlay(
            task.Roi,
            _configuration,
            task.Name,
            judgment,
            1.4);
        roiOverlay.Id = $"runtime-{task.Id}-roi";
        roiOverlay.IsEditable = false;
        camera.Overlays.Add(roiOverlay);

        if (task.Kind != InspectionTaskKind.Measurement)
        {
            return;
        }

        foreach (var overlay in RoiOverlayVisualFactory.CreateMeasurementOverlays(
                     task.Roi,
                     result?.FirstEdgeIndex,
                     result?.SecondEdgeIndex,
                     stroke,
                     $"runtime-{task.Id}",
                     includeDirectionArrow: true))
        {
            camera.Overlays.Add(overlay);
        }
    }

    private async ValueTask EnsureClassificationLibraryReadyAsync(
        ProductModelDefinition productModel,
        CameraViewModel camera,
        InspectionTaskDefinition task,
        CancellationToken cancellationToken)
    {
        if (task.Kind != InspectionTaskKind.Classification)
        {
            return;
        }

        var seedResult = await _clipTrainingLibraryService.EnsureTemplateOkSampleAsync(
            productModel,
            task,
            BuildCameraIdCandidates(camera, task),
            _configuration.Alignments.ToArray(),
            cancellationToken);

        if (seedResult.Added)
        {
            Log("INFO", $"{camera.Name} {task.Name} 已从模板自动创建OK样本");
            return;
        }

        if (seedResult.Summary.OkSamples.Count == 0)
        {
            var reason = FormatTemplateSeedMessage(seedResult.Message);
            throw new InvalidOperationException($"模型训练库没有OK样本：{task.Name}。{reason}");
        }
    }

    private static IReadOnlyList<string> BuildCameraIdCandidates(
        CameraViewModel camera,
        InspectionTaskDefinition task)
    {
        return new[]
            {
                task.CameraId,
                camera.ConfigurationId,
                camera.Name
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatTemplateSeedMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "请先在型号管理中创建模板参考图，或到模型训练库导入OK样本。";
        }

        if (message.Contains("Template reference image is not registered", StringComparison.OrdinalIgnoreCase))
        {
            return "当前相机未注册模板参考图，请先到型号管理创建模板。";
        }

        if (message.Contains("Template reference image was not found", StringComparison.OrdinalIgnoreCase))
        {
            return "模板参考图文件不存在，请重新创建模板。";
        }

        return message;
    }

    private async Task RecordInspectionResultAsync(
        long cycleId,
        string productCode,
        string serialNumber,
        CameraViewModel camera,
        InspectionTaskDefinition task,
        string rawImagePath,
        string? cropImagePath,
        InspectionTaskExecutionResult? result,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var vectorSet = task.Kind == InspectionTaskKind.Classification
                ? task.EnsureClipVectorSet()
                : task.Clip;
            var learningState = ResolveLearningState(result);
            await _resultStore.AddAsync(new InspectionResultRecord
            {
                OccurredAt = DateTimeOffset.Now,
                CycleId = cycleId,
                ProductCode = productCode,
                SerialNumber = serialNumber,
                CameraId = camera.ConfigurationId,
                CameraName = camera.Name,
                TaskId = task.Id,
                TaskName = task.Name,
                VectorSetId = vectorSet?.VectorSetId,
                RawImagePath = rawImagePath,
                CropImagePath = cropImagePath,
                Result = result?.Judgment.ToString() ?? InspectionJudgment.NG.ToString(),
                OkScore = result?.OkScore ?? result?.Score,
                NgScore = result?.NgScore,
                Margin = result?.Margin,
                Threshold = result?.Threshold,
                TopK = result?.TopK,
                ElapsedMs = result?.ElapsedMs,
                ErrorMessage = errorMessage,
                LearningState = learningState,
                TopOkSimilarity = result?.TopOkSimilarity,
                TopOkImagePath = result?.TopOkImagePath,
                TopNgSimilarity = result?.TopNgSimilarity,
                TopNgImagePath = result?.TopNgImagePath
            }, cancellationToken);
            if (learningState == InspectionLearningStates.OkCandidate)
            {
                Log("INFO", $"{camera.Name} {task.Name} 已记录OK候选样本");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("WARN", $"检测记录写入失败：{ex.Message}");
            AppDiagnostics.Error("inspection", "Inspection result record failed.", ex);
        }
    }

    private static string ResolveLearningState(InspectionTaskExecutionResult? result)
    {
        const float minCandidateSimilarity = 0.96f;
        const float maxDuplicateSimilarity = 0.995f;
        const int maxOkSamples = 20;

        if (result == null ||
            result.Judgment != InspectionJudgment.OK ||
            result.OkSampleCount is not { } okSampleCount ||
            okSampleCount >= maxOkSamples ||
            result.TopOkSimilarity is not { } nearestOk)
        {
            return InspectionLearningStates.None;
        }

        return nearestOk >= minCandidateSimilarity && nearestOk < maxDuplicateSimilarity
            ? InspectionLearningStates.OkCandidate
            : InspectionLearningStates.None;
    }

    private async Task<Mat> AlignInspectionFrameAsync(
        CameraViewModel camera,
        Mat frame,
        IReadOnlyList<InspectionTaskDefinition> tasks,
        CancellationToken cancellationToken)
    {
        var productModelId = _configuration.SelectedProductModelId;
        var alignment = ResolveCameraAlignment(productModelId, camera);
        if (alignment == null)
        {
            var message = $"{camera.Name} 未注册模板图像";
            RecordAlignmentFailure(camera, tasks, message);
            throw new InvalidOperationException(message);
        }

        // 如果选择跳过补正，跳过对齐步骤
        if (string.Equals(alignment.AlignmentMode, "跳过补正", StringComparison.OrdinalIgnoreCase))
        {
            Log("INFO", $"{camera.Name} 选择跳过补正，直接使用原始图像");
            return frame;
        }

        var result = await Task.Run(
            () => _alignmentService.AlignMatToTemplate(alignment, frame),
            cancellationToken);
        if (!result.Success || result.Image == null)
        {
            var message = $"{camera.Name} {result.Message}";
            RecordAlignmentFailure(camera, tasks, message);
            throw new InvalidOperationException(message);
        }

        Log("INFO", $"{camera.Name} 已对齐模板");
        AppDiagnostics.Info("inspection", $"Camera aligned to template. Camera={camera.Name}, ProductModelId={productModelId}, Message={result.Message}");
        return result.Image;
    }

    private CameraAlignmentDefinition? ResolveCameraAlignment(string productModelId, CameraViewModel camera)
    {
        return _configuration.Alignments.FirstOrDefault(alignment =>
            string.Equals(alignment.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(alignment.CameraId, camera.ConfigurationId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(alignment.CameraId, camera.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private void RecordAlignmentFailure(
        CameraViewModel camera,
        IReadOnlyList<InspectionTaskDefinition> tasks,
        string message)
    {
        SetAlarm(message);
        foreach (var task in tasks)
        {
            _results.Add(new InspectionResultRow
            {
                CameraName = camera.Name,
                Item = task.Name,
                Value = "对齐失败",
                DetailText = message,
                Result = "NG"
            });
        }

        Log("WARN", message);
        AppDiagnostics.Warn("inspection", $"Template alignment failed. Camera={camera.Name}, Message={message}");
    }

    private ProductModelDefinition? ResolveProductModel(string productCode)
    {
        return _configuration.ProductModels.FirstOrDefault(product =>
                   string.Equals(product.Id, productCode, StringComparison.OrdinalIgnoreCase))
               ?? _configuration.ProductModels.FirstOrDefault(product =>
                   string.Equals(product.Name, productCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatInspectionValue(InspectionTaskExecutionResult result)
    {
        if (result.Kind == InspectionTaskKind.Classification)
        {
            var score = GetClassificationDisplayScore(result);
            return score.HasValue
                ? $"{result.Judgment} {score.Value:0.000}"
                : result.Judgment.ToString();
        }

        if (result.Kind == InspectionTaskKind.Measurement)
        {
            return result.DistanceMm.HasValue
                ? $"{result.DistanceMm.Value:0.00} mm"
                : "失败";
        }

        return result.Score.HasValue
            ? result.Score.Value.ToString("0.000")
            : "--";
    }

    private static string FormatInspectionDetail(InspectionTaskExecutionResult result)
    {
        var lines = new List<string>
        {
            $"{result.TaskName} 判定：{result.Judgment}"
        };

        if (result.Kind == InspectionTaskKind.Classification)
        {
            lines.Add($"OK均值：{FormatNullable(result.OkScore)}");
            lines.Add($"NG均值：{FormatNullable(result.NgScore)}");
            lines.Add($"Top1 OK：{FormatNullable(result.TopOkSimilarity)}");
            lines.Add($"Top1 NG：{FormatNullable(result.TopNgSimilarity)}");
            lines.Add($"Margin：{FormatNullable(result.Margin)}");
            lines.Add($"阈值：{FormatNullable(result.Threshold)}");
            lines.Add($"TopK：{result.TopK?.ToString() ?? "--"}");
            lines.Add($"OK样本：{result.OkSampleCount?.ToString() ?? "--"}");
            lines.Add($"NG样本：{result.NgSampleCount?.ToString() ?? "--"}");
        }
        else if (result.Kind == InspectionTaskKind.Measurement)
        {
            lines.Add($"距离：{FormatNullable(result.DistanceMm, "0.00")} mm");
            lines.Add($"像素距离：{FormatNullable(result.DistancePx, "0.00")} px");
            lines.Add($"E1：{FormatNullable(result.FirstEdgeIndex, "0.00")}");
            lines.Add($"E2：{FormatNullable(result.SecondEdgeIndex, "0.00")}");
            if (!string.IsNullOrWhiteSpace(result.FailureReason))
            {
                lines.Add($"原因：{result.FailureReason}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            lines.Add(result.Detail);
        }

        lines.Add($"耗时：{result.ElapsedMs:0} ms");
        return string.Join(Environment.NewLine, lines);
    }

    private static float? GetClassificationDisplayScore(InspectionTaskExecutionResult result)
    {
        if (result.Judgment == InspectionJudgment.NG && result.NgScore.HasValue)
        {
            return result.NgScore.Value;
        }

        return result.OkScore ?? result.Score;
    }

    private static string FormatNullable(float? value, string format = "0.0000")
    {
        return value.HasValue ? value.Value.ToString(format) : "--";
    }

    private static string FormatNullable(double? value, string format = "0.0000")
    {
        return value.HasValue ? value.Value.ToString(format) : "--";
    }

    private static string FormatTriggerSource(InspectionTriggerSource source)
    {
        return source switch
        {
            InspectionTriggerSource.Manual => "手动",
            InspectionTriggerSource.Di => "DI",
            _ => source.ToString()
        };
    }

    private static string FormatCameraForTrace(CameraViewModel camera)
    {
        return
            $"Name={camera.Name}, Id={camera.ConfigurationId}, Enabled={camera.IsEnabled}, Configured={camera.IsAcquisitionConfigured}, ExplicitTarget={camera.HasExplicitAcquisitionTarget}, Connected={camera.IsConnected}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}, OpenCvSource={camera.OpenCvSource}, CameraIndex={camera.CameraIndex}, Trigger={camera.TriggerMode}";
    }

    private async Task UploadRawImageToS3Async(
        string localImagePath,
        string serialNumber,
        string cameraName,
        CancellationToken cancellationToken)
    {
        if (_s3UploadService == null || !_s3UploadService.IsEnabled)
            return;

        try
        {
            await _s3UploadService.UploadRawImageFolderAsync(localImagePath, serialNumber, cameraName, cancellationToken);
            AppDiagnostics.Info("inspection", $"Raw image uploaded to S3. Camera={cameraName}, Serial={serialNumber}");
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Info("inspection", $"S3 upload canceled. Camera={cameraName}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn("inspection", $"S3 upload failed (non-blocking). Camera={cameraName}, Error={ex.Message}");
        }
    }

    private async Task UploadImagePairToS3Async(
        string rawImagePath,
        string renderedImagePath,
        string serialNumber,
        string cameraName,
        CancellationToken cancellationToken)
    {
        if (_s3UploadService == null || !_s3UploadService.IsEnabled)
            return;

        try
        {
            await _s3UploadService.UploadImagePairAsync(rawImagePath, renderedImagePath, serialNumber, cameraName, cancellationToken);
            AppDiagnostics.Info("inspection", $"Image pair uploaded to S3. Camera={cameraName}, Serial={serialNumber}");
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Info("inspection", $"S3 image pair upload canceled. Camera={cameraName}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn("inspection", $"S3 image pair upload failed (non-blocking). Camera={cameraName}, Error={ex.Message}");
        }
    }

    private void Log(string level, string message) => _callbacks.Log(level, message);

    private void SetAlarm(string message) => _callbacks.SetAlarm(message);

    private void ClearAlarm() => _callbacks.ClearAlarm();
}
