using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageBox;
using OpenCvSharp;
using RoiAlignment.Core;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;
using Point = System.Windows.Point;

namespace VisionWorkbench.ViewModels;

public sealed partial class ProductModelManagementViewModel : ObservableObject
{
    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly ObservableCollection<CameraViewModel> _cameras;
    private readonly CameraAcquisitionService _cameraService;
    private readonly InspectionConfigurationStorage _storage;
    private readonly VisionAssetPathService _assetPathService;
    private readonly AlignmentTemplateStore _alignmentTemplateStore;
    private readonly ProductModelService _productModelService;
    private readonly BarcodeScannerSerialPortService _barcodeScanner;
    private readonly IUserDialogService _dialogService;
    private readonly RuntimeInspectionContext _inspectionContext;
    private bool _isEffectiveAlignmentRegionSelected;

    public ProductModelManagementViewModel(
        InspectionWorkspaceConfiguration configuration,
        ObservableCollection<CameraViewModel> cameras,
        CameraAcquisitionService cameraService,
        InspectionConfigurationStorage storage,
        BarcodeScannerSerialPortService barcodeScanner,
        IUserDialogService dialogService,
        VisionAssetPathService assetPathService,
        ClipRuntimeOptions clipOptions,
        RuntimeInspectionContext inspectionContext)
    {
        _configuration = configuration;
        _cameras = cameras;
        _cameraService = cameraService;
        _storage = storage;
        _barcodeScanner = barcodeScanner;
        _dialogService = dialogService;
        _assetPathService = assetPathService;
        _inspectionContext = inspectionContext;
        _alignmentTemplateStore = new AlignmentTemplateStore(_assetPathService.GetAlignmentTemplateDatabasePath());
        _productModelService = new ProductModelService(_configuration, _storage, _assetPathService, _alignmentTemplateStore, clipOptions);
        ProductModels = new ObservableCollection<ProductModelDefinition>(_configuration.ProductModels);
        selectedProductModel = ProductModels.FirstOrDefault(product =>
            string.Equals(product.Id, _configuration.SelectedProductModelId, StringComparison.OrdinalIgnoreCase))
            ?? ProductModels.FirstOrDefault();
        CameraTemplates = [];
        RefreshCameraTemplates();

        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;

        // 初始化时同步当前值
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(SerialNumber));
        SyncProductCodeToSelection();
        SyncCameraToSelection();
    }

    public string ProductCode
    {
        get => _inspectionContext.ProductCode;
        set
        {
            if (_inspectionContext.ProductCode != value)
            {
                _inspectionContext.ProductCode = value;
            }
        }
    }

    public string SerialNumber
    {
        get => _inspectionContext.SerialNumber;
        set
        {
            if (_inspectionContext.SerialNumber != value)
            {
                _inspectionContext.SerialNumber = value;
            }
        }
    }

    private void OnInspectionContextProductCodeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ProductCode));
        SyncProductCodeToSelection();
    }

    private void OnInspectionContextSerialNumberChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SerialNumber));
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        AppDiagnostics.Debug("product-model-mgmt", $"OnInspectionContextSelectedCameraChanged: context={_inspectionContext.SelectedCamera?.Name ?? "null"}");
        SyncCameraToSelection();
    }

    private void SyncProductCodeToSelection()
    {
        var productCode = _inspectionContext.ProductCode;
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var match = ProductModels.FirstOrDefault(p => string.Equals(p.Id, productCode, StringComparison.OrdinalIgnoreCase));
            if (match != null && !ReferenceEquals(SelectedProductModel, match))
            {
                SelectedProductModel = match;
            }
        }
    }

    private void SyncCameraToSelection()
    {
        var camera = _inspectionContext.SelectedCamera;
        if (camera == null)
        {
            return;
        }
        foreach (var template in CameraTemplates)
        {
            if (ReferenceEquals(template.Camera, camera))
            {
                SelectCameraTemplate(template);
                return;
            }
        }
    }

    public void RefreshPublicParams()
    {
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(SerialNumber));
        SyncProductCodeToSelection();
        SyncCameraToSelection();
        AppDiagnostics.Debug("product-model-mgmt", $"RefreshPublicParams: ProductCode={ProductCode}, SerialNumber={SerialNumber}, SelectedProductModel={SelectedProductModel?.Id ?? "null"}, SelectedCamera={_inspectionContext.SelectedCamera?.Name ?? "null"}");
    }

    public ObservableCollection<ProductModelDefinition> ProductModels { get; }

    public ObservableCollection<CameraTemplateViewModel> CameraTemplates { get; }

    public ObservableCollection<ImageOverlayItem> EffectiveAlignmentOverlays { get; } = [];

    public IReadOnlyList<string> FeatureMethodOptions { get; } = ["ORB", "SIFT", "AKAZE", "NCC+AKAZE"];

    public IReadOnlyList<string> AlignmentModeOptions { get; } = ["整图匹配", "有效区域匹配", "跳过补正"];

    public BarcodeScannerSerialPortService BarcodeScanner => _barcodeScanner;

    public event EventHandler? ProductModelsChanged;

    public event EventHandler<ProductModelDefinition>? ProductModelChanged;

    public event EventHandler<string>? AlarmRaised;

    public event EventHandler? AlarmCleared;

    [ObservableProperty]
    private ProductModelDefinition? selectedProductModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCameraTemplate))]
    [NotifyPropertyChangedFor(nameof(CanEditEffectiveAlignmentRegion))]
    [NotifyPropertyChangedFor(nameof(CanClearEffectiveAlignmentRegion))]
    private CameraTemplateViewModel? selectedCameraTemplate;

    [ObservableProperty]
    private ImageBoxInteractionMode templateImageInteractionMode = ImageBoxInteractionMode.Pan;

    [ObservableProperty]
    private string operationMessage = "选择型号和相机模板";

    public bool HasSelectedCameraTemplate => SelectedCameraTemplate != null;

    public bool CanEditEffectiveAlignmentRegion =>
        SelectedCameraTemplate is { ReferenceImage: not null, IsBusy: false };

    public bool CanClearEffectiveAlignmentRegion =>
        CanEditEffectiveAlignmentRegion && SelectedCameraTemplate!.HasEffectiveAlignmentRegion;

    partial void OnSelectedProductModelChanged(ProductModelDefinition? value)
    {
        if (value == null)
        {
            return;
        }

        _configuration.SelectedProductModelId = value.Id;
        RefreshCameraTemplates();
        PersistConfiguration();
        ProductModelChanged?.Invoke(this, value);
    }

    partial void OnSelectedCameraTemplateChanged(CameraTemplateViewModel? value)
    {
        _isEffectiveAlignmentRegionSelected = false;
        TemplateImageInteractionMode = ImageBoxInteractionMode.Pan;
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
    }

    [RelayCommand]
    private void CreateProductModel()
    {
        var dialogResult = _dialogService.ShowCreateProductModelDialog(ProductModels, _barcodeScanner);
        if (dialogResult == null)
        {
            return;
        }

        if (!TryValidateNewProduct(dialogResult.ProductCode, dialogResult.ProductName, out var message))
        {
            OperationMessage = message;
            return;
        }

        var product = _productModelService.CreateProduct(dialogResult.ProductCode, dialogResult.ProductName);
        ProductModels.Add(product);
        SelectedProductModel = product;
        OperationMessage = "型号已创建";
    }

    [RelayCommand]
    private async Task CopyProductModelAsync()
    {
        var dialogResult = _dialogService.ShowCopyProductModelDialog(ProductModels, SelectedProductModel, _barcodeScanner);
        if (dialogResult == null)
        {
            return;
        }

        if (!TryValidateNewProduct(dialogResult.ProductCode, dialogResult.ProductName, out var message))
        {
            OperationMessage = message;
            return;
        }

        try
        {
            var result = await _productModelService.CopyProductAsync(
                dialogResult.SourceProduct,
                dialogResult.ProductCode,
                dialogResult.ProductName);
            ProductModels.Add(result.Product);
            SelectedProductModel = result.Product;
            OperationMessage = result.Skipped > 0
                ? $"型号已复制，CLIP 跳过 {result.Skipped} 个未训练任务"
                : "型号已复制";
        }
        catch (Exception ex)
        {
            OperationMessage = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectScannedProductModel()
    {
        var productCode = _barcodeScanner.LastProductCode.Trim();
        if (string.IsNullOrWhiteSpace(productCode))
        {
            OperationMessage = "扫码枪当前成品号为空";
            return;
        }

        var product = ProductModels.FirstOrDefault(item =>
            string.Equals(item.Id, productCode, StringComparison.OrdinalIgnoreCase));
        if (product == null)
        {
            OperationMessage = $"未找到成品号：{productCode}";
            return;
        }

        SelectedProductModel = product;
        OperationMessage = "已切换到扫码成品号";
    }

    [RelayCommand]
    private void RenameProductModel()
    {
        if (SelectedProductModel == null)
        {
            return;
        }

        var productName = _dialogService.ShowRenameProductModelDialog(ProductModels, SelectedProductModel);
        if (productName == null)
        {
            return;
        }

        if (string.Equals(SelectedProductModel.Name, productName, StringComparison.Ordinal))
        {
            return;
        }

        SelectedProductModel.Name = productName;
        OperationMessage = "名称已保存";
        PersistConfiguration();
    }

    [RelayCommand]
    private async Task DeleteProductModelAsync()
    {
        if (SelectedProductModel == null || ProductModels.Count <= 1)
        {
            OperationMessage = "至少保留一个型号";
            return;
        }

        if (!_dialogService.ShowDeleteProductModelDialog(SelectedProductModel, _barcodeScanner))
        {
            return;
        }

        try
        {
            var deleted = SelectedProductModel;
            var result = await _productModelService.DeleteProductAsync(deleted);
            ProductModels.Remove(deleted);
            SelectedProductModel =
                ProductModels.FirstOrDefault(product =>
                    string.Equals(product.Id, result.SelectedProductModelId, StringComparison.OrdinalIgnoreCase))
                ?? ProductModels[0];
            OperationMessage = result.CleanupWarnings.Count > 0
                ? $"型号已删除，资源清理警告：{string.Join("；", result.CleanupWarnings)}"
                : "型号已删除";
        }
        catch (Exception ex)
        {
            OperationMessage = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveProductModels()
    {
        PersistConfiguration();
        OperationMessage = "型号配置已保存";
    }

    private void PersistConfiguration()
    {
        _configuration.ProductModels = ProductModels.ToList();
        if (SelectedProductModel != null)
        {
            _configuration.SelectedProductModelId = SelectedProductModel.Id;
        }

        _storage.Save(_configuration);
        ProductModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectCameraTemplate(CameraTemplateViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        foreach (var template in CameraTemplates)
        {
            template.IsSelected = ReferenceEquals(template, item);
        }

        SelectedCameraTemplate = item;

        // 反向同步到顶部公共参数
        if (!ReferenceEquals(_inspectionContext.SelectedCamera, item.Camera))
        {
            _inspectionContext.SelectedCamera = item.Camera;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditEffectiveAlignmentRegion))]
    private void DrawEffectiveAlignmentRegion()
    {
        if (SelectedCameraTemplate == null)
        {
            return;
        }

        TemplateImageInteractionMode = ImageBoxInteractionMode.DrawRectangle;
        _isEffectiveAlignmentRegionSelected = false;
        RefreshEffectiveAlignmentOverlays();
        OperationMessage = "请在模板图上拖动绘制有效区域";
        ClearAlarm();
    }

    [RelayCommand(CanExecute = nameof(CanClearEffectiveAlignmentRegion))]
    private void ClearEffectiveAlignmentRegion()
    {
        if (SelectedCameraTemplate == null || !SelectedCameraTemplate.ClearEffectiveAlignmentRegion())
        {
            return;
        }

        PersistConfiguration();
        _isEffectiveAlignmentRegionSelected = false;
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
        OperationMessage = "有效区域已清除，将使用整图；请重新创建模板";
    }

    public void CompleteEffectiveAlignmentRegionDraw(IReadOnlyList<Point> points)
    {
        if (TemplateImageInteractionMode != ImageBoxInteractionMode.DrawRectangle ||
            SelectedCameraTemplate == null)
        {
            return;
        }

        TemplateImageInteractionMode = ImageBoxInteractionMode.Pan;
        if (points.Count < 2 || !SelectedCameraTemplate.SetEffectiveAlignmentRegion(
                points[0].X,
                points[0].Y,
                points[1].X,
                points[1].Y))
        {
            _isEffectiveAlignmentRegionSelected = false;
            RefreshEffectiveAlignmentOverlays();
            OperationMessage = $"有效区域至少需要 {AlignmentEffectiveRegion.MinimumSize:0} x {AlignmentEffectiveRegion.MinimumSize:0} 像素";
            RefreshEffectiveAlignmentRegionCommandStates();
            return;
        }

        PersistConfiguration();
        _isEffectiveAlignmentRegionSelected = true;
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
        OperationMessage = "有效区域已保存，请重新创建模板";
        ClearAlarm();
    }

    public void RejectEffectiveAlignmentRegionDraw(string message)
    {
        if (TemplateImageInteractionMode != ImageBoxInteractionMode.DrawRectangle)
        {
            return;
        }

        TemplateImageInteractionMode = ImageBoxInteractionMode.Pan;
        OperationMessage = message;
    }

    public void SelectEffectiveAlignmentRegion(string id)
    {
        if (!string.Equals(id, "effective-alignment-region", StringComparison.Ordinal) ||
            SelectedCameraTemplate?.HasEffectiveAlignmentRegion != true)
        {
            return;
        }

        TemplateImageInteractionMode = ImageBoxInteractionMode.Pan;
        _isEffectiveAlignmentRegionSelected = true;
        RefreshEffectiveAlignmentOverlays();
    }

    public void CompleteEffectiveAlignmentRegionEdit(
        string id,
        double x,
        double y,
        double width,
        double height)
    {
        if (!string.Equals(id, "effective-alignment-region", StringComparison.Ordinal) ||
            SelectedCameraTemplate == null)
        {
            return;
        }

        if (!SelectedCameraTemplate.SetEffectiveAlignmentRegion(x, y, x + width, y + height))
        {
            OperationMessage = $"有效区域至少需要 {AlignmentEffectiveRegion.MinimumSize:0} x {AlignmentEffectiveRegion.MinimumSize:0} 像素";
            _isEffectiveAlignmentRegionSelected = false;
            RefreshEffectiveAlignmentOverlays();
            RefreshEffectiveAlignmentRegionCommandStates();
            return;
        }

        PersistConfiguration();
        _isEffectiveAlignmentRegionSelected = true;
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
        OperationMessage = "有效区域已更新，请重新创建模板";
    }

    [RelayCommand]
    private async Task CaptureAllTemplatesAsync()
    {
        var success = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var template in CameraTemplates)
        {
            if (!CanAcquireTemplate(template))
            {
                skipped++;
                continue;
            }

            if (await CaptureTemplateAsync(template))
            {
                success++;
            }
            else
            {
                failed++;
            }
        }

        OperationMessage = skipped > 0
            ? $"拍照完成：成功 {success}，失败 {failed}，跳过 {skipped}"
            : $"拍照完成：成功 {success}，失败 {failed}";
        if (success > 0)
        {
            PersistConfiguration();
        }
    }

    [RelayCommand]
    private async Task CaptureSelectedTemplateAsync()
    {
        if (SelectedCameraTemplate == null)
        {
            return;
        }

        if (!CanAcquireTemplate(SelectedCameraTemplate))
        {
            OperationMessage = $"{SelectedCameraTemplate.CameraName} 未启用或未选择设备";
            return;
        }

        if (await CaptureTemplateAsync(SelectedCameraTemplate))
        {
            PersistConfiguration();
            OperationMessage = $"{SelectedCameraTemplate.CameraName} 已拍照";
        }
        else
        {
            OperationMessage = $"{SelectedCameraTemplate.CameraName} 拍照失败";
        }
    }

    [RelayCommand]
    private async Task LoadSelectedReferenceImageAsync()
    {
        if (SelectedCameraTemplate == null)
        {
            return;
        }

        var imagePaths = _dialogService.OpenImageFiles(multiselect: false);
        if (imagePaths.Count == 0)
        {
            return;
        }

        var template = SelectedCameraTemplate;
        template.IsBusy = true;
        template.LastError = string.Empty;
        OperationMessage = $"{template.CameraName} 正在读取图片";
        ClearAlarm();
        try
        {
            var image = await Task.Run(() => ImageSourceFileStorage.LoadImage(imagePaths[0]));
            await SaveReferenceImageAsync(template, image);
            PersistConfiguration();
            OperationMessage = $"{template.CameraName} 已读取图片";
            ClearAlarm();
        }
        catch (Exception ex)
        {
            template.LastError = ex.Message;
            OperationMessage = ex.Message;
            RaiseAlarm($"读图失败：{ex.Message}");
        }
        finally
        {
            template.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAllTemplatesAsync()
    {
        var count = 0;
        var skipped = 0;
        foreach (var template in CameraTemplates)
        {
            if (!CanAcquireTemplate(template))
            {
                skipped++;
                continue;
            }

            if (await CreateTemplateRecordAsync(template))
            {
                count++;
            }
        }

        OperationMessage = skipped > 0
            ? $"创建完成：{count} 个模板记录，跳过 {skipped}"
            : $"创建完成：{count} 个模板记录";
        if (count > 0)
        {
            PersistConfiguration();
        }
    }

    [RelayCommand]
    private async Task CreateSelectedTemplateAsync()
    {
        if (SelectedCameraTemplate == null)
        {
            return;
        }

        if (await CreateTemplateRecordAsync(SelectedCameraTemplate))
        {
            PersistConfiguration();
            OperationMessage = $"{SelectedCameraTemplate.CameraName} 模板记录已创建";
            ClearAlarm();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(SelectedCameraTemplate.LastError))
            {
                OperationMessage = "请先拍照或读取图片";
            }
        }
    }

    [RelayCommand]
    private void ClearAllTemplates()
    {
        foreach (var template in CameraTemplates)
        {
            ClearTemplate(template);
        }

        PersistConfiguration();
        OperationMessage = "当前型号模板已清除";
    }

    [RelayCommand]
    private void ClearSelectedTemplate()
    {
        if (SelectedCameraTemplate == null)
        {
            return;
        }

        ClearTemplate(SelectedCameraTemplate);
        PersistConfiguration();
        OperationMessage = $"{SelectedCameraTemplate.CameraName} 模板已清除";
        ClearAlarm();
    }

    private async Task<bool> CaptureTemplateAsync(CameraTemplateViewModel template)
    {
        if (!CanAcquireTemplate(template))
        {
            template.LastError = "相机未启用或未选择设备";
            return false;
        }

        template.IsBusy = true;
        template.LastError = string.Empty;
        OperationMessage = $"正在拍照：{template.CameraName}";

        try
        {
            using var result = await _cameraService.CaptureAsync(template.Camera);
            template.Camera.SetInspectionSourceFromCapture(result.Image, result.Frame, result.DisplayName, result.ReportedFps);
            template.Camera.IsConnected = true;
            SaveReferenceImage(template, result.Image);
            return true;
        }
        catch (Exception ex)
        {
            template.LastError = ex.Message;
            return false;
        }
        finally
        {
            template.IsBusy = false;
        }
    }

    private void SaveReferenceImage(CameraTemplateViewModel template, ImageSource image)
    {
        if (SelectedProductModel == null)
        {
            return;
        }

        var relativePath = _assetPathService.GetReferenceImageRelativePath(SelectedProductModel.Id, template.CameraId);
        var fullPath = _assetPathService.GetFullPath(relativePath);
        ImageSourceFileStorage.SavePng(image, fullPath);

        template.Definition.ProductModelId = SelectedProductModel.Id;
        template.Definition.CameraId = template.CameraId;
        template.Definition.ReferenceImageRelativePath = relativePath;
        template.Definition.ImageWidth = (int)Math.Round(image.Width);
        template.Definition.ImageHeight = (int)Math.Round(image.Height);
        template.Definition.TemplateRelativePath = string.Empty;
        template.Definition.KeyPointCount = 0;
        template.Definition.DescriptorRows = 0;
        template.Definition.DescriptorCols = 0;
        template.Definition.DescriptorMatType = 0;
        template.Definition.RegisteredAt = null;
        template.Definition.RegisteredFeatureMethod = string.Empty;
        template.Definition.RegisteredMaxLongSide = 0;
        template.Definition.RegisteredMaxFeatures = 0;
        template.Definition.EffectiveAlignmentRegion = null;
        template.Definition.RegisteredEffectiveAlignmentRegion = null;
        _isEffectiveAlignmentRegionSelected = false;
        _alignmentTemplateStore.Delete(SelectedProductModel.Id, template.CameraId);
        DeleteAssetFile(_assetPathService.GetTemplateRelativePath(SelectedProductModel.Id, template.CameraId));
        template.ReferenceImage = image;
        template.LastError = string.Empty;
        template.RequiresTemplateRebuild = false;
        template.RefreshMetadata();
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
    }

    private async Task SaveReferenceImageAsync(CameraTemplateViewModel template, ImageSource image)
    {
        if (SelectedProductModel == null)
        {
            return;
        }

        var productId = SelectedProductModel.Id;
        var relativePath = _assetPathService.GetReferenceImageRelativePath(productId, template.CameraId);
        var fullPath = _assetPathService.GetFullPath(relativePath);
        await Task.Run(() => ImageSourceFileStorage.SavePng(image, fullPath));

        template.Definition.ProductModelId = productId;
        template.Definition.CameraId = template.CameraId;
        template.Definition.ReferenceImageRelativePath = relativePath;
        template.Definition.ImageWidth = (int)Math.Round(image.Width);
        template.Definition.ImageHeight = (int)Math.Round(image.Height);
        template.Definition.TemplateRelativePath = string.Empty;
        template.Definition.KeyPointCount = 0;
        template.Definition.DescriptorRows = 0;
        template.Definition.DescriptorCols = 0;
        template.Definition.DescriptorMatType = 0;
        template.Definition.RegisteredAt = null;
        template.Definition.RegisteredFeatureMethod = string.Empty;
        template.Definition.RegisteredMaxLongSide = 0;
        template.Definition.RegisteredMaxFeatures = 0;
        template.Definition.EffectiveAlignmentRegion = null;
        template.Definition.RegisteredEffectiveAlignmentRegion = null;
        _isEffectiveAlignmentRegionSelected = false;
        _alignmentTemplateStore.Delete(productId, template.CameraId);
        DeleteAssetFile(_assetPathService.GetTemplateRelativePath(productId, template.CameraId));
        template.ReferenceImage = image;
        template.LastError = string.Empty;
        template.RequiresTemplateRebuild = false;
        template.RefreshMetadata();
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
    }

    private async Task<bool> CreateTemplateRecordAsync(CameraTemplateViewModel template)
    {
        if (SelectedProductModel == null || string.IsNullOrWhiteSpace(template.Definition.ReferenceImageRelativePath))
        {
            return false;
        }

        template.IsBusy = true;
        template.LastError = string.Empty;
        OperationMessage = $"{template.CameraName} 正在创建模板";
        ClearAlarm();
        try
        {
            var referencePath = _assetPathService.GetFullPath(template.Definition.ReferenceImageRelativePath);
            var result = await Task.Run(() => BuildTemplate(
                referencePath,
                $"{SelectedProductModel.Name}-{template.CameraName}",
                template.Definition));

            template.Definition.ProductModelId = SelectedProductModel.Id;
            template.Definition.CameraId = template.CameraId;
            template.Definition.TemplateRelativePath = string.Empty;
            // 保留用户选择的特征方法，不要用模板构建返回的值覆盖
            template.Definition.TransformModel = result.Template.TransformModel.ToString();
            template.Definition.ImageWidth = result.Template.ImageWidth;
            template.Definition.ImageHeight = result.Template.ImageHeight;
            template.Definition.KeyPointCount = result.Template.KeyPoints.Count;
            template.Definition.DescriptorRows = result.Template.Descriptors.Rows;
            template.Definition.DescriptorCols = result.Template.Descriptors.Cols;
            template.Definition.DescriptorMatType = result.Template.Descriptors.MatType;
            template.Definition.RegisteredAt = DateTimeOffset.Now;
            template.MarkTemplateCreated();
            _alignmentTemplateStore.Save(template.Definition, result.Template);
            template.LastError = string.Empty;
            template.RefreshMetadata();
            return true;
        }
        catch (Exception ex)
        {
            template.LastError = ex.Message;
            OperationMessage = ex.Message;
            RaiseAlarm($"创建模板失败：{ex.Message}");
            template.RefreshMetadata();
            return false;
        }
        finally
        {
            template.IsBusy = false;
        }
    }

    private static TemplateBuildResult BuildTemplate(
        string referencePath,
        string templateName,
        CameraAlignmentDefinition definition)
    {
        if (!File.Exists(referencePath))
        {
            throw new FileNotFoundException("参考图不存在。", referencePath);
        }

        using var referenceImage = Cv2.ImRead(referencePath, ImreadModes.Color);
        if (referenceImage.Empty())
        {
            throw new InvalidOperationException("参考图读取失败。");
        }

        var featureMethod = ParseFeatureMethod(definition.FeatureMethod);
        using var registrationMask = CreateRegistrationMask(referenceImage.Size(), definition.EffectiveAlignmentRegion);
        var alignmentTemplate = AlignmentTemplateBuilder
            .FromImage(referenceImage)
            .Named(templateName)
            .UseFeatureMethod(featureMethod)
            .WithMaxLongSide(definition.MaxLongSide)
            .WithMaxFeatures(definition.MaxFeatures)
            .UseAffinePartial()
            .WithRegistrationMask(registrationMask)
            .Build();

        if (alignmentTemplate.IsEmpty)
        {
            throw new InvalidOperationException("模板特征为空。");
        }

        return new TemplateBuildResult(alignmentTemplate);
    }

    private static FeatureMethod ParseFeatureMethod(string value)
    {
        return Enum.TryParse<FeatureMethod>(value, ignoreCase: true, out var method)
            ? method
            : FeatureMethod.Sift;
    }

    private static Mat? CreateRegistrationMask(Size imageSize, AlignmentEffectiveRegion? effectiveRegion)
    {
        if (effectiveRegion == null)
        {
            return null;
        }

        var region = AlignmentEffectiveRegion.NormalizeOrNull(
            effectiveRegion,
            imageSize.Width,
            imageSize.Height);
        if (region == null)
        {
            throw new InvalidOperationException("有效区域无效，请重新绘制。");
        }

        var left = (int)Math.Floor(region.Left);
        var top = (int)Math.Floor(region.Top);
        var right = (int)Math.Ceiling(region.Right);
        var bottom = (int)Math.Ceiling(region.Bottom);
        var rectangle = new Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
        var mask = new Mat(imageSize, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(mask, rectangle, Scalar.White, -1);
        return mask;
    }

    private sealed record TemplateBuildResult(AlignmentTemplate Template);

    private void ClearTemplate(CameraTemplateViewModel template)
    {
        var productModelId = template.Definition.ProductModelId;
        var cameraId = template.Definition.CameraId;
        DeleteAssetFile(template.Definition.ReferenceImageRelativePath);
        DeleteAssetFile(template.Definition.TemplateRelativePath);
        if (!string.IsNullOrWhiteSpace(productModelId) && !string.IsNullOrWhiteSpace(cameraId))
        {
            _alignmentTemplateStore.Delete(productModelId, cameraId);
        }

        template.Definition.Clear();
        _isEffectiveAlignmentRegionSelected = false;
        template.ReferenceImage = null;
        template.LastError = string.Empty;
        template.RequiresTemplateRebuild = false;
        template.RefreshMetadata();
        RefreshEffectiveAlignmentOverlays();
        RefreshEffectiveAlignmentRegionCommandStates();
    }

    private void DeleteAssetFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var fullPath = _assetPathService.GetFullPath(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private void RefreshCameraTemplates(bool reloadImages = false)
    {
        CameraTemplates.Clear();
        if (SelectedProductModel == null)
        {
            SelectedCameraTemplate = null;
            return;
        }

        foreach (var camera in _cameras)
        {
            var definition = GetOrCreateAlignment(SelectedProductModel.Id, camera.ConfigurationId);
            var item = new CameraTemplateViewModel(camera, definition);
            item.AlignmentParameterChanged += OnTemplateAlignmentParameterChanged;
            if (reloadImages || !string.IsNullOrWhiteSpace(definition.ReferenceImageRelativePath))
            {
                TryLoadReferenceImage(item);
            }

            CameraTemplates.Add(item);
        }

        var selected = CameraTemplates.FirstOrDefault(CanAcquireTemplate) ?? CameraTemplates.FirstOrDefault();
        if (selected != null)
        {
            SelectCameraTemplate(selected);
        }
        else
        {
            RefreshEffectiveAlignmentOverlays();
            RefreshEffectiveAlignmentRegionCommandStates();
        }
    }

    private static bool CanAcquireTemplate(CameraTemplateViewModel template)
    {
        return template.Camera.IsAcquisitionConfigured;
    }

    private CameraAlignmentDefinition GetOrCreateAlignment(string productModelId, string cameraId)
    {
        var definition = _configuration.Alignments.FirstOrDefault(alignment =>
            string.Equals(alignment.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alignment.CameraId, cameraId, StringComparison.OrdinalIgnoreCase));

        if (definition != null)
        {
            return definition;
        }

        definition = new CameraAlignmentDefinition
        {
            ProductModelId = productModelId,
            CameraId = cameraId
        };
        _configuration.Alignments.Add(definition);
        return definition;
    }

    private void TryLoadReferenceImage(CameraTemplateViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Definition.ReferenceImageRelativePath))
        {
            return;
        }

        var fullPath = _assetPathService.GetFullPath(item.Definition.ReferenceImageRelativePath);
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            item.ReferenceImage = ImageSourceFileStorage.LoadImage(fullPath);
        }
        catch (Exception ex)
        {
            item.LastError = ex.Message;
        }
    }

    private bool TryValidateNewProduct(string productCode, string productName, out string message)
    {
        var code = productCode.Trim();
        var name = productName.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            message = "成品号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            message = "名称不能为空";
            return false;
        }

        if (ProductModels.Any(product => string.Equals(product.Id.Trim(), code, StringComparison.OrdinalIgnoreCase)))
        {
            message = "成品号已存在";
            return false;
        }

        if (IsDuplicateProductName(name, null))
        {
            message = "名称已存在";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool IsDuplicateProductName(string productName, ProductModelDefinition? excludedProduct)
    {
        return ProductModels.Any(product =>
            !ReferenceEquals(product, excludedProduct) &&
            string.Equals(product.Name.Trim(), productName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void RaiseAlarm(string message)
    {
        AlarmRaised?.Invoke(this, message);
    }

    private void ClearAlarm()
    {
        AlarmCleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnTemplateAlignmentParameterChanged(object? sender, AlignmentParameterChangedEventArgs e)
    {
        PersistConfiguration();
        OperationMessage = e.RequiresTemplateRebuild
            ? "模板参数已保存，请重新创建模板"
            : "高级参数已保存，下次对齐生效";
    }

    private void RefreshEffectiveAlignmentOverlays()
    {
        EffectiveAlignmentOverlays.Clear();
        if (SelectedCameraTemplate?.Definition.EffectiveAlignmentRegion is not { } region)
        {
            return;
        }

        var stroke = Brushes.DeepSkyBlue;
        EffectiveAlignmentOverlays.Add(new ImageOverlayItem
        {
            Id = "effective-alignment-region",
            Kind = ImageOverlayKind.Rectangle,
            X = region.Left,
            Y = region.Top,
            Width = region.Width,
            Height = region.Height,
            Text = "有效区域",
            LabelBackground = stroke,
            LabelForeground = Brushes.White,
            Stroke = stroke,
            Fill = RoiOverlayVisualFactory.CreateFill(stroke, 0.16),
            StrokeThickness = 2,
            IsSelected = _isEffectiveAlignmentRegionSelected,
            IsEditable = CanEditEffectiveAlignmentRegion,
            CanRotate = false
        });
    }

    private void RefreshEffectiveAlignmentRegionCommandStates()
    {
        OnPropertyChanged(nameof(CanEditEffectiveAlignmentRegion));
        OnPropertyChanged(nameof(CanClearEffectiveAlignmentRegion));
        DrawEffectiveAlignmentRegionCommand.NotifyCanExecuteChanged();
        ClearEffectiveAlignmentRegionCommand.NotifyCanExecuteChanged();
    }

}
