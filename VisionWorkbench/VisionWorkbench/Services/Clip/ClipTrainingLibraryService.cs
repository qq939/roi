using System.IO;
using ClipInspect.Storage.Sqlite;
using OpenCvSharp;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services.Clip;

public sealed class ClipTrainingLibraryService
{
    private const string TemplateSeedSource = "TemplateSeedDirectRoi";
    private const string AlignmentTemplateImageId = "__alignment_template__";
    private const string AlignmentTemplateSource = "对齐模板";

    private readonly IClipClassificationService _clipClassificationService;
    private readonly VisionAssetPathService _assetPathService;
    private readonly TaskImageAlignmentService _alignmentService;
    private readonly CameraTrainingLibraryStore _cameraTrainingStore;
    private readonly ClipRuntimeOptions _options;
    private readonly string _queryDirectory;

    public ClipTrainingLibraryService(
        IClipClassificationService clipClassificationService,
        VisionAssetPathService assetPathService,
        TaskImageAlignmentService alignmentService,
        ClipRuntimeOptions? options = null,
        VisionRuntimePaths? runtimePaths = null)
    {
        _clipClassificationService = clipClassificationService;
        _assetPathService = assetPathService;
        _alignmentService = alignmentService;
        _cameraTrainingStore = new CameraTrainingLibraryStore(assetPathService);
        _options = options ?? new ClipRuntimeOptions();
        _queryDirectory = (runtimePaths ?? new VisionRuntimePaths()).ClipTrainingQueriesDirectory;
    }

    public async ValueTask<ClipTrainingLibrarySummary> LoadSummaryAsync(
        InspectionTaskDefinition task,
        CancellationToken cancellationToken = default)
    {
        var vectorSet = task.EnsureClipVectorSet();
        var store = new SqliteVectorStore(_options.DatabasePath);
        var product = await store.GetProductAsync(vectorSet.VectorSetId, cancellationToken);
        var samples = product == null
            ? Array.Empty<SqliteVectorSample>()
            : await store.ListSamplesAsync(vectorSet.VectorSetId, cancellationToken);

        var imageSamples = samples
            .Where(sample => sample.Kind == "Image" && sample.Enabled && !string.IsNullOrWhiteSpace(sample.ImagePath))
            .Select(MapSample)
            .ToArray();

        var okCount = imageSamples.Count(s => s.Label == InspectionJudgment.OK);
        var ngCount = imageSamples.Count(s => s.Label == InspectionJudgment.NG);
        
        // 调试日志：记录训练库加载的样本数量
        AppDiagnostics.Info("clip-training", 
            $"LoadSummary: Task={task.Name}, TaskId={task.Id}, CameraId={task.CameraId}, " +
            $"VectorSetId={vectorSet.VectorSetId}, OK={okCount}, NG={ngCount}");

        return new ClipTrainingLibrarySummary
        {
            VectorSetId = vectorSet.VectorSetId,
            VectorSetExists = product != null,
            FeatureDim = product?.FeatureDim ?? 0,
            TopK = product?.TopK ?? vectorSet.TopK,
            // The task definition is the source of truth. SQLite keeps a mirrored value
            // for vector-set metadata, but inference always receives the task value.
            Threshold = vectorSet.Threshold,
            OkSamples = imageSamples.Where(sample => sample.Label == InspectionJudgment.OK).ToArray(),
            NgSamples = imageSamples.Where(sample => sample.Label == InspectionJudgment.NG).ToArray()
        };
    }

    public async ValueTask<bool> UpdateVectorSetConfigAsync(
        ClipVectorSetDefinition vectorSet,
        CancellationToken cancellationToken = default)
    {
        vectorSet.EnsureId();
        vectorSet.Normalize();
        var store = new SqliteVectorStore(_options.DatabasePath);
        var product = await store.GetProductAsync(vectorSet.VectorSetId, cancellationToken);
        if (product == null)
        {
            return false;
        }

        await store.CreateOrUpdateProductAsync(new SqliteProductConfig
        {
            ProductId = vectorSet.VectorSetId,
            Name = vectorSet.DisplayName,
            ModelName = vectorSet.ModelName,
            Pretrained = vectorSet.Pretrained,
            FeatureDim = product.FeatureDim,
            TopK = vectorSet.TopK,
            Threshold = vectorSet.Threshold,
            TextWeight = product.TextWeight
        }, cancellationToken);
        return true;
    }

    public async ValueTask<CameraTrainingLibraryDocument> LoadCameraTrainingDocumentAsync(
        string productModelId,
        string cameraId,
        string cameraName,
        IReadOnlyList<InspectionTaskDefinition> tasks,
        IReadOnlyList<CameraAlignmentDefinition> alignments,
        CancellationToken cancellationToken = default)
    {
        var document = await _cameraTrainingStore.LoadAsync(productModelId, cameraId, tasks, cancellationToken);
        var alignment = ResolveCameraAlignment(productModelId, cameraId, cameraName, alignments);
        if (EnsureAlignmentTemplateRecord(document, alignment, tasks))
        {
            await _cameraTrainingStore.SaveAsync(document, cancellationToken);
        }

        return document;
    }

    public async ValueTask SaveCameraTrainingDocumentAsync(
        CameraTrainingLibraryDocument document,
        CancellationToken cancellationToken = default)
    {
        await _cameraTrainingStore.SaveAsync(document, cancellationToken);
    }

    public async ValueTask<int> RemoveCameraTrainingImagesAsync(
        CameraTrainingLibraryDocument document,
        IReadOnlyCollection<string> imageIds,
        CancellationToken cancellationToken = default)
    {
        var idSet = imageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0)
        {
            return 0;
        }

        var removed = document.Images
            .Where(image => idSet.Contains(image.Id) && !image.IsProtected && !IsAlignmentTemplateRecord(image))
            .ToArray();
        if (removed.Length == 0)
        {
            return 0;
        }

        document.Images.RemoveAll(image => idSet.Contains(image.Id) && !image.IsProtected && !IsAlignmentTemplateRecord(image));
        await _cameraTrainingStore.SaveAsync(document, cancellationToken);
        foreach (var record in removed)
        {
            DeleteCameraTrainingImageFiles(document, record);
        }

        return removed.Length;
    }

    public string GetCameraTrainingImageFullPath(string relativePath)
    {
        return _cameraTrainingStore.GetFullPath(relativePath);
    }

    public async ValueTask<CameraTrainingImageImportResult> AddCameraTrainingImageFromFileAsync(
        CameraTrainingFileImportRequest request,
        CancellationToken cancellationToken = default)
    {
        using var source = Cv2.ImRead(Path.GetFullPath(request.SourceImagePath), ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"图片读取失败：{request.SourceImagePath}");
        }

        return await AddCameraTrainingImageCoreAsync(
            request,
            source,
            Path.GetFileName(request.SourceImagePath),
            cancellationToken);
    }

    public async ValueTask<CameraTrainingImageImportResult> AddCameraTrainingImageFromMatAsync(
        CameraTrainingMatImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SourceImage.Empty())
        {
            throw new InvalidOperationException("相机取图为空，无法加入训练库。");
        }

        return await AddCameraTrainingImageCoreAsync(
            request,
            request.SourceImage,
            request.SourceName,
            cancellationToken);
    }

    public async ValueTask<CameraTrainingVectorSyncResult> RebuildCameraTrainingVectorSetsAsync(
        CameraTrainingVectorSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = await _cameraTrainingStore.LoadAsync(
            request.ProductModel.Id,
            request.CameraId,
            request.Tasks,
            cancellationToken);
        var result = new CameraTrainingVectorSyncResult();
        var store = new SqliteVectorStore(_options.DatabasePath);

        foreach (var task in request.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectorSet = task.EnsureClipVectorSet();
            var taskCropDirectory = _cameraTrainingStore.GetTaskCropDirectory(
                request.ProductModel.Id,
                request.CameraId,
                task.Id);
            if (Directory.Exists(taskCropDirectory))
            {
                Directory.Delete(taskCropDirectory, recursive: true);
            }

            var okPaths = new List<string>();
            var ngPaths = new List<string>();
            var ignoredCount = 0;
            var unlabeledCount = 0;

            foreach (var image in document.Images)
            {
                var label = image.Labels.FirstOrDefault(item =>
                    string.Equals(item.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
                switch (label?.State ?? TrainingLabelState.Unlabeled)
                {
                    case TrainingLabelState.OK:
                    case TrainingLabelState.NG:
                        break;
                    case TrainingLabelState.Ignored:
                        ignoredCount++;
                        continue;
                    default:
                        unlabeledCount++;
                        continue;
                }

                if (!image.AlignmentSucceeded || string.IsNullOrWhiteSpace(image.AlignedImageRelativePath))
                {
                    unlabeledCount++;
                    continue;
                }

                var alignedPath = _cameraTrainingStore.GetFullPath(image.AlignedImageRelativePath);
                if (!File.Exists(alignedPath))
                {
                    unlabeledCount++;
                    continue;
                }

                using var aligned = Cv2.ImRead(alignedPath, ImreadModes.Color);
                if (aligned.Empty())
                {
                    unlabeledCount++;
                    continue;
                }

                using var crop = ClipFrameImageMaterializer.CropFrame(aligned, task.Roi);
                var cropPath = _cameraTrainingStore.CreateCropImagePath(
                    request.ProductModel.Id,
                    request.CameraId,
                    task.Id,
                    label!.State,
                    image.Id,
                    ResolveTrainingImageSampleName(image));
                if (!Cv2.ImWrite(cropPath, crop))
                {
                    throw new InvalidOperationException($"任务 {task.Name} 裁图保存失败：{cropPath}");
                }

                if (label.State == TrainingLabelState.OK)
                {
                    okPaths.Add(cropPath);
                }
                else
                {
                    ngPaths.Add(cropPath);
                }
            }

            await store.DeleteProductAsync(vectorSet.VectorSetId, cancellationToken);
            if (okPaths.Count == 0)
            {
                result.Tasks.Add(new CameraTrainingTaskSyncResult
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    VectorSetId = vectorSet.VectorSetId,
                    OkCount = 0,
                    NgCount = ngPaths.Count,
                    IgnoredCount = ignoredCount,
                    UnlabeledCount = unlabeledCount,
                    Skipped = true,
                    Message = $"{task.Name}：跳过，至少需要 1 张 OK 样本。NG {ngPaths.Count}，忽略 {ignoredCount}，未标注 {unlabeledCount}"
                });
                continue;
            }

            var build = await _clipClassificationService.BuildVectorSetAsync(new ClipBuildVectorSetRequest
            {
                VectorSet = vectorSet,
                OkImagePaths = okPaths,
                NgImagePaths = ngPaths,
                Source = "CameraTrainingLibrary"
            }, cancellationToken);

            result.Tasks.Add(new CameraTrainingTaskSyncResult
            {
                TaskId = task.Id,
                TaskName = task.Name,
                VectorSetId = build.VectorSetId,
                OkCount = build.OkCount,
                NgCount = build.NgCount,
                IgnoredCount = ignoredCount,
                UnlabeledCount = unlabeledCount,
                Built = true,
                Message = $"{task.Name}：已重建 OK {build.OkCount} / NG {build.NgCount}，忽略 {ignoredCount}，未标注 {unlabeledCount}"
            });
        }

        return result;
    }

    public async ValueTask<ClipTrainingAddSamplesResult> AddSamplesAsync(
        ClipTrainingAddSamplesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SourceImagePaths.Count == 0)
        {
            return new ClipTrainingAddSamplesResult
            {
                AddedCount = 0,
                PreparedImagePaths = Array.Empty<string>(),
                Summary = await LoadSummaryAsync(request.Task, cancellationToken)
            };
        }

        var vectorSet = request.Task.EnsureClipVectorSet();
        var preparedPaths = new List<string>(request.SourceImagePaths.Count);
        var databaseUpdated = false;
        try
        {
            foreach (var sourcePath in request.SourceImagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                preparedPaths.Add(PrepareManagedSampleImage(request, sourcePath));
            }

            var store = new SqliteVectorStore(_options.DatabasePath);
            var product = await store.GetProductAsync(vectorSet.VectorSetId, cancellationToken);
            if (product == null)
            {
                if (request.Label != InspectionJudgment.OK)
                {
                    throw new InvalidOperationException("首次创建模型训练库至少需要 1 张 OK 样本。");
                }

                await _clipClassificationService.BuildVectorSetAsync(new ClipBuildVectorSetRequest
                {
                    VectorSet = vectorSet,
                    OkImagePaths = preparedPaths
                }, cancellationToken);
                databaseUpdated = true;
            }
            else
            {
                // 先追加新样本
                await _clipClassificationService.AddSamplesAsync(new ClipSampleMaintenanceRequest
                {
                    VectorSet = vectorSet,
                    Label = request.Label,
                    ImagePaths = preparedPaths,
                    Source = $"TrainingLibrary:{request.InputMode}"
                }, cancellationToken);
                databaseUpdated = true;

                // 自动重建整个训练库，重新编码所有样本
                await RebuildVectorSetAsync(request.Task, cancellationToken);
            }

            return new ClipTrainingAddSamplesResult
            {
                AddedCount = preparedPaths.Count,
                PreparedImagePaths = preparedPaths.ToArray(),
                Summary = await LoadSummaryAsync(request.Task, cancellationToken)
            };
        }
        catch
        {
            if (!databaseUpdated)
            {
                CleanupPreparedFiles(preparedPaths);
            }

            throw;
        }
    }

    private async ValueTask<CameraTrainingImageImportResult> AddCameraTrainingImageCoreAsync(
        CameraTrainingImageImportRequest request,
        Mat source,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        var alignment = ResolveCameraAlignment(
            request.ProductModel.Id,
            request.CameraId,
            request.CameraName,
            request.Alignments)
            ?? throw new InvalidOperationException($"{request.CameraName} 未注册模板图像，无法生成对齐训练图。");

        using var aligned = AlignCameraTrainingImage(alignment, source);
        var document = await _cameraTrainingStore.LoadAsync(
            request.ProductModel.Id,
            request.CameraId,
            request.Tasks,
            cancellationToken);
        var imageId = Guid.NewGuid().ToString("N");
        var rawPath = _cameraTrainingStore.CreateRawImagePath(
            request.ProductModel.Id,
            request.CameraId,
            imageId,
            originalFileName);
        var alignedPath = _cameraTrainingStore.CreateAlignedImagePath(
            request.ProductModel.Id,
            request.CameraId,
            imageId,
            originalFileName);

        if (!Cv2.ImWrite(rawPath, source))
        {
            throw new InvalidOperationException($"原图保存失败：{rawPath}");
        }

        if (!Cv2.ImWrite(alignedPath, aligned))
        {
            throw new InvalidOperationException($"对齐图保存失败：{alignedPath}");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new TrainingImageRecord
        {
            Id = imageId,
            ProductModelId = request.ProductModel.Id,
            CameraId = request.CameraId,
            RawImageRelativePath = _cameraTrainingStore.GetRelativePath(rawPath),
            AlignedImageRelativePath = _cameraTrainingStore.GetRelativePath(alignedPath),
            Source = request.SourceName,
            OriginalFileName = originalFileName,
            CreatedAt = now,
            Width = aligned.Width,
            Height = aligned.Height,
            AlignmentSucceeded = true,
            AlignmentMessage = "对齐成功",
            Labels = request.Tasks.Select(task => new TrainingTaskLabel
            {
                TaskId = task.Id,
                State = TrainingLabelState.Unlabeled,
                UpdatedAt = now
            }).ToList()
        };
        document.Images.Add(record);
        await _cameraTrainingStore.SaveAsync(document, cancellationToken);

        return new CameraTrainingImageImportResult
        {
            Record = record,
            Document = document
        };
    }

    private bool EnsureAlignmentTemplateRecord(
        CameraTrainingLibraryDocument document,
        CameraAlignmentDefinition? alignment,
        IReadOnlyList<InspectionTaskDefinition> tasks)
    {
        if (alignment == null || string.IsNullOrWhiteSpace(alignment.ReferenceImageRelativePath))
        {
            return false;
        }

        var changed = false;
        var record = document.Images.FirstOrDefault(IsAlignmentTemplateRecord);
        if (record == null)
        {
            record = new TrainingImageRecord
            {
                Id = AlignmentTemplateImageId,
                CreatedAt = alignment.RegisteredAt ?? DateTimeOffset.UtcNow,
                Labels = tasks.Select(task => new TrainingTaskLabel
                {
                    TaskId = task.Id,
                    State = TrainingLabelState.Unlabeled,
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToList()
            };
            document.Images.Insert(0, record);
            changed = true;
        }

        changed |= SetIfDifferent(record.Id, AlignmentTemplateImageId, value => record.Id = value);
        changed |= SetIfDifferent(record.ProductModelId, document.ProductModelId, value => record.ProductModelId = value);
        changed |= SetIfDifferent(record.CameraId, document.CameraId, value => record.CameraId = value);
        changed |= SetIfDifferent(record.RawImageRelativePath, alignment.ReferenceImageRelativePath, value => record.RawImageRelativePath = value);
        changed |= SetIfDifferent(record.AlignedImageRelativePath, alignment.ReferenceImageRelativePath, value => record.AlignedImageRelativePath = value);
        changed |= SetIfDifferent(record.Source, AlignmentTemplateSource, value => record.Source = value);
        changed |= SetIfDifferent(record.OriginalFileName, AlignmentTemplateSource, value => record.OriginalFileName = value);
        changed |= SetIfDifferent(record.AlignmentMessage, AlignmentTemplateSource, value => record.AlignmentMessage = value);

        if (!record.IsProtected)
        {
            record.IsProtected = true;
            changed = true;
        }

        if (!record.AlignmentSucceeded)
        {
            record.AlignmentSucceeded = true;
            changed = true;
        }

        var (width, height) = ResolveAlignmentTemplateSize(alignment);
        if (record.Width != width)
        {
            record.Width = width;
            changed = true;
        }

        if (record.Height != height)
        {
            record.Height = height;
            changed = true;
        }

        record.Labels ??= [];
        foreach (var task in tasks)
        {
            var before = record.Labels.Count;
            CameraTrainingLibraryStore.EnsureLabel(record, task.Id);
            changed |= record.Labels.Count != before;
        }

        return changed;
    }

    private (int Width, int Height) ResolveAlignmentTemplateSize(CameraAlignmentDefinition alignment)
    {
        if (alignment.ImageWidth > 0 && alignment.ImageHeight > 0)
        {
            return (alignment.ImageWidth, alignment.ImageHeight);
        }

        try
        {
            var referencePath = _assetPathService.GetFullPath(alignment.ReferenceImageRelativePath);
            if (!File.Exists(referencePath))
            {
                return (0, 0);
            }

            using var image = Cv2.ImRead(referencePath, ImreadModes.Color);
            return image.Empty() ? (0, 0) : (image.Width, image.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsAlignmentTemplateRecord(TrainingImageRecord record)
    {
        return string.Equals(record.Id, AlignmentTemplateImageId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(record.Source, AlignmentTemplateSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTrainingImageSampleName(TrainingImageRecord image)
    {
        if (!string.IsNullOrWhiteSpace(image.OriginalFileName))
        {
            return image.OriginalFileName;
        }

        if (IsAlignmentTemplateRecord(image))
        {
            return AlignmentTemplateSource;
        }

        return string.IsNullOrWhiteSpace(image.Id) ? "sample" : image.Id;
    }

    private static bool SetIfDifferent(string value, string next, Action<string> assign)
    {
        if (string.Equals(value, next, StringComparison.Ordinal))
        {
            return false;
        }

        assign(next);
        return true;
    }

    public async ValueTask<ClipTrainingTemplateSeedResult> EnsureTemplateOkSampleAsync(
        ProductModelDefinition productModel,
        InspectionTaskDefinition task,
        IReadOnlyList<string> cameraIdCandidates,
        IReadOnlyList<CameraAlignmentDefinition> alignments,
        CancellationToken cancellationToken = default)
    {
        var summary = await LoadSummaryAsync(task, cancellationToken);
        var legacyTemplateSample = ResolveLegacyTemplateSeedSample(summary);
        if (summary.OkSamples.Count > 0 && legacyTemplateSample == null)
        {
            return new ClipTrainingTemplateSeedResult
            {
                Added = false,
                Message = "OK sample library already has samples.",
                Summary = summary
            };
        }

        var alignment = ResolveAlignment(productModel.Id, cameraIdCandidates, alignments);
        if (alignment == null || string.IsNullOrWhiteSpace(alignment.ReferenceImageRelativePath))
        {
            return new ClipTrainingTemplateSeedResult
            {
                Added = false,
                Message = "Template reference image is not registered.",
                Summary = summary
            };
        }

        var referencePath = _assetPathService.GetFullPath(alignment.ReferenceImageRelativePath);
        if (!File.Exists(referencePath))
        {
            return new ClipTrainingTemplateSeedResult
            {
                Added = false,
                Message = $"Template reference image was not found: {referencePath}",
                Summary = summary
            };
        }

        var vectorSet = task.EnsureClipVectorSet();
        string? preparedPath = null;
        var databaseUpdated = false;
        try
        {
            preparedPath = SaveTemplateRoiOkSample(
                productModel,
                task,
                referencePath);
            var store = new SqliteVectorStore(_options.DatabasePath);
            var product = await store.GetProductAsync(vectorSet.VectorSetId, cancellationToken);
            if (product == null)
            {
                await _clipClassificationService.BuildVectorSetAsync(new ClipBuildVectorSetRequest
                {
                    VectorSet = vectorSet,
                    OkImagePaths = [preparedPath],
                    Source = TemplateSeedSource
                }, cancellationToken);
                databaseUpdated = true;
            }
            else
            {
                await _clipClassificationService.AddSamplesAsync(new ClipSampleMaintenanceRequest
                {
                    VectorSet = vectorSet,
                    Label = InspectionJudgment.OK,
                    ImagePaths = [preparedPath],
                    Source = TemplateSeedSource
                }, cancellationToken);
                databaseUpdated = true;
            }

            if (legacyTemplateSample != null)
            {
                await DeleteSampleAsync(legacyTemplateSample, cancellationToken);
            }

            return new ClipTrainingTemplateSeedResult
            {
                Added = true,
                PreparedImagePath = preparedPath,
                Message = legacyTemplateSample == null
                    ? "Template reference image was added as the first OK sample."
                    : "Legacy template OK sample was rebuilt.",
                Summary = await LoadSummaryAsync(task, cancellationToken)
            };
        }
        catch
        {
            if (!databaseUpdated && !string.IsNullOrWhiteSpace(preparedPath))
            {
                CleanupPreparedFiles([preparedPath]);
            }

            throw;
        }
    }

    public async ValueTask<bool> DeleteSampleAsync(
        ClipTrainingSampleInfo sample,
        CancellationToken cancellationToken = default)
    {
        var store = new SqliteVectorStore(_options.DatabasePath);
        var deleted = await store.DeleteSampleAsync(sample.Id, cancellationToken);
        if (deleted && sample.IsManagedFile)
        {
            DeleteManagedFile(sample.ImagePath);
        }

        return deleted;
    }

    public async ValueTask<IReadOnlyList<ClipTrainingSampleInfo>> LoadAllSamplesAsync(
        InspectionTaskDefinition task,
        CancellationToken cancellationToken = default)
    {
        var vectorSet = task.EnsureClipVectorSet();
        var store = new SqliteVectorStore(_options.DatabasePath);
        var product = await store.GetProductAsync(vectorSet.VectorSetId, cancellationToken);
        if (product == null)
        {
            return Array.Empty<ClipTrainingSampleInfo>();
        }

        var samples = await store.ListSamplesAsync(vectorSet.VectorSetId, cancellationToken);
        return samples
            .Where(sample => sample.Kind == "Image" && !string.IsNullOrWhiteSpace(sample.ImagePath))
            .Select(MapSample)
            .OrderByDescending(sample => sample.CreatedAt)
            .ToArray();
    }

    /// <summary>
    /// 重建vector set，重新编码所有样本
    /// </summary>
    private async ValueTask RebuildVectorSetAsync(
        InspectionTaskDefinition task,
        CancellationToken cancellationToken)
    {
        var vectorSet = task.EnsureClipVectorSet();
        var store = new SqliteVectorStore(_options.DatabasePath);
        
        // 获取所有现有样本
        var samples = await store.ListSamplesAsync(vectorSet.VectorSetId, cancellationToken);
        var imageSamples = samples
            .Where(s => s.Kind == "Image" && s.Enabled && !string.IsNullOrWhiteSpace(s.ImagePath))
            .Select(MapSample)
            .ToArray();

        var okPaths = imageSamples
            .Where(s => s.Label == InspectionJudgment.OK)
            .Select(s => s.ImagePath)
            .ToList();

        var ngPaths = imageSamples
            .Where(s => s.Label == InspectionJudgment.NG)
            .Select(s => s.ImagePath)
            .ToList();

        if (okPaths.Count == 0)
        {
            AppDiagnostics.Warn("clip-training", $"Rebuild skipped: {task.Name} has no OK samples");
            return;
        }

        // 删除旧的vector set
        await store.DeleteProductAsync(vectorSet.VectorSetId, cancellationToken);

        // 重建vector set
        await _clipClassificationService.BuildVectorSetAsync(new ClipBuildVectorSetRequest
        {
            VectorSet = vectorSet,
            OkImagePaths = okPaths,
            NgImagePaths = ngPaths,
            Source = "AutoRebuild"
        }, cancellationToken);

        AppDiagnostics.Info("clip-training", 
            $"RebuildVectorSet: {task.Name}, OK={okPaths.Count}, NG={ngPaths.Count}");
    }

    public async ValueTask<bool> RelabelSampleAsync(
        ClipTrainingSampleInfo sample,
        InspectionJudgment label,
        CancellationToken cancellationToken = default)
    {
        var store = new SqliteVectorStore(_options.DatabasePath);
        var changed = await store.UpdateSampleLabelAsync(sample.Id, label.ToString(), cancellationToken);
        if (changed)
        {
            await store.SetSampleEnabledAsync(sample.Id, true, cancellationToken);
        }

        return changed;
    }

    public async ValueTask<bool> SetSampleIgnoredAsync(
        ClipTrainingSampleInfo sample,
        CancellationToken cancellationToken = default)
    {
        var store = new SqliteVectorStore(_options.DatabasePath);
        return await store.SetSampleEnabledAsync(sample.Id, false, cancellationToken);
    }

    public async ValueTask<ClipTrainingClassifyResult> ClassifyAsync(
        ClipTrainingClassifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var vectorSet = request.Task.EnsureClipVectorSet();
        var imagePath = request.InputMode == ClipSampleInputMode.RoiImage
            ? Path.GetFullPath(request.SourceImagePath)
            : PrepareQueryImage(request, request.SourceImagePath);

        var result = await _clipClassificationService.ClassifyAsync(new ClipClassificationRequest
        {
            VectorSet = vectorSet,
            ImagePath = imagePath
        }, cancellationToken);

        return new ClipTrainingClassifyResult
        {
            PreparedImagePath = imagePath,
            Classification = result
        };
    }

    private string PrepareManagedSampleImage(ClipTrainingAddSamplesRequest request, string sourcePath)
    {
        return request.InputMode == ClipSampleInputMode.RoiImage
            ? CopyRoiImage(request, sourcePath)
            : SaveAlignedRoiImage(
                request.ProductModel.Id,
                request.Task,
                request.CameraIdCandidates,
                request.Alignments,
                sourcePath,
                BuildManagedSamplePath(request, sourcePath, ".jpg"));
    }

    private string PrepareQueryImage(ClipTrainingClassifyRequest request, string sourcePath)
    {
        Directory.CreateDirectory(_queryDirectory);
        var targetPath = Path.Combine(
            _queryDirectory,
            $"{SanitizeFileName(request.Task.EnsureClipVectorSet().VectorSetId)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
        return SaveAlignedRoiImage(
            request.ProductModel.Id,
            request.Task,
            request.CameraIdCandidates,
            request.Alignments,
            sourcePath,
            targetPath);
    }

    private string CopyRoiImage(ClipTrainingAddSamplesRequest request, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var targetPath = BuildManagedSamplePath(request, sourcePath, extension);
        File.Copy(Path.GetFullPath(sourcePath), targetPath);
        return targetPath;
    }

    private string SaveAlignedRoiImage(
        string productModelId,
        InspectionTaskDefinition task,
        IReadOnlyList<string> cameraIdCandidates,
        IReadOnlyList<CameraAlignmentDefinition> alignments,
        string sourcePath,
        string targetPath)
    {
        var alignment = ResolveAlignment(productModelId, cameraIdCandidates, alignments)
            ?? throw new InvalidOperationException("未找到当前任务相机的模板对齐配置。");

        using var source = Cv2.ImRead(Path.GetFullPath(sourcePath), ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"图片读取失败：{sourcePath}");
        }

        using var aligned = AlignSourceImage(alignment, source);
        using var roi = ClipFrameImageMaterializer.CropFrame(aligned, task.Roi);
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!Cv2.ImWrite(targetPath, roi))
        {
            throw new InvalidOperationException($"样本图片保存失败：{targetPath}");
        }

        return targetPath;
    }

    private Mat AlignSourceImage(CameraAlignmentDefinition alignment, Mat source)
    {
        if (string.Equals(alignment.AlignmentMode, "跳过补正", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var result = _alignmentService.AlignMatToTemplate(alignment, source);
        if (!result.Success || result.Image == null)
        {
            throw new InvalidOperationException($"整图对齐失败：{result.Message}");
        }

        return result.Image;
    }

    private Mat AlignCameraTrainingImage(CameraAlignmentDefinition alignment, Mat source)
    {
        if (string.Equals(alignment.AlignmentMode, "跳过补正", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var result = _alignmentService.AlignMatToTemplate(alignment, source);
        if (!result.Success || result.Image == null)
        {
            throw new InvalidOperationException($"整图对齐失败：{result.Message}");
        }

        return result.Image;
    }

    private string BuildManagedSamplePath(
        ClipTrainingAddSamplesRequest request,
        string sourcePath,
        string extension)
    {
        var directory = Path.Combine(
            _assetPathService.GetRootFullPath(),
            "Products",
            SanitizePathSegment(request.ProductModel.Id),
            SanitizePathSegment(request.Task.CameraId),
            "clip",
            SanitizePathSegment(request.Task.Id),
            request.Label.ToString());
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "sample";
        }

        var uniqueName =
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SanitizeFileName(fileName)}_{Guid.NewGuid():N}{extension}";
        return Path.Combine(directory, uniqueName);
    }

    private static ClipTrainingSampleInfo? ResolveLegacyTemplateSeedSample(ClipTrainingLibrarySummary summary)
    {
        if (summary.OkSamples.Count != 1 || summary.NgSamples.Count != 0)
        {
            return null;
        }

        var sample = summary.OkSamples[0];
        if (!sample.IsManagedFile ||
            string.Equals(sample.Source, TemplateSeedSource, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(sample.ImagePath);
        if (!fileName.Contains("reference", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return sample.Source is "Build" or "TemplateSeed"
            ? sample
            : null;
    }

    private string SaveTemplateRoiOkSample(
        ProductModelDefinition productModel,
        InspectionTaskDefinition task,
        string referencePath)
    {
        var request = new ClipTrainingAddSamplesRequest
        {
            ProductModel = productModel,
            Task = task,
            CameraIdCandidates = Array.Empty<string>(),
            Alignments = Array.Empty<CameraAlignmentDefinition>(),
            Label = InspectionJudgment.OK,
            InputMode = ClipSampleInputMode.RoiImage,
            SourceImagePaths = [referencePath]
        };
        var targetPath = BuildManagedSamplePath(request, referencePath, ".jpg");

        using var reference = Cv2.ImRead(Path.GetFullPath(referencePath), ImreadModes.Color);
        if (reference.Empty())
        {
            throw new InvalidOperationException($"Template reference image read failed: {referencePath}");
        }

        using var roi = ClipFrameImageMaterializer.CropFrame(reference, task.Roi);
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!Cv2.ImWrite(targetPath, roi))
        {
            throw new InvalidOperationException($"Template OK sample save failed: {targetPath}");
        }

        return targetPath;
    }

    private CameraAlignmentDefinition? ResolveAlignment(
        string productModelId,
        IReadOnlyList<string> cameraIdCandidates,
        IReadOnlyList<CameraAlignmentDefinition> alignments)
    {
        return alignments.FirstOrDefault(alignment =>
            string.Equals(alignment.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
            cameraIdCandidates.Any(candidate =>
                string.Equals(alignment.CameraId, candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static CameraAlignmentDefinition? ResolveCameraAlignment(
        string productModelId,
        string cameraId,
        string cameraName,
        IReadOnlyList<CameraAlignmentDefinition> alignments)
    {
        return alignments.FirstOrDefault(alignment =>
            string.Equals(alignment.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(alignment.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(alignment.CameraId, cameraName, StringComparison.OrdinalIgnoreCase)));
    }

    private ClipTrainingSampleInfo MapSample(SqliteVectorSample sample)
    {
        return new ClipTrainingSampleInfo
        {
            Id = sample.Id,
            Label = string.Equals(sample.Label, "OK", StringComparison.OrdinalIgnoreCase)
                ? InspectionJudgment.OK
                : InspectionJudgment.NG,
            Enabled = sample.Enabled,
            ImagePath = sample.ImagePath ?? string.Empty,
            Source = sample.Source,
            CreatedAt = TryParseTimestamp(sample.CreatedAt),
            IsManagedFile = IsManagedClipFile(sample.ImagePath)
        };
    }

    private bool IsManagedClipFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var root = _assetPathService.GetRootFullPath();
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return false;
        }

        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "clip", StringComparison.OrdinalIgnoreCase));
    }

    private void DeleteCameraTrainingImageFiles(
        CameraTrainingLibraryDocument document,
        TrainingImageRecord record)
    {
        TryDeleteCameraTrainingImageFile(document, record.RawImageRelativePath);
        TryDeleteCameraTrainingImageFile(document, record.AlignedImageRelativePath);
    }

    private void TryDeleteCameraTrainingImageFile(
        CameraTrainingLibraryDocument document,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(_cameraTrainingStore.GetFullPath(relativePath));
            var trainingDirectory = Path.GetFullPath(
                _cameraTrainingStore.GetTrainingDirectory(document.ProductModelId, document.CameraId));
            if (!fullPath.StartsWith(
                    trainingDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Removing the annotation is the source of truth; stale image files can be cleaned up later.
        }
    }

    private void DeleteManagedFile(string path)
    {
        if (!IsManagedClipFile(path) || !File.Exists(path))
        {
            return;
        }

        File.Delete(path);
    }

    private static void CleanupPreparedFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup after a failed import.
            }
        }
    }

    private static DateTimeOffset? TryParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
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

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "sample";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
