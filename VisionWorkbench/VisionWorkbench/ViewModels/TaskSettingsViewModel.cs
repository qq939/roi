using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageBox;
using Microsoft.Win32;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;
using VisionWorkbench.Views;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;

namespace VisionWorkbench.ViewModels;

public sealed partial class TaskSettingsViewModel : ObservableObject
{
    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly ObservableCollection<CameraViewModel> _cameras;
    private readonly CameraAcquisitionService _cameraService;
    private readonly InspectionConfigurationStorage _storage;
    private readonly VisionAssetPathService _assetPathService;
    private readonly TaskImageAlignmentService _alignmentService;
    private readonly ClipTrainingLibraryService _clipTrainingLibraryService;
    private readonly InspectionImageArchiveService _imageArchiveService;
    private readonly RuntimeInspectionContext _inspectionContext;
    private readonly Measurement1DService _measurementService = new();
    private MeasurementProfileAnalysis? _currentMeasurementAnalysis;

    public TaskSettingsViewModel(
        InspectionWorkspaceConfiguration configuration,
        ObservableCollection<CameraViewModel> cameras,
        CameraAcquisitionService cameraService,
        InspectionConfigurationStorage storage,
        VisionAssetPathService assetPathService,
        TaskImageAlignmentService alignmentService,
        ClipTrainingLibraryService clipTrainingLibraryService,
        InspectionImageArchiveService imageArchiveService,
        RuntimeInspectionContext inspectionContext)
    {
        _configuration = configuration;
        _cameras = cameras;
        _cameraService = cameraService;
        _storage = storage;
        _assetPathService = assetPathService;
        _alignmentService = alignmentService;
        _clipTrainingLibraryService = clipTrainingLibraryService;
        _imageArchiveService = imageArchiveService;
        _inspectionContext = inspectionContext;

        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;

        // 初始化时同步当前值
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(SerialNumber));

        ProductModels = new ObservableCollection<ProductModelDefinition>(_configuration.ProductModels);
        Cameras = cameras;
        TaskItems = [];
        TaskOverlays = [];
        TaskItems.CollectionChanged += OnTaskItemsChanged;
        selectedProductModel = ProductModels.FirstOrDefault(product =>
            string.Equals(product.Id, _configuration.SelectedProductModelId, StringComparison.OrdinalIgnoreCase))
            ?? ProductModels.First();
        selectedCamera = _inspectionContext.SelectedCamera ?? Cameras.First();
        taskKindOptions =
        [
            InspectionTaskKind.Classification,
            InspectionTaskKind.Measurement
        ];
        currentInteractionMode = ImageBoxInteractionMode.Pan;

        RefreshTasks();
        TryLoadTemplateImage();
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

    private void OnInspectionContextSerialNumberChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SerialNumber));
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        var camera = _inspectionContext.SelectedCamera;
        AppDiagnostics.Debug("task-settings", $"OnInspectionContextSelectedCameraChanged: context={camera?.Name ?? "null"}, current={SelectedCamera?.Name ?? "null"}");
        if (camera != null && !ReferenceEquals(SelectedCamera, camera))
        {
            SelectedCamera = camera;
        }
    }

    public void RefreshPublicParams()
    {
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(SerialNumber));

        // 同步成品号下拉框
        var productCode = _inspectionContext.ProductCode;
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var match = ProductModels.FirstOrDefault(p => string.Equals(p.Id, productCode, StringComparison.OrdinalIgnoreCase));
            if (match != null && !ReferenceEquals(SelectedProductModel, match))
            {
                SelectedProductModel = match;
            }
        }

        // 同步相机下拉框
        var ctxCamera = _inspectionContext.SelectedCamera;
        AppDiagnostics.Debug("task-settings", $"RefreshPublicParams: ProductCode={productCode}, SerialNumber={_inspectionContext.SerialNumber}, CtxCamera={ctxCamera?.Name ?? "null"}, CurrentCamera={SelectedCamera?.Name ?? "null"}");
        if (ctxCamera != null && !ReferenceEquals(SelectedCamera, ctxCamera))
        {
            SelectedCamera = ctxCamera;
        }
        else
        {
            OnPropertyChanged(nameof(SelectedCamera));
        }
    }

    public ObservableCollection<ProductModelDefinition> ProductModels { get; }

    public ObservableCollection<CameraViewModel> Cameras { get; }

    public ObservableCollection<InspectionTaskEditorViewModel> TaskItems { get; }

    public ObservableCollection<ImageOverlayItem> TaskOverlays { get; }

    public event EventHandler<string>? AlarmRaised;

    public event EventHandler? AlarmCleared;

    public bool IsTemplateEditMode
    {
        get => ImageMode == TaskImageMode.TemplateEdit;
        set
        {
            if (value)
            {
                ImageMode = TaskImageMode.TemplateEdit;
            }
        }
    }

    public bool IsTestPreviewMode
    {
        get => ImageMode == TaskImageMode.TestPreview;
        set
        {
            if (value)
            {
                ImageMode = TaskImageMode.TestPreview;
            }
        }
    }

    public bool CanEditTaskParameters => IsTemplateEditMode && SelectedTask != null && CurrentImage != null && HasRegisteredTemplateImage();

    public bool CanEditRoi => CanEditTaskParameters;

    public bool CanRedrawRoi => CanEditRoi;

    public bool CanShowMeasurementProfile =>
        IsTemplateEditMode &&
        SelectedTask?.Kind == InspectionTaskKind.Measurement &&
        HasRegisteredTemplateImage();

    public bool CanAddTask => IsTemplateEditMode && SelectedProductModel is not null && SelectedCamera is not null;

    public bool CanDeleteTask => IsTemplateEditMode && SelectedTask != null;

    public void RefreshProductModels()
    {
        ProductModels.Clear();
        foreach (var product in _configuration.ProductModels)
        {
            ProductModels.Add(product);
        }

        var selected = ProductModels.FirstOrDefault(product =>
            string.Equals(product.Id, _configuration.SelectedProductModelId, StringComparison.OrdinalIgnoreCase))
            ?? ProductModels.FirstOrDefault();
        if (selected == null)
        {
            return;
        }

        if (ReferenceEquals(SelectedProductModel, selected))
        {
            RefreshTasks();
            TryLoadTemplateImage();
            return;
        }

        SelectedProductModel = selected;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTask))]
    [NotifyPropertyChangedFor(nameof(CanShowMeasurementProfile))]
    private ProductModelDefinition selectedProductModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTask))]
    [NotifyPropertyChangedFor(nameof(CanShowMeasurementProfile))]
    private CameraViewModel selectedCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditTaskParameters))]
    [NotifyPropertyChangedFor(nameof(CanEditRoi))]
    [NotifyPropertyChangedFor(nameof(CanRedrawRoi))]
    [NotifyPropertyChangedFor(nameof(CanAddTask))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTask))]
    [NotifyPropertyChangedFor(nameof(CanShowMeasurementProfile))]
    private InspectionTaskEditorViewModel? selectedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditTaskParameters))]
    [NotifyPropertyChangedFor(nameof(CanEditRoi))]
    [NotifyPropertyChangedFor(nameof(CanRedrawRoi))]
    [NotifyPropertyChangedFor(nameof(CanAddTask))]
    [NotifyPropertyChangedFor(nameof(CanShowMeasurementProfile))]
    private ImageSource? currentImage;

    [ObservableProperty]
    private ImageBoxInteractionMode currentInteractionMode;

    [ObservableProperty]
    private IReadOnlyList<InspectionTaskKind> taskKindOptions;

    public IReadOnlyList<MeasurementEdgePolarity> MeasurementEdgePolarityOptions { get; } =
        Enum.GetValues<MeasurementEdgePolarity>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTemplateEditMode))]
    [NotifyPropertyChangedFor(nameof(IsTestPreviewMode))]
    [NotifyPropertyChangedFor(nameof(CanEditTaskParameters))]
    [NotifyPropertyChangedFor(nameof(CanEditRoi))]
    [NotifyPropertyChangedFor(nameof(CanRedrawRoi))]
    [NotifyPropertyChangedFor(nameof(CanAddTask))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTask))]
    [NotifyPropertyChangedFor(nameof(CanShowMeasurementProfile))]
    private TaskImageMode imageMode = TaskImageMode.TemplateEdit;

    [ObservableProperty]
    private string operationMessage = "选择型号和相机";

    [ObservableProperty]
    private string testPreviewResultText = "切换到测试预览后，执行测试拍照或测试读图。";

    [ObservableProperty]
    private string currentMeasurementValueText = "--";

    [ObservableProperty]
    private Brush currentMeasurementStatusBrush = UiBrushes.TextMuted;

    [ObservableProperty]
    private string currentMeasurementToolTip = "当前显示图暂无测量结果";

    partial void OnSelectedProductModelChanged(ProductModelDefinition value)
    {
        _configuration.SelectedProductModelId = value.Id;
        ImageMode = TaskImageMode.TemplateEdit;
        RefreshTasks();
        TryLoadTemplateImage();
        RefreshCommandStates();
    }

    partial void OnSelectedCameraChanged(CameraViewModel value)
    {
        AppDiagnostics.Debug("task-settings", $"OnSelectedCameraChanged: {value?.Name ?? "null"}");
        _inspectionContext.SelectedCamera = value;
        ImageMode = TaskImageMode.TemplateEdit;
        RefreshTasks();
        TryLoadTemplateImage();
        RefreshCommandStates();
    }

    partial void OnSelectedTaskChanged(InspectionTaskEditorViewModel? value)
    {
        RefreshOverlays();
        RefreshCommandStates();
    }

    partial void OnCurrentImageChanged(ImageSource? value)
    {
        RefreshOverlays();
        RefreshCommandStates();
    }

    partial void OnImageModeChanged(TaskImageMode value)
    {
        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        OnPropertyChanged(nameof(IsTemplateEditMode));
        OnPropertyChanged(nameof(IsTestPreviewMode));
        RefreshOverlays();
        RefreshCommandStates();

        if (value == TaskImageMode.TemplateEdit)
        {
            TryLoadTemplateImage();
        }
        else
        {
            OperationMessage = "测试预览模式";
            TestPreviewResultText = "等待测试拍照或测试读图。";
        }
    }

    [RelayCommand]
    private async Task CaptureImageAsync()
    {
        ImageMode = TaskImageMode.TestPreview;
        CurrentImage = null;

        if (!SelectedCamera.IsAcquisitionConfigured)
        {
            var message = $"{SelectedCamera.Name} 未启用或未选择设备";
            OperationMessage = message;
            TestPreviewResultText = message;
            RaiseAlarm(message);
            return;
        }

        SelectedCamera.IsBusy = true;
        TestPreviewResultText = $"{SelectedCamera.Name} 正在拍照并对齐...";
        try
        {
            using var result = await _cameraService.CaptureAsync(SelectedCamera);
            // Test preview only updates the display image; it must not replace the production inspection source.
            SelectedCamera.Frame = result.Image;
            SelectedCamera.IsConnected = true;
            SelectedCamera.DeviceDisplayName = result.DisplayName;
            SelectedCamera.LastFrameInfo = $"{DateTime.Now:HH:mm:ss}  {result.Image.Width:0}x{result.Image.Height:0}";
            await ApplyRuntimeImageAsync(result.Image);
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
            TestPreviewResultText = $"测试拍照失败：{ex.Message}";
            RaiseAlarm($"测试拍照失败：{ex.Message}");
        }
        finally
        {
            SelectedCamera.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All Files|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ImageMode = TaskImageMode.TestPreview;
        CurrentImage = null;
        TestPreviewResultText = "正在读取图片并对齐...";
        try
        {
            await ApplyRuntimeImageFromFileAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
            TestPreviewResultText = $"测试读图失败：{ex.Message}";
            RaiseAlarm($"测试读图失败：{ex.Message}");
        }
    }

    private async Task ApplyRuntimeImageFromFileAsync(string filePath)
    {
        var definition = ResolveCurrentAlignment();
        if (definition == null || string.IsNullOrWhiteSpace(definition.ReferenceImageRelativePath))
        {
            var message = "未注册模板图像";
            OperationMessage = message;
            TestPreviewResultText = message;
            RaiseAlarm(message);
            return;
        }

        TestPreviewResultText = "正在执行模板对齐...";
        var result = await Task.Run(() =>
        {
            using var runtimeMat = OpenCvSharp.Cv2.ImRead(System.IO.Path.GetFullPath(filePath), OpenCvSharp.ImreadModes.Color);
            return _alignmentService.AlignMatToTemplate(definition, runtimeMat);
        });

        if (!result.Success || result.Image == null)
        {
            CurrentImage = null;
            OperationMessage = result.Message;
            TestPreviewResultText = result.Message;
            RaiseAlarm($"测试图像对齐失败：{result.Message}");
            return;
        }

        using var alignedMat = result.Image;
        CurrentImage = MatImageSourceConverter.CreateImageSource(alignedMat);
        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        OperationMessage = result.Message;
        ClearAlarm();

        // 保存对齐后的图像到临时文件，避免分类服务再次对齐
        var alignedPath = Path.Combine(Path.GetTempPath(), $"vision_aligned_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        Cv2.ImWrite(alignedPath, alignedMat);

        // 对所有分类任务执行检测 - 使用对齐后的图像路径
        await ExecuteClassificationPreviewAsync(alignedPath);
    }

    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private void AddTask()
    {
        if (CurrentImage == null || !HasRegisteredTemplateImage())
        {
            OperationMessage = "请先到型号管理注册模板图像";
            RaiseAlarm(OperationMessage);
            return;
        }

        var index = TaskItems.Count + 1;
        var definition = new InspectionTaskDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"任务 {index:00}",
            ProductModelId = SelectedProductModel.Id,
            CameraId = SelectedCamera.ConfigurationId,
            Kind = InspectionTaskKind.Classification,
            Roi = new RoiRegion
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"ROI {index:00}",
                X = CurrentImage.Width / 2.0,
                Y = CurrentImage.Height / 2.0,
                Width = Math.Min(180, Math.Max(20, CurrentImage.Width * 0.35)),
                Height = Math.Min(120, Math.Max(20, CurrentImage.Height * 0.25))
            }
        };
        definition.EnsureClipVectorSet().Threshold = GetCurrentProductOkThreshold();
        _configuration.Tasks.Add(definition);

        var item = CreateEditor(definition);
        TaskItems.Add(item);
        SelectedTask = item;
        StartRoiRedraw();
        RefreshOverlays();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTask))]
    private void DeleteTask()
    {
        if (SelectedTask == null)
        {
            return;
        }

        SelectedTask.PropertyChanged -= OnTaskPropertyChanged;
        _configuration.Tasks.Remove(SelectedTask.Definition);
        TaskItems.Remove(SelectedTask);
        SelectedTask = TaskItems.FirstOrDefault();
        RefreshOverlays();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _storage.Save(_configuration);

        // 获取当前成品号下所有相机的分类任务
        var allCameraTasks = ResolveAllCameraTasks()
            .Where(task => task.Kind == InspectionTaskKind.Classification)
            .ToList();

        var updatedVectorSets = 0;
        try
        {
            foreach (var task in allCameraTasks)
            {
                if (await _clipTrainingLibraryService.UpdateVectorSetConfigAsync(task.EnsureClipVectorSet()))
                {
                    updatedVectorSets++;
                }
            }

            var cameraCount = allCameraTasks.Select(t => t.CameraId).Distinct().Count();
            OperationMessage = updatedVectorSets > 0
                ? $"任务配置已保存，已同步 {updatedVectorSets} 个训练库（{cameraCount} 个相机）"
                : "任务配置已保存";
            ClearAlarm();
            AppDiagnostics.Info("task-settings", $"Task configuration saved. Cameras={cameraCount}, VectorSets={updatedVectorSets}");
        }
        catch (Exception ex)
        {
            OperationMessage = $"任务配置已保存，但训练库同步失败：{ex.Message}";
            AppDiagnostics.Error("task-settings", "Task configuration saved, but vector-set metadata synchronization failed.", ex);
            RaiseAlarm(OperationMessage);
        }
    }

    private IEnumerable<InspectionTaskDefinition> ResolveAllCameraTasks()
    {
        if (SelectedProductModel == null)
        {
            return Enumerable.Empty<InspectionTaskDefinition>();
        }

        return _configuration.Tasks
            .Where(task =>
                string.Equals(task.ProductModelId, SelectedProductModel.Id, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand(CanExecute = nameof(CanRedrawRoi))]
    private void RedrawRoi()
    {
        StartRoiRedraw();
    }

    [RelayCommand(CanExecute = nameof(CanShowMeasurementProfile))]
    private void ShowMeasurementProfile()
    {
        if (SelectedTask == null || SelectedTask.Kind != InspectionTaskKind.Measurement)
        {
            OperationMessage = "请先选择测量任务";
            RaiseAlarm(OperationMessage);
            return;
        }

        var alignment = ResolveCurrentAlignment();
        if (alignment == null || string.IsNullOrWhiteSpace(alignment.ReferenceImageRelativePath))
        {
            OperationMessage = "请先到型号管理创建模板参考图";
            RaiseAlarm(OperationMessage);
            return;
        }

        var fullPath = _assetPathService.GetFullPath(alignment.ReferenceImageRelativePath);
        if (!File.Exists(fullPath))
        {
            OperationMessage = "模板参考图文件不存在";
            RaiseAlarm(OperationMessage);
            return;
        }

        try
        {
            using var template = Cv2.ImRead(fullPath, ImreadModes.Color);
            if (template.Empty())
            {
                OperationMessage = "模板参考图读取失败";
                RaiseAlarm(OperationMessage);
                return;
            }

            using var crop = ClipFrameImageMaterializer.CropFrame(template, SelectedTask.Definition.Roi);
            var dialog = new MeasurementProfileDialog(crop, SelectedTask.Definition.EnsureMeasurementOptions())
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.AppliedOptions != null)
            {
                SelectedTask.ApplyMeasurementOptions(dialog.AppliedOptions);
                _storage.Save(_configuration);
                RefreshOverlays();
                OperationMessage = "测量 Profile 参数已应用并保存";
                ClearAlarm();
            }
        }
        catch (Exception ex)
        {
            OperationMessage = $"打开 Profile 失败：{ex.Message}";
            RaiseAlarm(OperationMessage);
        }
    }

    public void CompleteRoiDraw(ImageBoxInteractionMode mode, IReadOnlyList<Point> points)
    {
        if (!CanEditRoi || SelectedTask == null)
        {
            CurrentInteractionMode = ImageBoxInteractionMode.Pan;
            return;
        }

        if (mode == ImageBoxInteractionMode.DrawRotatedRectangle && points.Count >= 4)
        {
            var roi = InspectionRoiGeometry.FromCornerPoints(points, SelectedTask.Definition.Roi);
            SelectedTask.SetRoi(roi.X, roi.Y, roi.Width, roi.Height, roi.AngleDegrees);
        }
        else if (mode == ImageBoxInteractionMode.DrawRectangle && points.Count >= 2)
        {
            var first = points[0];
            var second = points[1];
            var x = Math.Min(first.X, second.X);
            var y = Math.Min(first.Y, second.Y);
            var width = Math.Abs(second.X - first.X);
            var height = Math.Abs(second.Y - first.Y);
            SelectedTask.SetRoi(
                x + width / 2.0,
                y + height / 2.0,
                width,
                height);
        }

        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        OperationMessage = "ROI 已更新";
        RefreshOverlays();
    }

    public void SelectTaskById(string id)
    {
        var task = TaskItems.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return;
        }

        SelectedTask = task;
        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        RefreshOverlays();
    }

    public void CompleteRoiEdit(
        string id,
        double x,
        double y,
        double width,
        double height,
        double angleDegrees)
    {
        if (!CanEditRoi)
        {
            CurrentInteractionMode = ImageBoxInteractionMode.Pan;
            return;
        }

        var task = TaskItems.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return;
        }

        SelectedTask = task;
        task.SetRoi(x + width / 2.0, y + height / 2.0, width, height, angleDegrees);
        OperationMessage = "ROI 已更新";
        RefreshOverlays();
    }

    public void RejectRoiDraw(string message)
    {
        OperationMessage = message;
        RaiseAlarm(message);
    }

    private void RefreshTasks()
    {
        foreach (var item in TaskItems)
        {
            item.PropertyChanged -= OnTaskPropertyChanged;
        }

        TaskItems.Clear();
        var tasks = _configuration.Tasks
            .Where(task =>
                string.Equals(task.ProductModelId, SelectedProductModel.Id, StringComparison.OrdinalIgnoreCase) &&
                IsCameraTask(task, SelectedCamera))
            .ToArray();

        foreach (var task in tasks)
        {
            TaskItems.Add(CreateEditor(task));
        }

        SelectedTask = TaskItems.FirstOrDefault();
        RefreshOverlays();
        RefreshCommandStates();
    }

    /// <summary>
    /// 刷新所有任务的阈值显示（从配置中重新读取）
    /// </summary>
    public void RefreshTaskThresholds()
    {
        foreach (var item in TaskItems)
        {
            item.RefreshThreshold();
        }
    }

    private InspectionTaskEditorViewModel CreateEditor(InspectionTaskDefinition definition)
    {
        var editor = new InspectionTaskEditorViewModel(definition);
        editor.PropertyChanged += OnTaskPropertyChanged;
        return editor;
    }

    private void OnTaskItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshOverlays();
        RefreshCommandStates();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshOverlays();
        RefreshCommandStates();
    }

    private void RefreshOverlays()
    {
        RefreshMeasurementPreview();
        TaskOverlays.Clear();
        foreach (var task in TaskItems)
        {
            var roi = task.Definition.Roi;
            if (roi.IsFullImage)
            {
                continue;
            }

            var selected = ReferenceEquals(task, SelectedTask);
            if (selected && CurrentInteractionMode == ImageBoxInteractionMode.DrawRotatedRectangle)
            {
                continue;
            }

            var overlay = RoiOverlayVisualFactory.CreateRoiOverlay(
                roi,
                _configuration,
                task.Name,
                RoiOverlayJudgment.Default,
                selected ? 1.6 : 1.2);
            overlay.Id = task.Id;
            overlay.IsEditable = CanEditRoi;
            overlay.IsSelected = selected;
            TaskOverlays.Add(overlay);

            if (selected && task.Kind == InspectionTaskKind.Measurement)
            {
                var analysis = _currentMeasurementAnalysis;
                var stroke = analysis?.Judgment == InspectionJudgment.OK
                    ? UiBrushes.Success
                    : analysis == null
                        ? UiBrushes.TextMuted
                        : UiBrushes.Danger;
                foreach (var measurementOverlay in RoiOverlayVisualFactory.CreateMeasurementOverlays(
                             roi,
                             analysis?.FirstEdgeIndex,
                             analysis?.SecondEdgeIndex,
                             stroke,
                             $"task-{task.Id}",
                             includeDirectionArrow: true))
                {
                    TaskOverlays.Add(measurementOverlay);
                }
            }
        }
    }

    private void RefreshMeasurementPreview()
    {
        _currentMeasurementAnalysis = null;
        CurrentMeasurementValueText = "--";
        CurrentMeasurementStatusBrush = UiBrushes.TextMuted;
        CurrentMeasurementToolTip = "当前显示图暂无测量结果";

        if (SelectedTask == null || SelectedTask.Kind != InspectionTaskKind.Measurement)
        {
            CurrentMeasurementToolTip = "请选择测量任务";
            return;
        }

        if (CurrentImage == null)
        {
            CurrentMeasurementToolTip = "当前没有可测量图像";
            return;
        }

        try
        {
            using var frame = MatImageSourceConverter.CreateMat(CurrentImage);
            using var crop = ClipFrameImageMaterializer.CropFrame(frame, SelectedTask.Definition.Roi);
            var analysis = _measurementService.Analyze(crop, SelectedTask.Definition.EnsureMeasurementOptions());
            _currentMeasurementAnalysis = analysis;
            CurrentMeasurementStatusBrush = analysis.Judgment == InspectionJudgment.OK
                ? UiBrushes.Success
                : UiBrushes.Danger;

            if (analysis.DistanceMm.HasValue)
            {
                CurrentMeasurementValueText = $"{analysis.DistanceMm.Value:0.00}";
                CurrentMeasurementToolTip =
                    $"距离 {analysis.DistanceMm.Value:0.00} mm / {analysis.DistancePx:0.00} px，" +
                    $"E1 {FormatNullable(analysis.FirstEdgeIndex, "0.00")}，" +
                    $"E2 {FormatNullable(analysis.SecondEdgeIndex, "0.00")}" +
                    (string.IsNullOrWhiteSpace(analysis.FailureReason) ? string.Empty : $"，{analysis.FailureReason}");
                return;
            }

            CurrentMeasurementToolTip = string.IsNullOrWhiteSpace(analysis.FailureReason)
                ? "未找到完整测量边缘"
                : analysis.FailureReason;
        }
        catch (Exception ex)
        {
            CurrentMeasurementStatusBrush = UiBrushes.Danger;
            CurrentMeasurementToolTip = $"当前值计算失败：{ex.Message}";
        }
    }

    private void TryLoadTemplateImage()
    {
        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        var definition = ResolveCurrentAlignment();

        if (definition == null || string.IsNullOrWhiteSpace(definition.ReferenceImageRelativePath))
        {
            CurrentImage = null;
            OperationMessage = "未注册模板图像";
            return;
        }

        var fullPath = _assetPathService.GetFullPath(definition.ReferenceImageRelativePath);
        if (!File.Exists(fullPath))
        {
            CurrentImage = null;
            OperationMessage = "模板图像不存在";
            return;
        }

        try
        {
            CurrentImage = ImageSourceFileStorage.LoadImage(fullPath);
            OperationMessage = "已加载模板图像";
            ClearAlarm();
        }
        catch (Exception ex)
        {
            CurrentImage = null;
            OperationMessage = ex.Message;
            RaiseAlarm($"加载模板图像失败：{ex.Message}");
        }
    }

    private async Task ApplyRuntimeImageAsync(ImageSource runtimeImage)
    {
        var definition = ResolveCurrentAlignment();
        if (definition == null || string.IsNullOrWhiteSpace(definition.ReferenceImageRelativePath))
        {
            var message = "未注册模板图像";
            OperationMessage = message;
            TestPreviewResultText = message;
            RaiseAlarm(message);
            return;
        }

        TestPreviewResultText = "正在执行模板对齐...";
        var result = await Task.Run(() => _alignmentService.AlignToTemplate(definition, runtimeImage));
        if (!result.Success || result.Image == null)
        {
            CurrentImage = null;
            OperationMessage = result.Message;
            TestPreviewResultText = result.Message;
            RaiseAlarm($"测试图像对齐失败：{result.Message}");
            return;
        }

        CurrentImage = result.Image;
        CurrentInteractionMode = ImageBoxInteractionMode.Pan;
        OperationMessage = result.Message;
        ClearAlarm();

        // 对所有分类任务执行检测 - 保存到临时文件
        var tempPath = Path.Combine(Path.GetTempPath(), $"vision_preview_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
        ImageSourceFileStorage.SavePng(runtimeImage, tempPath);
        await ExecuteClassificationPreviewAsync(tempPath);
    }

    private async Task ExecuteClassificationPreviewAsync(string alignedImagePath)
    {
        var classificationTasks = TaskItems.Where(t => t.Kind == InspectionTaskKind.Classification && t.Enabled).ToList();
        if (classificationTasks.Count == 0)
        {
            TestPreviewResultText = "无分类任务可检测";
            return;
        }

        var results = new List<string>();
        var okCount = 0;
        var ngCount = 0;
        var renderAnnotations = new List<InspectionRenderAnnotation>();

        foreach (var task in TaskItems)
        {
            task.ClearPreviewResult();
        }

        foreach (var task in classificationTasks)
        {
            try
            {
                // 对每个任务进行ROI裁剪，与正式检测流程保持一致
                var roiPath = await Task.Run(() =>
                {
                    using var aligned = Cv2.ImRead(alignedImagePath, ImreadModes.Color);
                    using var roiMat = ClipFrameImageMaterializer.CropFrame(aligned, task.Definition.Roi);
                    var path = Path.Combine(Path.GetTempPath(), $"vision_roi_{task.Definition.Id}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                    Cv2.ImWrite(path, roiMat);
                    return path;
                });

                var classifyResult = await _clipTrainingLibraryService.ClassifyAsync(new ClipTrainingClassifyRequest
                {
                    ProductModel = SelectedProductModel,
                    Task = task.Definition,
                    SourceImagePath = roiPath,
                    InputMode = ClipSampleInputMode.RoiImage,
                    CameraIdCandidates = new[] { SelectedCamera.ConfigurationId },
                    Alignments = _configuration.Alignments
                });

                task.PreviewResult = classifyResult.Classification.Judgment;
                task.PreviewOkScore = classifyResult.Classification.OkScore;
                task.PreviewNgScore = classifyResult.Classification.NgScore;

                var status = classifyResult.Classification.Judgment == InspectionJudgment.OK ? "OK" : "NG";
                var okScore = classifyResult.Classification.OkScore.ToString("0.0000");
                var ngScore = classifyResult.Classification.NgScore.HasValue ? classifyResult.Classification.NgScore.Value.ToString("0.0000") : "--";
                
                results.Add($"{task.Name}: {status} (OK={okScore}, NG={ngScore})");
                
                if (classifyResult.Classification.Judgment == InspectionJudgment.OK)
                    okCount++;
                else
                    ngCount++;

                // 创建渲染标注
                var judgment = classifyResult.Classification.Judgment;
                var taskName = string.IsNullOrWhiteSpace(task.Name) ? task.Definition.Id : task.Name;
                var resultText = $"{judgment} {classifyResult.Classification.OkScore:0.000}";
                renderAnnotations.Add(new InspectionRenderAnnotation(
                    task.Definition.Roi,
                    judgment,
                    resultText,
                    taskName));
            }
            catch (Exception ex)
            {
                results.Add($"{task.Name}: 检测失败 - {ex.Message}");
                
                // 失败的任务也创建标注
                var taskName = string.IsNullOrWhiteSpace(task.Name) ? task.Definition.Id : task.Name;
                renderAnnotations.Add(new InspectionRenderAnnotation(
                    task.Definition.Roi,
                    InspectionJudgment.NG,
                    "NG",
                    taskName));
                ngCount++;
            }
        }

        var totalStatus = ngCount > 0 ? "NG" : "OK";
        TestPreviewResultText = $"检测完成：{totalStatus} (OK={okCount}, NG={ngCount})\n{string.Join("\n", results)}";

        // 更新界面上的 Overlay 显示检测结果
        UpdatePreviewOverlays();

        // 保存测试预览渲染图
        await SaveTestPreviewRenderedResultAsync(alignedImagePath, renderAnnotations, totalStatus == "OK" ? InspectionJudgment.OK : InspectionJudgment.NG);
    }

    private void UpdatePreviewOverlays()
    {
        if (!IsTestPreviewMode)
            return;

        TaskOverlays.Clear();
        foreach (var task in TaskItems)
        {
            var roi = task.Definition.Roi;
            if (roi.IsFullImage)
                continue;

            var selected = ReferenceEquals(task, SelectedTask);
            
            var judgment = task.PreviewResult switch
            {
                InspectionJudgment.OK => RoiOverlayJudgment.OK,
                InspectionJudgment.NG => RoiOverlayJudgment.NG,
                _ => RoiOverlayJudgment.Default
            };

            var overlay = RoiOverlayVisualFactory.CreateRoiOverlay(
                roi,
                _configuration,
                task.Name,
                judgment,
                selected ? 1.6 : 1.2);
            overlay.Id = task.Id;
            overlay.IsEditable = false;
            overlay.IsSelected = selected;
            
            // 添加检测结果到标签
            if (task.PreviewResult != InspectionJudgment.Unknown)
            {
                var okScore = task.PreviewOkScore?.ToString("0.000") ?? "--";
                overlay.Text = $"{task.Name} {task.PreviewResult} {okScore}";
            }
            
            TaskOverlays.Add(overlay);
        }
    }

    private async Task SaveTestPreviewRenderedResultAsync(
        string alignedImagePath,
        List<InspectionRenderAnnotation> annotations,
        InspectionJudgment judgment)
    {
        try
        {
            var timestamp = DateTime.Now;
            var productCode = SanitizeFileName(SelectedProductModel.Name);
            var cameraName = SanitizeFileName(SelectedCamera.Name);
            var judgmentText = judgment == InspectionJudgment.OK ? "OK" : "NG";
            
            var previewDirectory = Path.Combine(
                _imageArchiveService.RootDirectory,
                timestamp.ToString("yyyyMMdd"),
                productCode,
                "测试预览");
            Directory.CreateDirectory(previewDirectory);

            var fileName = $"{productCode}--{timestamp:yyyyMMdd_HHmmss_fff}-{cameraName}-{judgmentText}.jpg";
            var filePath = Path.Combine(previewDirectory, fileName);

            // 添加序号避免重复
            var finalPath = filePath;
            if (File.Exists(finalPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(filePath);
                var extension = Path.GetExtension(filePath);
                for (var i = 1; i < 1000; i++)
                {
                    finalPath = Path.Combine(previewDirectory, $"{baseName}_{i:000}{extension}");
                    if (!File.Exists(finalPath))
                        break;
                }
            }

            await Task.Run(() =>
            {
                using var aligned = Cv2.ImRead(alignedImagePath, ImreadModes.Color);
                if (aligned.Empty())
                    return;

                using var rendered = aligned.Clone();
                InspectionImageArchiveService.DrawResultAnnotations(rendered, annotations, null);
                Cv2.ImWrite(finalPath, rendered);
            });

            OperationMessage = $"测试预览渲染图已保存：{Path.GetFileName(finalPath)}";
            AppDiagnostics.Info("test-preview", $"Test preview rendered result saved. Path={finalPath}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn("test-preview", $"Test preview rendered result save failed. Error={ex.Message}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Replace(' ', '_');
    }

    private bool HasRegisteredTemplateImage()
    {
        var definition = ResolveCurrentAlignment();
        return definition != null &&
               !string.IsNullOrWhiteSpace(definition.ReferenceImageRelativePath);
    }

    private CameraAlignmentDefinition? ResolveCurrentAlignment()
    {
        return _configuration.Alignments.FirstOrDefault(alignment =>
            string.Equals(alignment.ProductModelId, SelectedProductModel.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alignment.CameraId, SelectedCamera.ConfigurationId, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshCommandStates()
    {
        AddTaskCommand.NotifyCanExecuteChanged();
        DeleteTaskCommand.NotifyCanExecuteChanged();
        RedrawRoiCommand.NotifyCanExecuteChanged();
        ShowMeasurementProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditTaskParameters));
        OnPropertyChanged(nameof(CanEditRoi));
        OnPropertyChanged(nameof(CanRedrawRoi));
        OnPropertyChanged(nameof(CanShowMeasurementProfile));
        OnPropertyChanged(nameof(CanAddTask));
        OnPropertyChanged(nameof(CanDeleteTask));
    }

    private void StartRoiRedraw()
    {
        if (!CanEditRoi || SelectedTask == null)
        {
            OperationMessage = "请先选择可编辑的模板 ROI";
            RaiseAlarm(OperationMessage);
            return;
        }

        CurrentInteractionMode = ImageBoxInteractionMode.DrawRotatedRectangle;
        OperationMessage = "依次点击 3 个点绘制 ROI";
        ClearAlarm();
        RefreshOverlays();
    }

    private float GetCurrentProductOkThreshold()
    {
        var productId = SelectedProductModel?.Id;
        if (!string.IsNullOrWhiteSpace(productId)
            && _configuration.ProductModelOkThresholds.TryGetValue(productId, out var saved))
        {
            return ClipVectorSetDefinition.NormalizeThreshold((float)saved);
        }
        return ClipVectorSetDefinition.DefaultThreshold;
    }

    private void RaiseAlarm(string message)
    {
        AlarmRaised?.Invoke(this, message);
    }

    private void ClearAlarm()
    {
        AlarmCleared?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCameraTask(InspectionTaskDefinition task, CameraViewModel camera)
    {
        return string.Equals(task.CameraId, camera.ConfigurationId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(task.CameraId, camera.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNullable(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format) : "--";
    }

}
