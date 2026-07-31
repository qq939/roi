using System.IO;
using ClipInspect.Storage;
using ClipInspect.Storage.Sqlite;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.Services;

public sealed record ProductModelCopyResult(
    ProductModelDefinition Product,
    int Copied,
    int Skipped);

public sealed record ProductModelDeleteResult(
    string SelectedProductModelId,
    IReadOnlyList<string> CleanupWarnings);

public sealed class ProductModelService
{
    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly InspectionConfigurationStorage _storage;
    private readonly VisionAssetPathService _assetPathService;
    private readonly AlignmentTemplateStore _alignmentTemplateStore;
    private readonly ClipRuntimeOptions _clipOptions;

    public ProductModelService(
        InspectionWorkspaceConfiguration configuration,
        InspectionConfigurationStorage storage,
        VisionAssetPathService assetPathService,
        AlignmentTemplateStore alignmentTemplateStore,
        ClipRuntimeOptions? clipOptions = null)
    {
        _configuration = configuration;
        _storage = storage;
        _assetPathService = assetPathService;
        _alignmentTemplateStore = alignmentTemplateStore;
        _clipOptions = clipOptions ?? new ClipRuntimeOptions();
    }

    public ProductModelDefinition CreateProduct(string productCode, string productName)
    {
        ValidateNewProduct(productCode, productName, out var code, out var name);

        var product = new ProductModelDefinition
        {
            Id = code,
            Name = name
        };

        _configuration.ProductModels.Add(product);
        _configuration.SelectedProductModelId = product.Id;
        SaveConfiguration();
        return product;
    }

    public async Task<ProductModelCopyResult> CopyProductAsync(
        ProductModelDefinition sourceProduct,
        string targetProductCode,
        string targetProductName)
    {
        EnsureExistingProduct(sourceProduct);
        ValidateNewProduct(targetProductCode, targetProductName, out var code, out var name);

        var assetsCopied = false;
        var targetVectorSetIds = new List<string>();
        try
        {
            assetsCopied = CopyProductAssets(sourceProduct.Id, code);

            var product = new ProductModelDefinition
            {
                Id = code,
                Name = name,
                Enabled = sourceProduct.Enabled
            };

            var alignmentCopies = _configuration.Alignments
                .Where(alignment => string.Equals(alignment.ProductModelId, sourceProduct.Id, StringComparison.OrdinalIgnoreCase))
                .Select(alignment => CloneAlignment(alignment, sourceProduct.Id, code))
                .ToArray();

            var clipMappings = new List<(string SourceVectorSetId, string TargetVectorSetId)>();
            var taskCopies = _configuration.Tasks
                .Where(task => string.Equals(task.ProductModelId, sourceProduct.Id, StringComparison.OrdinalIgnoreCase))
                .Select(task => CloneTask(task, code, clipMappings))
                .ToArray();

            targetVectorSetIds.AddRange(clipMappings.Select(mapping => mapping.TargetVectorSetId));
            var clipCopyResult = await CopyClipVectorSetsAsync(
                clipMappings,
                sourceProduct.Id,
                code,
                _assetPathService,
                _clipOptions.DatabasePath);
            _alignmentTemplateStore.CopyProduct(sourceProduct.Id, code);

            _configuration.ProductModels.Add(product);
            _configuration.Alignments.AddRange(alignmentCopies);
            _configuration.Tasks.AddRange(taskCopies);
            _configuration.SelectedProductModelId = product.Id;
            SaveConfiguration();
            return new ProductModelCopyResult(product, clipCopyResult.Copied, clipCopyResult.Skipped);
        }
        catch
        {
            await DeleteClipVectorSetsByIdsAsync(targetVectorSetIds, _clipOptions.DatabasePath);
            _alignmentTemplateStore.DeleteProduct(code);
            if (assetsCopied)
            {
                DeleteProductAssets(code);
            }

            _configuration.ProductModels.RemoveAll(product =>
                string.Equals(product.Id, code, StringComparison.OrdinalIgnoreCase));
            _configuration.Alignments.RemoveAll(alignment =>
                string.Equals(alignment.ProductModelId, code, StringComparison.OrdinalIgnoreCase));
            _configuration.Tasks.RemoveAll(task =>
                string.Equals(task.ProductModelId, code, StringComparison.OrdinalIgnoreCase));
            throw;
        }
    }

    public async Task<ProductModelDeleteResult> DeleteProductAsync(ProductModelDefinition product)
    {
        EnsureCanDeleteProduct(product);
        var originalProductModels = _configuration.ProductModels.ToList();
        var originalAlignments = _configuration.Alignments.ToList();
        var originalTasks = _configuration.Tasks.ToList();
        var originalSelectedProductModelId = _configuration.SelectedProductModelId;
        var productId = product.Id;

        var tasks = _configuration.Tasks
            .Where(task => string.Equals(task.ProductModelId, productId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var vectorSetIds = ResolveClipVectorSetIds(tasks);

        _configuration.ProductModels.RemoveAll(item =>
            string.Equals(item.Id, productId, StringComparison.OrdinalIgnoreCase));
        _configuration.Alignments.RemoveAll(alignment =>
            string.Equals(alignment.ProductModelId, productId, StringComparison.OrdinalIgnoreCase));
        _configuration.Tasks.RemoveAll(task =>
            string.Equals(task.ProductModelId, productId, StringComparison.OrdinalIgnoreCase));
        if (_configuration.SelectedProductModelId.Equals(productId, StringComparison.OrdinalIgnoreCase))
        {
            _configuration.SelectedProductModelId = _configuration.ProductModels.FirstOrDefault()?.Id ?? string.Empty;
        }

        try
        {
            SaveConfiguration();
        }
        catch
        {
            _configuration.ProductModels = originalProductModels;
            _configuration.Alignments = originalAlignments;
            _configuration.Tasks = originalTasks;
            _configuration.SelectedProductModelId = originalSelectedProductModelId;
            throw;
        }

        var warnings = new List<string>();
        await TryCleanupAsync(
            () => DeleteClipVectorSetsByIdsAsync(vectorSetIds, _clipOptions.DatabasePath),
            "CLIP 向量库清理失败",
            warnings);
        TryCleanup(
            () => _alignmentTemplateStore.DeleteProduct(productId),
            "对齐模板清理失败",
            warnings);
        TryCleanup(
            () => DeleteProductAssets(productId),
            "型号资源目录清理失败",
            warnings);

        return new ProductModelDeleteResult(_configuration.SelectedProductModelId, warnings);
    }

    private void SaveConfiguration()
    {
        _storage.Save(_configuration);
    }

    private CameraAlignmentDefinition CloneAlignment(
        CameraAlignmentDefinition source,
        string sourceProductId,
        string targetProductId)
    {
        return new CameraAlignmentDefinition
        {
            ProductModelId = targetProductId,
            CameraId = source.CameraId,
            Enabled = source.Enabled,
            ReferenceImageRelativePath = RetargetProductRelativePath(source.ReferenceImageRelativePath, sourceProductId, targetProductId),
            TemplateRelativePath = RetargetProductRelativePath(source.TemplateRelativePath, sourceProductId, targetProductId),
            PreviewRelativePath = RetargetProductRelativePath(source.PreviewRelativePath, sourceProductId, targetProductId),
            FeatureMethod = source.FeatureMethod,
            TransformModel = source.TransformModel,
            AlignmentMode = source.AlignmentMode,
            AlignmentMethod = source.AlignmentMethod,
            ImageWidth = source.ImageWidth,
            ImageHeight = source.ImageHeight,
            KeyPointCount = source.KeyPointCount,
            DescriptorRows = source.DescriptorRows,
            DescriptorCols = source.DescriptorCols,
            DescriptorMatType = source.DescriptorMatType,
            MaxLongSide = source.MaxLongSide,
            MaxFeatures = source.MaxFeatures,
            LoweRatio = source.LoweRatio,
            MinGoodMatches = source.MinGoodMatches,
            MinInliers = source.MinInliers,
            MinInlierRatio = source.MinInlierRatio,
            RansacReprojectionThreshold = source.RansacReprojectionThreshold,
            MaxReprojectionRmse = source.MaxReprojectionRmse,
            RegisteredFeatureMethod = source.RegisteredFeatureMethod,
            RegisteredAlignmentMethod = source.RegisteredAlignmentMethod,
            RegisteredMaxLongSide = source.RegisteredMaxLongSide,
            RegisteredMaxFeatures = source.RegisteredMaxFeatures,
            EffectiveAlignmentRegion = source.EffectiveAlignmentRegion?.Clone(),
            RegisteredEffectiveAlignmentRegion = source.RegisteredEffectiveAlignmentRegion?.Clone(),
            RegisteredAt = source.RegisteredAt
        };
    }

    private static InspectionTaskDefinition CloneTask(
        InspectionTaskDefinition source,
        string targetProductId,
        List<(string SourceVectorSetId, string TargetVectorSetId)> clipMappings)
    {
        var target = new InspectionTaskDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = source.Name,
            ProductModelId = targetProductId,
            CameraId = source.CameraId,
            Kind = source.Kind,
            Enabled = source.Enabled,
            Roi = CloneRoi(source.Roi),
            Measurement = CloneMeasurement(source.EnsureMeasurementOptions())
        };

        if (source.Clip != null)
        {
            var sourceVectorSetId = string.IsNullOrWhiteSpace(source.Clip.VectorSetId)
                ? ClipVectorSetDefinition.BuildId(source.ProductModelId, source.CameraId, source.Id)
                : source.Clip.VectorSetId;
            var targetVectorSetId = ClipVectorSetDefinition.BuildId(targetProductId, target.CameraId, target.Id);

            target.Clip = new ClipVectorSetDefinition
            {
                VectorSetId = targetVectorSetId,
                ProductModelId = targetProductId,
                CameraId = target.CameraId,
                TaskId = target.Id,
                DisplayName = string.IsNullOrWhiteSpace(source.Clip.DisplayName) ? source.Name : source.Clip.DisplayName,
                BackboneType = source.Clip.BackboneType,
                TopK = source.Clip.TopK,
                Threshold = source.Clip.Threshold,
                ModelName = source.Clip.ModelName,
                Pretrained = source.Clip.Pretrained
            };

            clipMappings.Add((sourceVectorSetId, targetVectorSetId));
        }
        else if (source.Kind == InspectionTaskKind.Classification)
        {
            target.EnsureClipVectorSet();
        }

        return target;
    }

    private static RoiRegion CloneRoi(RoiRegion source)
    {
        return new RoiRegion
        {
            Id = source.Id,
            Name = source.Name,
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            AngleDegrees = source.AngleDegrees
        };
    }

    private static MeasurementOptions CloneMeasurement(MeasurementOptions source)
    {
        return new MeasurementOptions
        {
            FirstEdgePolarity = source.FirstEdgePolarity,
            SecondEdgePolarity = source.SecondEdgePolarity,
            MinDistancePx = source.MinDistancePx,
            MaxDistancePx = source.MaxDistancePx,
            PixelToMillimeterScale = source.PixelToMillimeterScale,
            MinDistanceMm = source.MinDistanceMm,
            MaxDistanceMm = source.MaxDistanceMm,
            EdgeThreshold = source.EdgeThreshold,
            SmoothWindow = source.SmoothWindow,
            MinEdgeGapPx = source.MinEdgeGapPx
        };
    }

    private string RetargetProductRelativePath(string sourceRelativePath, string sourceProductId, string targetProductId)
    {
        if (string.IsNullOrWhiteSpace(sourceRelativePath))
        {
            return string.Empty;
        }

        var sourcePrefix = NormalizeRelativePath(_assetPathService.GetProductRelativeDirectory(sourceProductId));
        var targetPrefix = NormalizeRelativePath(_assetPathService.GetProductRelativeDirectory(targetProductId));
        var relativePath = NormalizeRelativePath(sourceRelativePath);

        if (relativePath.Equals(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return targetPrefix.Replace('/', Path.DirectorySeparatorChar);
        }

        if (!relativePath.StartsWith(sourcePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return (targetPrefix + relativePath[sourcePrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private void ValidateNewProduct(
        string productCode,
        string productName,
        out string normalizedProductCode,
        out string normalizedProductName)
    {
        normalizedProductCode = productCode.Trim();
        normalizedProductName = productName.Trim();

        if (string.IsNullOrWhiteSpace(normalizedProductCode))
        {
            throw new InvalidOperationException("成品号不能为空");
        }

        if (string.IsNullOrWhiteSpace(normalizedProductName))
        {
            throw new InvalidOperationException("名称不能为空");
        }

        var productCodeValue = normalizedProductCode;
        var productNameValue = normalizedProductName;
        if (_configuration.ProductModels.Any(product =>
                string.Equals(product.Id.Trim(), productCodeValue, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("成品号已存在");
        }

        if (_configuration.ProductModels.Any(product =>
                string.Equals(product.Name.Trim(), productNameValue, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("名称已存在");
        }
    }

    private void EnsureExistingProduct(ProductModelDefinition product)
    {
        if (_configuration.ProductModels.All(item =>
                !string.Equals(item.Id, product.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("源型号不存在");
        }
    }

    private void EnsureCanDeleteProduct(ProductModelDefinition product)
    {
        EnsureExistingProduct(product);
        if (_configuration.ProductModels.Count <= 1)
        {
            throw new InvalidOperationException("至少保留一个型号");
        }
    }

    private bool CopyProductAssets(string sourceProductId, string targetProductId)
    {
        var targetDirectory = _assetPathService.GetProductDirectory(targetProductId);
        if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            throw new InvalidOperationException($"目标资源目录已存在：{targetDirectory}");
        }

        var sourceDirectory = _assetPathService.GetProductDirectory(sourceProductId);
        if (!Directory.Exists(sourceDirectory))
        {
            return false;
        }

        CopyDirectory(sourceDirectory, targetDirectory);
        return true;
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, targetPath, overwrite: false);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, targetPath);
        }
    }

    private void DeleteProductAssets(string productId)
    {
        var productDirectory = _assetPathService.GetProductDirectory(productId);
        if (Directory.Exists(productDirectory))
        {
            Directory.Delete(productDirectory, recursive: true);
        }
    }

    private static async Task<(int Copied, int Skipped)> CopyClipVectorSetsAsync(
        IReadOnlyCollection<(string SourceVectorSetId, string TargetVectorSetId)> mappings,
        string sourceProductId,
        string targetProductId,
        VisionAssetPathService assetPathService,
        string databasePath)
    {
        if (mappings.Count == 0)
        {
            return (0, 0);
        }

        var store = new SqliteVectorStore(databasePath);
        var copied = 0;
        var skipped = 0;
        foreach (var mapping in mappings.Distinct())
        {
            try
            {
                var sourceCache = await store.LoadCacheAsync(mapping.SourceVectorSetId);
                var targetCache = new ClipCache
                {
                    ProductId = mapping.TargetVectorSetId,
                    ModelName = sourceCache.ModelName,
                    Pretrained = sourceCache.Pretrained,
                    FeatureDim = sourceCache.FeatureDim,
                    TopK = sourceCache.TopK,
                    Threshold = sourceCache.Threshold,
                    TextWeight = sourceCache.TextWeight,
                    OkItems = RetargetImageItems(sourceCache.OkItems, sourceProductId, targetProductId, assetPathService),
                    NgItems = RetargetImageItems(sourceCache.NgItems, sourceProductId, targetProductId, assetPathService),
                    OkTextItems = sourceCache.OkTextItems,
                    NgTextItems = sourceCache.NgTextItems
                };

                await store.ImportCacheAsync(targetCache);
                copied++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
            }
        }

        return (copied, skipped);
    }

    private static IReadOnlyList<ImageCacheItem> RetargetImageItems(
        IReadOnlyList<ImageCacheItem> items,
        string sourceProductId,
        string targetProductId,
        VisionAssetPathService assetPathService)
    {
        return items
            .Select(item => new ImageCacheItem
            {
                ImagePath = RetargetProductPath(item.ImagePath, sourceProductId, targetProductId, assetPathService),
                Feature = item.Feature
            })
            .ToArray();
    }

    private static string RetargetProductPath(
        string path,
        string sourceProductId,
        string targetProductId,
        VisionAssetPathService assetPathService)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path))
        {
            var fullPath = Path.GetFullPath(path);
            var sourceDirectory = Path.GetFullPath(assetPathService.GetProductDirectory(sourceProductId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetDirectory = Path.GetFullPath(assetPathService.GetProductDirectory(targetProductId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (fullPath.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return targetDirectory;
            }

            if (fullPath.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(sourceDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(targetDirectory, fullPath[(sourceDirectory.Length + 1)..]);
            }

            return path;
        }

        var sourcePrefix = NormalizeRelativePath(assetPathService.GetProductRelativeDirectory(sourceProductId));
        var targetPrefix = NormalizeRelativePath(assetPathService.GetProductRelativeDirectory(targetProductId));
        var relativePath = NormalizeRelativePath(path);
        if (relativePath.Equals(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return targetPrefix.Replace('/', Path.DirectorySeparatorChar);
        }

        if (relativePath.StartsWith(sourcePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (targetPrefix + relativePath[sourcePrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
        }

        return path;
    }

    private static string[] ResolveClipVectorSetIds(IEnumerable<InspectionTaskDefinition> tasks)
    {
        return tasks
            .Select(ResolveClipVectorSetId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static async Task TryCleanupAsync(
        Func<Task> cleanup,
        string operationName,
        ICollection<string> warnings)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex)
        {
            warnings.Add($"{operationName}：{ex.Message}");
        }
    }

    private static void TryCleanup(
        Action cleanup,
        string operationName,
        ICollection<string> warnings)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            warnings.Add($"{operationName}：{ex.Message}");
        }
    }

    private static async Task DeleteClipVectorSetsAsync(
        IEnumerable<InspectionTaskDefinition> tasks,
        string databasePath)
    {
        await DeleteClipVectorSetsByIdsAsync(ResolveClipVectorSetIds(tasks), databasePath);
    }

    private static async Task DeleteClipVectorSetsByIdsAsync(
        IEnumerable<string> vectorSetIds,
        string databasePath)
    {
        var ids = vectorSetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var store = new SqliteVectorStore(databasePath);
        foreach (var vectorSetId in ids)
        {
            await store.DeleteProductAsync(vectorSetId);
        }
    }

    private static string? ResolveClipVectorSetId(InspectionTaskDefinition task)
    {
        if (task.Clip == null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(task.Clip.VectorSetId)
            ? ClipVectorSetDefinition.BuildId(task.ProductModelId, task.CameraId, task.Id)
            : task.Clip.VectorSetId;
    }
}
