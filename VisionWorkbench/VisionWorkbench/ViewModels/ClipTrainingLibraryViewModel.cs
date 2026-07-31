using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageBox;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;
using VisionWorkbench.Views;

namespace VisionWorkbench.ViewModels;

public sealed partial class ClipTrainingLibraryViewModel : ObservableObject
{
    private static readonly Brush DefaultStroke = Frozen("#FACC15");
    internal static readonly Brush UnlabeledBrush = Frozen("#DB2777");
    private static readonly Brush IgnoredBrush = Frozen("#94A3B8");
    internal static readonly Brush OkSoftBrush = Frozen("#DCFCE7");
    internal static readonly Brush NgSoftBrush = Frozen("#FEE2E2");
    internal static readonly Brush UnlabeledSoftBrush = Frozen("#FCE7F3");
    internal static readonly Brush IgnoredSoftBrush = Frozen("#E2E8F0");

    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly InspectionConfigurationStorage _storage;
    private readonly ClipTrainingLibraryService _libraryService;
    private readonly CameraAcquisitionService _cameraService;
    private readonly IUserDialogService _dialogService;
    private readonly RuntimeInspectionContext _inspectionContext;
    private CameraTrainingLibraryDocument? _document;
    private IReadOnlyList<InspectionTaskDefinition> _currentTasks = Array.Empty<InspectionTaskDefinition>();

    public ClipTrainingLibraryViewModel(
        InspectionWorkspaceConfiguration configuration,
        ObservableCollection<CameraViewModel> cameras,
        CameraAcquisitionService cameraService,
        InspectionConfigurationStorage storage,
        ClipTrainingLibraryService libraryService,
        InspectionResultStore resultStore,
        IUserDialogService dialogService,
        RuntimeInspectionContext inspectionContext)
    {
        _configuration = configuration;
        _cameraService = cameraService;
        _storage = storage;
        _libraryService = libraryService;
        _dialogService = dialogService;
        _inspectionContext = inspectionContext;
        _ = resultStore;

        ProductModels = new ObservableCollection<ProductModelDefinition>(_configuration.ProductModels);
        Cameras = cameras;
        TrainingImages = [];
        SelectedTrainingImages = [];
        TrainingTaskLabels = [];
        TaskOverlays = [];

        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;

        // 初始化时同步当前值
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(SerialNumber));
        selectedCamera = _inspectionContext.SelectedCamera ?? Cameras.FirstOrDefault();
        AppDiagnostics.Debug("clip-training", $"ClipTrainingLibraryViewModel init. SelectedCamera={selectedCamera?.Name ?? "null"}, Cameras.Count={Cameras.Count}");
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
        AppDiagnostics.Debug("clip-training", $"OnInspectionContextSelectedCameraChanged: context={camera?.Name ?? "null"}, current={SelectedCamera?.Name ?? "null"}");
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
        AppDiagnostics.Debug("clip-training", $"RefreshPublicParams: ProductCode={productCode}, SerialNumber={_inspectionContext.SerialNumber}, CtxCamera={ctxCamera?.Name ?? "null"}, CurrentCamera={SelectedCamera?.Name ?? "null"}");
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

    public ObservableCollection<TrainingImageRecordViewModel> TrainingImages { get; }

    public ObservableCollection<TrainingImageRecordViewModel> SelectedTrainingImages { get; }

    public ObservableCollection<TrainingTaskLabelViewModel> TrainingTaskLabels { get; }

    public ObservableCollection<ImageOverlayItem> TaskOverlays { get; }

    public event EventHandler<string>? AlarmRaised;

    public event EventHandler? AlarmCleared;

    public bool CanOperate => !IsBusy && SelectedProductModel != null && SelectedCamera != null;

    public bool CanOperateWithTasks => CanOperate && _currentTasks.Count > 0;

    public bool CanSetSelectedImageLabels => CanOperateWithTasks && SelectedTrainingImage != null;

    public bool CanRemoveSelectedTrainingImages => CanOperate && SelectedTrainingImages.Count > 0;

    public bool CanClearTrainingImages => CanOperate && TrainingImages.Any(image => !image.IsProtected);

    public string SelectedProductIdText => SelectedProductModel?.Id ?? "--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanOperateWithTasks))]
    [NotifyPropertyChangedFor(nameof(CanSetSelectedImageLabels))]
    [NotifyPropertyChangedFor(nameof(SelectedProductIdText))]
    private ProductModelDefinition? selectedProductModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanOperateWithTasks))]
    [NotifyPropertyChangedFor(nameof(CanSetSelectedImageLabels))]
    private CameraViewModel? selectedCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetSelectedImageLabels))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelectedTrainingImages))]
    private TrainingImageRecordViewModel? selectedTrainingImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanOperateWithTasks))]
    [NotifyPropertyChangedFor(nameof(CanSetSelectedImageLabels))]
    private bool isBusy;

    [ObservableProperty]
    private ImageSource? previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTitleDisplay))]
    private string previewTitle = "未选择图片";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDetailDisplay))]
    private string previewDetail = "选择左侧图像后显示预览和任务 ROI。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OperationMessageDisplay))]
    private string operationMessage = "选择成品号和相机后导入训练图片。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VectorSetStatusTextDisplay))]
    private string vectorSetStatusText = "未加载";

    public string PreviewTitleDisplay => PreviewTitle;

    public string PreviewDetailDisplay => PreviewDetail;

    public string OperationMessageDisplay => OperationMessage;

    public string VectorSetStatusTextDisplay => VectorSetStatusText;

    partial void OnSelectedProductModelChanged(ProductModelDefinition? value)
    {
        if (value != null)
        {
            _configuration.SelectedProductModelId = value.Id;
            _storage.Save(_configuration);
        }

        OnPropertyChanged(nameof(SelectedProductIdText));
        _ = RefreshAsync();
        RefreshCommandStates();
    }

    partial void OnSelectedCameraChanged(CameraViewModel? value)
    {
        AppDiagnostics.Debug("clip-training", $"OnSelectedCameraChanged: {value?.Name ?? "null"}");
        if (value != null)
        {
            _inspectionContext.SelectedCamera = value;
        }
        _ = RefreshAsync();
        RefreshCommandStates();
    }

    partial void OnSelectedTrainingImageChanged(TrainingImageRecordViewModel? value)
    {
        ApplySelectedTrainingImageAsync(value);
        RefreshCommandStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    public void RefreshProductModels()
    {
        var selectedId = SelectedProductModel?.Id ?? _configuration.SelectedProductModelId;
        ProductModels.Clear();
        foreach (var product in _configuration.ProductModels)
        {
            ProductModels.Add(product);
        }

        SelectedProductModel = ProductModels.FirstOrDefault(product =>
            string.Equals(product.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? ProductModels.FirstOrDefault();
        _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshAsync()
    {
        if (SelectedProductModel == null || SelectedCamera == null)
        {
            ClearDocumentView();
            OperationMessage = "请先选择成品号和相机。";
            return;
        }

        await RunOperationAsync("正在加载相机训练库...", async () =>
        {
            _currentTasks = ResolveCurrentClassificationTasks();
            _document = await _libraryService.LoadCameraTrainingDocumentAsync(
                SelectedProductModel.Id,
                SelectedCamera.ConfigurationId,
                SelectedCamera.Name,
                _currentTasks,
                _configuration.Alignments.ToArray());
            ApplyDocument(_document, SelectedTrainingImage?.Id);
            OperationMessage = _currentTasks.Count == 0
                ? "当前成品号和相机没有启用的分类任务。"
                : "相机训练库已刷新。";
        }, allowWithoutOperate: true);
    }

    [RelayCommand(CanExecute = nameof(CanOperateWithTasks))]
    private async Task AddImagesFromFileAsync()
    {
        if (SelectedProductModel == null || SelectedCamera == null)
        {
            return;
        }

        var imagePaths = _dialogService.OpenImageFiles(multiselect: true);
        if (imagePaths.Count == 0)
        {
            return;
        }

        await RunOperationAsync("正在导入并对齐训练图片...", async () =>
        {
            TrainingImageRecord? lastRecord = null;
            foreach (var imagePath in imagePaths)
            {
                var result = await _libraryService.AddCameraTrainingImageFromFileAsync(new CameraTrainingFileImportRequest
                {
                    ProductModel = SelectedProductModel,
                    CameraId = SelectedCamera.ConfigurationId,
                    CameraName = SelectedCamera.Name,
                    Tasks = _currentTasks,
                    Alignments = _configuration.Alignments.ToArray(),
                    SourceName = "读取图片",
                    SourceImagePath = imagePath
                });
                _document = result.Document;
                lastRecord = result.Record;
            }

            ApplyDocument(_document, lastRecord?.Id);
            OperationMessage = $"已导入 {imagePaths.Count} 张图片，所有任务默认未标注。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperateWithTasks))]
    private async Task CaptureImageAsync()
    {
        if (SelectedProductModel == null || SelectedCamera == null)
        {
            return;
        }

        await RunOperationAsync("正在相机拍照并对齐训练图片...", async () =>
        {
            if (!SelectedCamera.IsAcquisitionConfigured)
            {
                throw new InvalidOperationException($"{SelectedCamera.Name} 未配置取图来源。");
            }

            if (!SelectedCamera.IsConnected)
            {
                var connect = await _cameraService.TryConnectAsync(SelectedCamera);
                if (!connect.Success)
                {
                    throw new InvalidOperationException(connect.Message, connect.Exception);
                }

                SelectedCamera.IsConnected = true;
            }

            using var capture = await _cameraService.CaptureAsync(SelectedCamera);
            SelectedCamera.SetInspectionSourceFromCapture(
                capture.Image,
                capture.Frame,
                capture.DisplayName,
                capture.ReportedFps);

            var result = await _libraryService.AddCameraTrainingImageFromMatAsync(new CameraTrainingMatImportRequest
            {
                ProductModel = SelectedProductModel,
                CameraId = SelectedCamera.ConfigurationId,
                CameraName = SelectedCamera.Name,
                Tasks = _currentTasks,
                Alignments = _configuration.Alignments.ToArray(),
                SourceName = "相机拍照",
                SourceImage = capture.Frame
            });
            _document = result.Document;
            ApplyDocument(_document, result.Record.Id);
            OperationMessage = $"{SelectedCamera.Name} 相机拍照训练图已加入，所有任务默认未标注。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedImageLabels))]
    private async Task SetSelectedImageAllOkAsync()
    {
        await SetSelectedImageLabelsAsync(TrainingLabelState.OK);
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedImageLabels))]
    private async Task SetSelectedImageAllNgAsync()
    {
        await SetSelectedImageLabelsAsync(TrainingLabelState.NG);
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedImageLabels))]
    private async Task SetSelectedImageAllIgnoredAsync()
    {
        await SetSelectedImageLabelsAsync(TrainingLabelState.Ignored);
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedImageLabels))]
    private async Task SetSelectedImageAllUnlabeledAsync()
    {
        await SetSelectedImageLabelsAsync(TrainingLabelState.Unlabeled);
    }

    [RelayCommand(CanExecute = nameof(CanOperateWithTasks))]
    private async Task RebuildTrainingLibraryAsync()
    {
        if (SelectedProductModel == null)
        {
            return;
        }

        await RunOperationAsync("正在按标注重建所有相机训练库...", async () =>
        {
            var enabledCameras = Cameras.Where(c => c.IsEnabled).ToList();
            var allTasks = ResolveAllClassificationTasks();
            var results = new List<(string CameraName, string Message)>();

            // 并行重建所有相机的训练库
            var tasks = enabledCameras.Select(async camera =>
            {
                var cameraTasks = allTasks.Where(t => IsTaskForCamera(t, camera)).ToList();
                if (cameraTasks.Count == 0)
                {
                    return (camera.Name, "无分类任务，跳过");
                }

                var result = await _libraryService.RebuildCameraTrainingVectorSetsAsync(new CameraTrainingVectorSyncRequest
                {
                    ProductModel = SelectedProductModel,
                    CameraId = camera.ConfigurationId,
                    Tasks = cameraTasks
                });
                return (camera.Name, result.SummaryText);
            });

            var allResults = await Task.WhenAll(tasks);
            results.AddRange(allResults);

            // 刷新当前相机状态
            if (SelectedCamera != null)
            {
                _document = await _libraryService.LoadCameraTrainingDocumentAsync(
                    SelectedProductModel.Id,
                    SelectedCamera.ConfigurationId,
                    SelectedCamera.Name,
                    _currentTasks,
                    _configuration.Alignments.ToArray());
                ApplyDocument(_document, SelectedTrainingImage?.Id);
            }

            var summary = string.Join(Environment.NewLine, results.Select(r => $"【{r.CameraName}】{r.Message}"));
            OperationMessage = $"已重建 {results.Count(r => !r.Message.Contains("跳过"))} 个相机训练库";
            VectorSetStatusText = summary;
        });
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedTrainingImages))]
    private async Task RemoveSelectedTrainingImagesAsync()
    {
        if (_document == null || SelectedTrainingImages.Count == 0)
        {
            return;
        }

        var selected = SelectedTrainingImages.ToArray();
        if (selected.Any(image => image.IsProtected))
        {
            OperationMessage = "对齐模板图必须保留，不能移除。请取消选择对齐模板后再移除其他图像。";
            return;
        }

        var confirm = _dialogService.Confirm(
            $"确定移除选中的 {selected.Length} 张图像？\n对应的原图和已对齐图像文件也会删除。\n如需让模型训练库同步变化，请再执行“重建训练库”。",
            "移除训练图像");
        if (!confirm)
        {
            return;
        }

        await RunOperationAsync($"正在移除 {selected.Length} 张图像...", async () =>
        {
            var selectedIds = selected.Select(image => image.Id).ToArray();
            var selectedIdSet = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSelectedId = TrainingImages
                .FirstOrDefault(image => !selectedIdSet.Contains(image.Id))
                ?.Id;
            var removed = await _libraryService.RemoveCameraTrainingImagesAsync(_document, selectedIds);
            ApplyDocument(_document, nextSelectedId);
            OperationMessage = $"已移除 {removed} 张图像。重建训练库后模型样本才会同步更新。";
        }, allowWithoutOperate: true);
    }

    [RelayCommand(CanExecute = nameof(CanClearTrainingImages))]
    private async Task ClearTrainingImagesAsync()
    {
        if (_document == null || TrainingImages.Count == 0)
        {
            return;
        }

        var imageCount = TrainingImages.Count;
        var confirm = _dialogService.Confirm(
            $"确定清空当前图像列表的 {imageCount} 张图像？\n对应的原图和已对齐图像文件也会删除。\n如需清空模型训练样本，请再执行“重建训练库”。",
            "清空训练图像");
        if (!confirm)
        {
            return;
        }

        await RunOperationAsync($"正在清空 {imageCount} 张图像...", async () =>
        {
            var imageIds = TrainingImages
                .Where(image => !image.IsProtected)
                .Select(image => image.Id)
                .ToArray();
            var removed = await _libraryService.RemoveCameraTrainingImagesAsync(_document, imageIds);
            ApplyDocument(_document, _document.Images.FirstOrDefault(image => image.IsProtected)?.Id);
            OperationMessage = $"已清空 {removed} 张普通图像，对齐模板图已保留。重建训练库后模型样本才会同步更新。";
        }, allowWithoutOperate: true);
    }

    [RelayCommand(CanExecute = nameof(CanOperateWithTasks))]
    private void OpenTaskSampleLibrary(TrainingTaskLabelViewModel? taskLabel)
    {
        if (taskLabel == null)
        {
            return;
        }

        var viewModel = new TaskSampleLibraryViewModel(taskLabel.Definition, _libraryService, _dialogService);
        var dialog = new TaskSampleLibraryDialog(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    private async void ApplySelectedTrainingImageAsync(TrainingImageRecordViewModel? image)
    {
        try
        {
            if (image == null)
            {
                PreviewImage = null;
                PreviewTitle = "未选择图片";
                PreviewDetail = "选择左侧图像后显示预览和任务 ROI。";
                TrainingTaskLabels.Clear();
                TaskOverlays.Clear();
                return;
            }

            await ShowPreviewAsync(image.Record);
            RefreshTaskLabelCards(image.Record);
            RebuildOverlays(image.Record);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    public void SetSelectedTrainingImages(IEnumerable<TrainingImageRecordViewModel> images)
    {
        SelectedTrainingImages.Clear();
        foreach (var image in images)
        {
            SelectedTrainingImages.Add(image);
        }

        RefreshCommandStates();
    }

    private async Task SetSelectedImageLabelsAsync(TrainingLabelState state)
    {
        if (_document == null || SelectedTrainingImage == null)
        {
            return;
        }

        foreach (var task in _currentTasks)
        {
            var label = CameraTrainingLibraryStore.EnsureLabel(SelectedTrainingImage.Record, task.Id);
            label.State = state;
            label.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _libraryService.SaveCameraTrainingDocumentAsync(_document);
        RefreshAfterLabelChange();
        OperationMessage = $"当前图片全部任务已设置为 {FormatState(state)}。";
    }

    private async Task OnTaskLabelChangedAsync(TrainingTaskLabelViewModel item)
    {
        try
        {
            if (_document == null || SelectedTrainingImage == null)
            {
                return;
            }

            await _libraryService.SaveCameraTrainingDocumentAsync(_document);
            RefreshAfterLabelChange();
            OperationMessage = $"{item.TaskName} 已设置为 {item.StateTextDisplay}。";
        }
        catch (Exception ex)
        {
            SetError(CameraErrorFormatter.ToUserMessage(ex));
        }
    }

    private void RefreshAfterLabelChange()
    {
        foreach (var image in TrainingImages)
        {
            image.Refresh();
        }

        foreach (var label in TrainingTaskLabels)
        {
            label.RefreshCounts();
        }

        if (SelectedTrainingImage != null)
        {
            RebuildOverlays(SelectedTrainingImage.Record);
        }

        VectorSetStatusText = BuildStatusText(_document, _currentTasks, null);
    }

    private void ApplyDocument(
        CameraTrainingLibraryDocument? document,
        string? selectedImageId)
    {
        TrainingImages.Clear();
        SelectedTrainingImages.Clear();
        if (document == null)
        {
            ClearDocumentView();
            return;
        }

        foreach (var image in document.Images
                     .OrderBy(image => image.IsProtected ? 0 : 1)
                     .ThenByDescending(image => image.CreatedAt))
        {
            TrainingImages.Add(new TrainingImageRecordViewModel(image, _currentTasks));
        }

        var next = TrainingImages.FirstOrDefault(image =>
                       string.Equals(image.Id, selectedImageId, StringComparison.OrdinalIgnoreCase))
                   ?? TrainingImages.FirstOrDefault();
        SelectedTrainingImage = next;
        if (next == null)
        {
            ApplySelectedTrainingImageAsync(null);
        }

        VectorSetStatusText = BuildStatusText(document, _currentTasks, null);
        RefreshCommandStates();
    }

    private void ClearDocumentView()
    {
        _document = null;
        _currentTasks = Array.Empty<InspectionTaskDefinition>();
        TrainingImages.Clear();
        SelectedTrainingImages.Clear();
        TrainingTaskLabels.Clear();
        TaskOverlays.Clear();
        SelectedTrainingImage = null;
        PreviewImage = null;
        PreviewTitle = "未选择图片";
        PreviewDetail = "选择左侧图像后显示预览和任务 ROI。";
        VectorSetStatusText = "未加载";
    }

    private void RefreshTaskLabelCards(TrainingImageRecord record)
    {
        TrainingTaskLabels.Clear();
        foreach (var task in _currentTasks)
        {
            var label = CameraTrainingLibraryStore.EnsureLabel(record, task.Id);
            TrainingTaskLabels.Add(new TrainingTaskLabelViewModel(
                task,
                label,
                () => _document,
                changed => _ = OnTaskLabelChangedAsync(changed)));
        }
    }

    private void RebuildOverlays(TrainingImageRecord record)
    {
        TaskOverlays.Clear();
        foreach (var task in _currentTasks)
        {
            if (task.Roi == null || task.Roi.IsFullImage)
            {
                continue;
            }

            var label = record.Labels.FirstOrDefault(item =>
                string.Equals(item.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
            var state = label?.State ?? TrainingLabelState.Unlabeled;
            TaskOverlays.Add(CreateTaskOverlay(task, state));
        }
    }

    private ImageOverlayItem CreateTaskOverlay(
        InspectionTaskDefinition task,
        TrainingLabelState state)
    {
        _configuration.RoiOverlay ??= new RoiOverlaySettings();
        _configuration.RoiOverlay.Normalize();
        var stroke = StateToStroke(state);
        var overlay = InspectionRoiGeometry.ToOverlayItem(
            task.Roi,
            stroke,
            RoiOverlayVisualFactory.CreateFill(stroke, _configuration.RoiOverlay.FillOpacity),
            state == TrainingLabelState.Unlabeled ? 1.8 : 2.4);
        overlay.Id = task.Id;
        overlay.Text = $"{task.Name} {FormatState(state)}";
        overlay.LabelBackground = StateToSoftBrush(state);
        overlay.LabelForeground = state switch
        {
            TrainingLabelState.Unlabeled => UnlabeledBrush,
            TrainingLabelState.Ignored => IgnoredBrush,
            _ => stroke
        };
        overlay.LabelFontSize = 13;
        return overlay;
    }

    private async Task ShowPreviewAsync(TrainingImageRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AlignedImageRelativePath))
        {
            PreviewImage = null;
            PreviewTitle = record.DisplayName();
            PreviewDetail = "没有已对齐图像。";
            return;
        }

        var imagePath = ResolveAssetPath(record.AlignedImageRelativePath);
        PreviewImage = await Task.Run(() => ImageSourceFileStorage.LoadImage(imagePath));
        PreviewTitle = record.DisplayName();
        PreviewDetail = $"{record.Width} x {record.Height}  {record.SourceDisplayName()}  {FormatLocalTime(record.CreatedAt)}";
    }

    private string ResolveAssetPath(string relativePath)
    {
        return _libraryService.GetCameraTrainingImageFullPath(relativePath);
    }

    private IReadOnlyList<InspectionTaskDefinition> ResolveCurrentClassificationTasks()
    {
        if (SelectedProductModel == null || SelectedCamera == null)
        {
            return Array.Empty<InspectionTaskDefinition>();
        }

        return _configuration.Tasks
            .Where(task =>
                task.Enabled &&
                task.Kind == InspectionTaskKind.Classification &&
                string.Equals(task.ProductModelId, SelectedProductModel.Id, StringComparison.OrdinalIgnoreCase) &&
                IsTaskForCamera(task, SelectedCamera))
            .OrderBy(task => task.Name)
            .ToArray();
    }

    private IReadOnlyList<InspectionTaskDefinition> ResolveAllClassificationTasks()
    {
        if (SelectedProductModel == null)
        {
            return Array.Empty<InspectionTaskDefinition>();
        }

        return _configuration.Tasks
            .Where(task =>
                task.Enabled &&
                task.Kind == InspectionTaskKind.Classification &&
                string.Equals(task.ProductModelId, SelectedProductModel.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.Name)
            .ToArray();
    }

    private static bool IsTaskForCamera(InspectionTaskDefinition task, CameraViewModel camera)
    {
        return string.Equals(task.CameraId, camera.ConfigurationId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(task.CameraId, camera.Name, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildStatusText(
        CameraTrainingLibraryDocument? document,
        IReadOnlyList<InspectionTaskDefinition> tasks,
        string? syncMessage)
    {
        if (SelectedProductModel == null || SelectedCamera == null)
        {
            return "未加载";
        }

        if (tasks.Count == 0)
        {
            return $"成品号：{SelectedProductModel.Id}{Environment.NewLine}相机：{SelectedCamera.Name}{Environment.NewLine}没有启用的分类任务。";
        }

        var imageCount = document?.Images.Count ?? 0;
        var lines = new List<string>
        {
            $"成品号：{SelectedProductModel.Id}",
            $"相机：{SelectedCamera.Name}",
            $"图像（已对齐）：{imageCount}",
            $"分类任务：{tasks.Count}"
        };

        foreach (var task in tasks)
        {
            var counts = CountTaskLabels(document, task.Id);
            lines.Add($"{task.Name}：OK {counts.Ok} / NG {counts.Ng} / 忽略 {counts.Ignored} / 未标注 {counts.Unlabeled}");
        }

        if (!string.IsNullOrWhiteSpace(syncMessage))
        {
            lines.Add("");
            lines.Add(syncMessage);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static TaskLabelCounts CountTaskLabels(
        CameraTrainingLibraryDocument? document,
        string taskId)
    {
        var counts = new TaskLabelCounts();
        if (document == null)
        {
            return counts;
        }

        foreach (var image in document.Images)
        {
            var label = image.Labels.FirstOrDefault(item =>
                string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            switch (label?.State ?? TrainingLabelState.Unlabeled)
            {
                case TrainingLabelState.OK:
                    counts.Ok++;
                    break;
                case TrainingLabelState.NG:
                    counts.Ng++;
                    break;
                case TrainingLabelState.Ignored:
                    counts.Ignored++;
                    break;
                default:
                    counts.Unlabeled++;
                    break;
            }
        }

        return counts;
    }

    private async Task RunOperationAsync(
        string busyMessage,
        Func<Task> operation,
        bool allowWithoutOperate = false)
    {
        if (!allowWithoutOperate && !CanOperate)
        {
            return;
        }

        IsBusy = true;
        OperationMessage = busyMessage;
        ClearAlarm();
        try
        {
            await operation();
            ClearAlarm();
        }
        catch (Exception ex)
        {
            SetError(CameraErrorFormatter.ToUserMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetError(string message)
    {
        OperationMessage = message;
        AlarmRaised?.Invoke(this, message);
    }

    private void ClearAlarm()
    {
        AlarmCleared?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanOperateWithTasks));
        OnPropertyChanged(nameof(CanSetSelectedImageLabels));
        OnPropertyChanged(nameof(CanRemoveSelectedTrainingImages));
        OnPropertyChanged(nameof(CanClearTrainingImages));
        RefreshCommand.NotifyCanExecuteChanged();
        AddImagesFromFileCommand.NotifyCanExecuteChanged();
        CaptureImageCommand.NotifyCanExecuteChanged();
        SetSelectedImageAllOkCommand.NotifyCanExecuteChanged();
        SetSelectedImageAllNgCommand.NotifyCanExecuteChanged();
        SetSelectedImageAllIgnoredCommand.NotifyCanExecuteChanged();
        SetSelectedImageAllUnlabeledCommand.NotifyCanExecuteChanged();
        RebuildTrainingLibraryCommand.NotifyCanExecuteChanged();
        RemoveSelectedTrainingImagesCommand.NotifyCanExecuteChanged();
        ClearTrainingImagesCommand.NotifyCanExecuteChanged();
        OpenTaskSampleLibraryCommand.NotifyCanExecuteChanged();
    }

    private static Brush StateToStroke(TrainingLabelState state)
    {
        return state switch
        {
            TrainingLabelState.OK => UiBrushes.Success,
            TrainingLabelState.NG => UiBrushes.Danger,
            TrainingLabelState.Ignored => IgnoredBrush,
            TrainingLabelState.Unlabeled => UnlabeledBrush,
            _ => DefaultStroke
        };
    }

    private static Brush StateToSoftBrush(TrainingLabelState state)
    {
        return state switch
        {
            TrainingLabelState.OK => OkSoftBrush,
            TrainingLabelState.NG => NgSoftBrush,
            TrainingLabelState.Ignored => IgnoredSoftBrush,
            _ => UnlabeledSoftBrush
        };
    }

    private static string FormatState(TrainingLabelState state)
    {
        return state switch
        {
            TrainingLabelState.OK => "OK",
            TrainingLabelState.NG => "NG",
            TrainingLabelState.Ignored => "忽略",
            _ => "未标注"
        };
    }

    private static string FormatLocalTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed class TaskLabelCounts
    {
        public int Ok { get; set; }

        public int Ng { get; set; }

        public int Ignored { get; set; }

        public int Unlabeled { get; set; }
    }
}

public sealed partial class TrainingImageRecordViewModel : ObservableObject
{
    private readonly IReadOnlyList<InspectionTaskDefinition> _tasks;

    public TrainingImageRecordViewModel(
        TrainingImageRecord record,
        IReadOnlyList<InspectionTaskDefinition> tasks)
    {
        Record = record;
        _tasks = tasks;
    }

    public TrainingImageRecord Record { get; }

    public string Id => Record.Id;

    public bool IsProtected => Record.IsProtected;

    public string DisplayTitle => IsProtected ? "对齐模板" : Record.ImageName();

    public string DetailText
    {
        get
        {
            var counts = CountLabels();
            return string.Join(Environment.NewLine,
                $"类型：{(IsProtected ? "对齐模板（不可删除）" : "训练图像")}",
                $"时间：{CreatedAtText}",
                $"来源：{Record.SourceDisplayName()}",
                $"尺寸：{Record.Width}x{Record.Height}",
                $"标注：OK {counts.Ok} / NG {counts.Ng} / 忽略 {counts.Ignored} / 未标注 {counts.Unlabeled}");
        }
    }

    public string CreatedAtText => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DetailText));
    }

    private (int Ok, int Ng, int Ignored, int Unlabeled) CountLabels()
    {
        var ok = 0;
        var ng = 0;
        var ignored = 0;
        var unlabeled = 0;
        foreach (var task in _tasks)
        {
            var label = Record.Labels.FirstOrDefault(item =>
                string.Equals(item.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
            switch (label?.State ?? TrainingLabelState.Unlabeled)
            {
                case TrainingLabelState.OK:
                    ok++;
                    break;
                case TrainingLabelState.NG:
                    ng++;
                    break;
                case TrainingLabelState.Ignored:
                    ignored++;
                    break;
                default:
                    unlabeled++;
                    break;
            }
        }

        return (ok, ng, ignored, unlabeled);
    }
}

public sealed partial class TrainingTaskLabelViewModel : ObservableObject
{
    private readonly TrainingTaskLabel _label;
    private readonly Func<CameraTrainingLibraryDocument?> _documentAccessor;
    private readonly Action<TrainingTaskLabelViewModel> _stateChanged;

    public TrainingTaskLabelViewModel(
        InspectionTaskDefinition definition,
        TrainingTaskLabel label,
        Func<CameraTrainingLibraryDocument?> documentAccessor,
        Action<TrainingTaskLabelViewModel> stateChanged)
    {
        Definition = definition;
        _label = label;
        _documentAccessor = documentAccessor;
        _stateChanged = stateChanged;
    }

    public InspectionTaskDefinition Definition { get; }

    public string TaskName => Definition.Name;

    public string VectorSetId => Definition.EnsureClipVectorSet().VectorSetId;

    public TrainingLabelState State
    {
        get => _label.State;
        set
        {
            if (_label.State == value)
            {
                return;
            }

            _label.State = value;
            _label.UpdatedAt = DateTimeOffset.UtcNow;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateTextDisplay));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(StateBackground));
            OnPropertyChanged(nameof(IsUnlabeled));
            OnPropertyChanged(nameof(IsOk));
            OnPropertyChanged(nameof(IsNg));
            OnPropertyChanged(nameof(IsIgnored));
            _stateChanged(this);
        }
    }

    public string StateText => State switch
    {
        TrainingLabelState.OK => "OK",
        TrainingLabelState.NG => "NG",
        TrainingLabelState.Ignored => "忽略",
        _ => "未标注"
    };

    public string StateTextDisplay => StateText;

    public Brush StateBrush => State switch
    {
        TrainingLabelState.OK => UiBrushes.Success,
        TrainingLabelState.NG => UiBrushes.Danger,
        TrainingLabelState.Ignored => UiBrushes.TextMuted,
        _ => ClipTrainingLibraryViewModel.UnlabeledBrush
    };

    public Brush StateBackground => State switch
    {
        TrainingLabelState.OK => ClipTrainingLibraryViewModel.OkSoftBrush,
        TrainingLabelState.NG => ClipTrainingLibraryViewModel.NgSoftBrush,
        TrainingLabelState.Ignored => ClipTrainingLibraryViewModel.IgnoredSoftBrush,
        _ => ClipTrainingLibraryViewModel.UnlabeledSoftBrush
    };

    public string CountsText
    {
        get
        {
            var document = _documentAccessor();
            if (document == null)
            {
                return "OK 0 / NG 0 / 忽略 0 / 未标注 0";
            }

            var ok = 0;
            var ng = 0;
            var ignored = 0;
            var unlabeled = 0;
            foreach (var image in document.Images)
            {
                var label = image.Labels.FirstOrDefault(item =>
                    string.Equals(item.TaskId, Definition.Id, StringComparison.OrdinalIgnoreCase));
                switch (label?.State ?? TrainingLabelState.Unlabeled)
                {
                    case TrainingLabelState.OK:
                        ok++;
                        break;
                    case TrainingLabelState.NG:
                        ng++;
                        break;
                    case TrainingLabelState.Ignored:
                        ignored++;
                        break;
                    default:
                        unlabeled++;
                        break;
                }
            }

            return $"OK {ok} / NG {ng} / 忽略 {ignored} / 未标注 {unlabeled}";
        }
    }

    public string CountsTextDisplay => CountsText;

    public bool IsUnlabeled
    {
        get => State == TrainingLabelState.Unlabeled;
        set
        {
            if (value)
            {
                State = TrainingLabelState.Unlabeled;
            }
        }
    }

    public bool IsOk
    {
        get => State == TrainingLabelState.OK;
        set
        {
            if (value)
            {
                State = TrainingLabelState.OK;
            }
        }
    }

    public bool IsNg
    {
        get => State == TrainingLabelState.NG;
        set
        {
            if (value)
            {
                State = TrainingLabelState.NG;
            }
        }
    }

    public bool IsIgnored
    {
        get => State == TrainingLabelState.Ignored;
        set
        {
            if (value)
            {
                State = TrainingLabelState.Ignored;
            }
        }
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(CountsTextDisplay));
    }

    [RelayCommand]
    private void SetUnlabeled()
    {
        State = TrainingLabelState.Unlabeled;
    }

    [RelayCommand]
    private void SetOk()
    {
        State = TrainingLabelState.OK;
    }

    [RelayCommand]
    private void SetNg()
    {
        State = TrainingLabelState.NG;
    }

    [RelayCommand]
    private void SetIgnored()
    {
        State = TrainingLabelState.Ignored;
    }
}

internal static class TrainingImageRecordExtensions
{
    public static string ImageName(this TrainingImageRecord record)
    {
        return string.IsNullOrWhiteSpace(record.OriginalFileName)
            ? record.Id
            : record.OriginalFileName;
    }

    public static string SourceDisplayName(this TrainingImageRecord record)
    {
        return record.Source switch
        {
            "读图" => "读取图片",
            "拍照" => "相机拍照",
            _ => string.IsNullOrWhiteSpace(record.Source) ? "--" : record.Source
        };
    }

    public static string DisplayName(this TrainingImageRecord record)
    {
        return $"{record.CreatedAt.ToLocalTime():MM-dd HH:mm:ss}  {record.ImageName()}";
    }
}
