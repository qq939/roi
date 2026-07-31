﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using VideoInferenceDemo;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Models;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly CameraAcquisitionService _cameraService = new();
    private readonly BarcodeScannerSerialPortService _barcodeScanner = new();
    private readonly RuntimeInspectionContext _inspectionContext = new();
    private readonly Mt3aModbusTcpIoClient _ioModule;
    private readonly ClipClassificationService _clipClassificationService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly InspectionConfigurationStorage _inspectionConfigurationStorage;
    private readonly IUserDialogService _dialogService = new UserDialogService();
    private readonly VisionRuntimePaths _runtimePaths;
    private readonly ClipRuntimeOptions _clipOptions;
    private readonly string _cameraConfigPath;
    private readonly Mt3aModbusTcpIoConfigurationStorage _ioConfigurationStorage;
    private readonly VisionAssetPathService _assetPathService;
    private readonly TaskImageAlignmentService _taskImageAlignmentService;
    private readonly ClipTrainingLibraryService _clipTrainingLibraryService;
    private readonly InspectionResultStore _inspectionResultStore;
    private readonly InspectionImageArchiveService _inspectionImageArchiveService;
    private readonly S3UploadService _s3UploadService;
    private readonly InspectionCycleService _inspectionCycleService;
    private readonly ProductionLoopService _productionLoop;

    public MainWindowViewModel()
    {
        AppDiagnostics.Info("main", "MainWindowViewModel initializing.");
        _runtimePaths = new VisionRuntimePaths();
        _runtimePaths.EnsureDirectories();
        _runtimePaths.MigrateLegacyFiles();
        _ioConfigurationStorage = new Mt3aModbusTcpIoConfigurationStorage(_runtimePaths.IoModuleConfigurationPath);
        var ioOptions = _ioConfigurationStorage.LoadOrCreate();
        _ioModule = new Mt3aModbusTcpIoClient(ioOptions);
        _clipOptions = ClipRuntimeOptions.FromRuntimePaths(_runtimePaths);
        _clipClassificationService = new ClipClassificationService(_clipOptions);
        _inspectionConfigurationStorage = new InspectionConfigurationStorage(_runtimePaths.InspectionConfigurationPath);
        _cameraConfigPath = _runtimePaths.CameraConfigurationPath;
        Cameras = SampleDataFactory.CreateCameras();
        Logs = [];
        InspectionConfiguration = _inspectionConfigurationStorage.Load();
        _assetPathService = new VisionAssetPathService(_runtimePaths);
        _taskImageAlignmentService = new TaskImageAlignmentService(_assetPathService);
        _clipTrainingLibraryService = new ClipTrainingLibraryService(
            _clipClassificationService,
            _assetPathService,
            _taskImageAlignmentService,
            _clipOptions,
            _runtimePaths);
        _inspectionResultStore = new InspectionResultStore(_runtimePaths.InspectionResultDatabasePath);
        _inspectionImageArchiveService = new InspectionImageArchiveService(InspectionConfiguration.ImageArchiveRootDirectory);

        var s3ConfigPath = Path.Combine(AppContext.BaseDirectory, "agent", ".env");
        var s3Config = S3UploadConfiguration.LoadFromEnvFile(s3ConfigPath);
        _s3UploadService = new S3UploadService(s3Config);
        if (_s3UploadService.IsEnabled)
        {
            AppDiagnostics.Info("main", "S3UploadService enabled and initialized.");
        }

        LoadCameraSettings();

        selectedCamera = Cameras.FirstOrDefault(camera => camera.IsEnabled) ?? Cameras.FirstOrDefault();
        AppDiagnostics.Debug("main", $"Init selectedCamera: {selectedCamera?.Name ?? "null"}, Cameras.Count={Cameras.Count}");
        _inspectionContext.SelectedCamera = selectedCamera;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;
        Results = [];
        HomeWorkspace = new HomeWorkspaceViewModel(Cameras, Results, Logs, selectedCamera, _barcodeScanner, _ioModule, _inspectionContext);
        SecondaryBoardWorkspace = new SecondaryBoardViewModel(
            InspectionConfiguration,
            HomeWorkspace,
            _inspectionConfigurationStorage);
        // 扫码枪扫描 SN 号码后，自动启动相机
        HomeWorkspace.SnScanned += OnSnScanned;
        _inspectionCycleService = new InspectionCycleService(
            InspectionConfiguration,
            Cameras,
            Results,
            _cameraService,
            new InspectionTaskExecutionService(
                _clipClassificationService,
                new ClipFrameImageMaterializer(_runtimePaths.ClipQueriesDirectory)),
            _taskImageAlignmentService,
            _clipTrainingLibraryService,
            _inspectionImageArchiveService,
            _inspectionResultStore,
            _ioModule,
            _barcodeScanner,
            new InspectionCycleCallbacks
            {
                Log = AddLog,
                SetAlarm = SetAlarm,
                ClearAlarm = ClearAlarm,
                SelectCamera = camera => SelectedCamera = camera,
                RefreshUiBeforeTaskAsync = RefreshUiBeforeTaskAsync,
                ResultsChanged = () =>
                {
                    OnPropertyChanged(nameof(OkCount));
                    OnPropertyChanged(nameof(NgCount));
                },
                SummaryChanged = HomeWorkspace.RefreshSummary
            },
            _s3UploadService);
        _productionLoop = new ProductionLoopService(
            _ioModule,
            cancellationToken => RunInspectionCycleAsync(cancellationToken, InspectionTriggerSource.Di),
            AddLog,
            () => IsRunning);
        ProductModelManagementWorkspace = new ProductModelManagementViewModel(
            InspectionConfiguration,
            Cameras,
            _cameraService,
            _inspectionConfigurationStorage,
            _barcodeScanner,
            _dialogService,
            _assetPathService,
            _clipOptions,
            _inspectionContext);
        TaskSettingsWorkspace = new TaskSettingsViewModel(
            InspectionConfiguration,
            Cameras,
            _cameraService,
            _inspectionConfigurationStorage,
            _assetPathService,
            _taskImageAlignmentService,
            _clipTrainingLibraryService,
            _inspectionImageArchiveService,
            _inspectionContext);
        ClipTrainingLibraryWorkspace = new ClipTrainingLibraryViewModel(
            InspectionConfiguration,
            Cameras,
            _cameraService,
            _inspectionConfigurationStorage,
            _clipTrainingLibraryService,
            _inspectionResultStore,
            _dialogService,
            _inspectionContext);
        InspectionResultQueryWorkspace = new InspectionResultQueryViewModel(_inspectionResultStore, _inspectionContext);
        ProductModelManagementWorkspace.ProductModelsChanged += OnProductModelsChanged;
        ProductModelManagementWorkspace.ProductModelChanged += OnProductModelChanged;
        ProductModelManagementWorkspace.AlarmRaised += OnWorkspaceAlarmRaised;
        ProductModelManagementWorkspace.AlarmCleared += OnWorkspaceAlarmCleared;
        TaskSettingsWorkspace.AlarmRaised += OnWorkspaceAlarmRaised;
        TaskSettingsWorkspace.AlarmCleared += OnWorkspaceAlarmCleared;
        ClipTrainingLibraryWorkspace.AlarmRaised += OnWorkspaceAlarmRaised;
        ClipTrainingLibraryWorkspace.AlarmCleared += OnWorkspaceAlarmCleared;
        CameraSettingsWorkspace = new CameraSettingsViewModel(Cameras, _cameraService, _cameraConfigPath, selectedCamera, _inspectionContext);
        ParameterSettingsWorkspace = new ParameterSettingsViewModel(
            InspectionConfiguration,
            _runtimePaths,
            _clipOptions,
            _inspectionConfigurationStorage,
            _inspectionImageArchiveService,
            SecondaryBoardWorkspace,
            _ioConfigurationStorage,
            _ioModule);
        ParameterSettingsWorkspace.OkThresholdApplied += OnOkThresholdApplied;
        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        NavigationItems = CreateNavigationItems();
        foreach (var item in NavigationItems)
        {
            item.PropertyChanged += OnNavigationItemPropertyChanged;
        }

        foreach (var camera in Cameras)
        {
            camera.PropertyChanged += OnCameraPropertyChanged;
        }

        selectedNavigationItem = NavigationItems[0];
        selectedNavigationItem.IsSelected = true;
        currentWorkspace = HomeWorkspace;
        _barcodeScanner.BarcodeScanned += OnBarcodeScanned;
        AppDiagnostics.Debug("scanner", $"[SUBSCRIBE] BarcodeScanned event subscribed");
        _barcodeScanner.Start();
        AddLog(_barcodeScanner.IsConnected ? "INFO" : "EVENT", $"扫码枪：{_barcodeScanner.StatusText}");
        _ioModule.ConnectionStateChanged += OnIoModuleConnectionStateChanged;
        _ioModule.OutputsWritten += OnIoModuleOutputsWritten;
        _ioModule.Start();
        AddLog("INFO", $"IO模块正在连接：{_ioModule.EndpointText} ({_ioModule.SettingsText})");
        // 相机连接改为扫码触发，不自动启动
        // _ = ConnectConfiguredCamerasSafelyAsync(_lifetimeCancellation.Token);
        _productionLoop.Start();
        AppDiagnostics.Info("main", $"MainWindowViewModel initialized. XTraceLogPath={AppDiagnostics.LogPath}");
    }

    public ObservableCollection<CameraViewModel> Cameras { get; }

    public ObservableCollection<InspectionResultRow> Results { get; }

    public ObservableCollection<LogEntry> Logs { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public RuntimeInspectionContext InspectionContext => _inspectionContext;

    public string ProductCode
    {
        get => _inspectionContext.ProductCode;
        set => _inspectionContext.ProductCode = value;
    }

    public string SerialNumber
    {
        get => _inspectionContext.SerialNumber;
        set => _inspectionContext.SerialNumber = value;
    }

    public InspectionWorkspaceConfiguration InspectionConfiguration { get; }

    public HomeWorkspaceViewModel HomeWorkspace { get; }

    public SecondaryBoardViewModel SecondaryBoardWorkspace { get; }

    public ProductModelManagementViewModel ProductModelManagementWorkspace { get; }

    public TaskSettingsViewModel TaskSettingsWorkspace { get; }

    public ClipTrainingLibraryViewModel ClipTrainingLibraryWorkspace { get; }

    public InspectionResultQueryViewModel InspectionResultQueryWorkspace { get; }

    public CameraSettingsViewModel CameraSettingsWorkspace { get; }

    public ParameterSettingsViewModel ParameterSettingsWorkspace { get; }

    public Mt3aModbusTcpIoClient IoModule => _ioModule;

    public ProductionLoopService ProductionLoop => _productionLoop;

    public void ReportApplicationException(string source, Exception exception)
    {
        var message = CameraErrorFormatter.ToUserMessage(exception);
        AddLog("WARN", $"{source}：{message}");
        SetAlarm(message);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText))]
    [NotifyPropertyChangedFor(nameof(RunStateBrush))]
    private bool isRunning;

    [ObservableProperty]
    private object currentWorkspace;

    [ObservableProperty]
    private CameraViewModel selectedCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentNavigationTitle))]
    [NotifyPropertyChangedFor(nameof(IsDetailMode))]
    private NavigationItemViewModel? selectedNavigationItem;

    [ObservableProperty]
    private string cameraOperationMessage = "请选择相机并连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailMode))]
    private bool isOverviewMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlarm))]
    private string alarmText = string.Empty;

    private bool alarmExpanded;

    public bool AlarmExpanded
    {
        get => alarmExpanded;
        set
        {
            if (SetProperty(ref alarmExpanded, value))
            {
                OnPropertyChanged(nameof(AlarmToggleTooltip));
            }
        }
    }

    public string AlarmToggleTooltip => AlarmExpanded ? "点击折叠" : "点击展开";

    public bool HasAlarm => !string.IsNullOrWhiteSpace(AlarmText);

    [RelayCommand]
    private void ToggleAlarm()
    {
        AlarmExpanded = !AlarmExpanded;
    }

    [RelayCommand]
    private void DismissAlarm()
    {
        AlarmText = string.Empty;
    }

    public bool IsDetailMode => SelectedNavigationItem?.Kind == NavigationItemKind.Camera;

    public string CurrentNavigationTitle => SelectedNavigationItem?.Title ?? "主页";

    public string RunStateText => IsRunning ? "运行中" : "已停止";

    public Brush RunStateBrush => IsRunning ? UiBrushes.Success : UiBrushes.TextMuted;

    public int OkCount => Results.Count(result => result.Result == "OK");

    public int NgCount => Results.Count(result => result.Result != "OK");

    partial void OnSelectedCameraChanged(CameraViewModel value)
    {
        AppDiagnostics.Debug("main", $"OnSelectedCameraChanged: {value?.Name ?? "null"}");
        _inspectionContext.SelectedCamera = value;
        HomeWorkspace.SelectedCamera = value;
        CameraSettingsWorkspace.SelectedCamera = value;
        TaskSettingsWorkspace.SelectedCamera = value;
        ClipTrainingLibraryWorkspace.SelectedCamera = value;
        CameraOperationMessage = value.ConnectionStateText;
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        var camera = _inspectionContext.SelectedCamera;
        AppDiagnostics.Debug("main", $"OnInspectionContextSelectedCameraChanged: context={camera?.Name ?? "null"}, current={SelectedCamera?.Name ?? "null"}");
        if (camera != null && !ReferenceEquals(SelectedCamera, camera))
        {
            SelectedCamera = camera;
        }
    }

    [RelayCommand]
    private void Navigate(NavigationItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        SelectNavigationItem(item);
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        HomeWorkspace.ResetInspectionState();
        _productionLoop.Start();
        AddLog("INFO", "检测已启动");
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        AddLog("WARN", "检测已停止");
    }

    [RelayCommand]
    private async Task TriggerAsync()
    {
        HomeWorkspace.ResetInspectionState();
        ClearAlarm();
        AddLog("EVENT", $"手动触发已点击，运行状态={IsRunning}");
        AppDiagnostics.Info("inspection", "Manual trigger requested.");
        await RunInspectionCycleAsync(_lifetimeCancellation.Token, InspectionTriggerSource.Manual);
    }

    private Task RunInspectionCycleAsync(
        CancellationToken cancellationToken,
        InspectionTriggerSource triggerSource)
    {
        return _inspectionCycleService.RunAsync(new InspectionCycleRequest(
            triggerSource,
            IsRunning,
            HomeWorkspace.ProductCode,
            HomeWorkspace.SerialNumber), cancellationToken);
    }

    private int _isConnectingCameras;

    private async void OnSnScanned()
    {
        // 重入保护：如果已经在连接中，跳过本次调用
        if (Interlocked.CompareExchange(ref _isConnectingCameras, 1, 0) != 0)
        {
            AppDiagnostics.Info("scanner", "OnSnScanned skipped: already connecting");
            return;
        }

        try
        {
            AppDiagnostics.Info("scanner", "OnSnScanned triggered, checking cameras...");
            AddLog("EVENT", "扫码触发：开始启动相机");
            var enabledCameras = Cameras.Where(c => c.IsEnabled).ToList();
            AppDiagnostics.Info("scanner", $"Enabled cameras count: {enabledCameras.Count}, disconnected count: {enabledCameras.Count(c => !c.IsConnected)}");
            
            // 扫码后先启动相机
            foreach (var camera in enabledCameras)
            {
                AppDiagnostics.Info("scanner", $"Camera: {camera.Name}, IsConnected={camera.IsConnected}, IsEnabled={camera.IsEnabled}, IsAcquisitionConfigured={camera.IsAcquisitionConfigured}");
                
                if (!camera.IsConnected)
                {
                    AddLog("EVENT", $"启动相机：{camera.Name}");
                    if (!camera.IsAcquisitionConfigured)
                    {
                        AddLog("WARN", $"{camera.Name} 启动连接跳过：未配置设备");
                        continue;
                    }

                    camera.IsBusy = true;
                    try
                    {
                        var result = await _cameraService.TryConnectAsync(camera, _lifetimeCancellation.Token);
                        if (!result.Success)
                        {
                            camera.IsConnected = false;
                            camera.LastFrameInfo = "连接失败";
                            AddLog("ERROR", $"相机连接失败：{camera.Name} - {result.Message}");
                        }
                        else
                        {
                            camera.IsConnected = true;
                            camera.LastFrameInfo = "已连接";
                            AddLog("INFO", $"{camera.Name} 已连接");
                        }
                    }
                    catch (Exception ex)
                    {
                        var message = CameraErrorFormatter.ToUserMessage(ex);
                        camera.IsConnected = false;
                        camera.LastFrameInfo = "连接失败";
                        AddLog("ERROR", $"相机连接失败：{camera.Name} - {message}");
                    }
                    finally
                    {
                        camera.IsBusy = false;
                    }
                }
                else
                {
                    AppDiagnostics.Info("scanner", $"{camera.Name} is already connected, skipping");
                }
            }

            // 相机连接完成后，启动检测流程
            if (!IsRunning)
            {
                IsRunning = true;
                HomeWorkspace.ResetInspectionState();
                _productionLoop.Start();
                AddLog("INFO", "扫码触发：检测已启动");
                AppDiagnostics.Info("scanner", "Scan triggered inspection start");
            }

            AppDiagnostics.Info("scanner", "OnSnScanned completed");
        }
        finally
        {
            Interlocked.Exchange(ref _isConnectingCameras, 0);
        }
    }

    [RelayCommand]
    private async Task LoadCameraImageAsync(CameraViewModel? camera)
    {
        camera ??= SelectedCamera;
        if (camera == null)
        {
            return;
        }

        if (camera.IsBusy)
        {
            AddLog("EVENT", $"{camera.Name} 正在处理，读取图片已跳过");
            return;
        }

        var imagePaths = _dialogService.OpenImageFiles(multiselect: false);
        if (imagePaths.Count == 0)
        {
            return;
        }

        camera.IsBusy = true;
        try
        {
            var imagePath = imagePaths[0];
            using var loaded = await Task.Run(() => LoadInspectionImageFromFile(imagePath), _lifetimeCancellation.Token);
            camera.SetInspectionSourceFromLoadedImage(imagePath, loaded.Image, loaded.Mat);
            camera.DeviceDisplayName = Path.GetFileName(imagePath);
            camera.LastFrameInfo = FormatLoadedImageInfo(loaded.Image);
            SelectedCamera = camera;
            HomeWorkspace.ShowCamera(camera);
            AddLog("INFO", $"{camera.Name} 已读取图片：{Path.GetFileName(imagePath)}，{loaded.Mat.Width}x{loaded.Mat.Height}");
            AppDiagnostics.Info(
                "camera-detail",
                $"Loaded image for camera detail. Camera={camera.Name}, Path={imagePath}, Mat={loaded.Mat.Width}x{loaded.Mat.Height}, Frame={FormatImageSourceSize(loaded.Image)}");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetAlarm($"{camera.Name} 读取图片失败：{ex.Message}");
            AddLog("WARN", $"{camera.Name} 读取图片失败：{ex.Message}");
            AppDiagnostics.Error("camera-detail", $"Load camera image failed. Camera={camera.Name}", ex);
        }
        finally
        {
            camera.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InspectCameraImageAsync(CameraViewModel? camera)
    {
        camera ??= SelectedCamera;
        if (camera == null)
        {
            return;
        }

        if (camera.IsBusy)
        {
            AddLog("EVENT", $"{camera.Name} 正在处理，单相机检测已跳过");
            return;
        }

        camera.IsBusy = true;
        try
        {
            ClearAlarm();
            SelectedCamera = camera;
            HomeWorkspace.ShowCamera(camera);
            AddLog("EVENT", $"{camera.Name} 单相机检测按钮已点击");
            Mat frame;
            try
            {
                frame = await Task.Run(() => camera.CloneInspectionSourceMat(), _lifetimeCancellation.Token);
            }
            catch (InvalidOperationException ex)
            {
                SetAlarm($"{camera.Name} {ex.Message}");
                AddLog("WARN", $"{camera.Name} 单相机检测跳过：{ex.Message}");
                return;
            }

            using (frame)
            {
                var source = string.IsNullOrWhiteSpace(camera.InspectionSourceDescription)
                    ? "未记录"
                    : camera.InspectionSourceDescription;
                AddLog("EVENT", $"{camera.Name} 单相机检测输入：{source}，{frame.Width}x{frame.Height}");
                AppDiagnostics.Info(
                    "camera-detail",
                    $"Inspect camera image input. Camera={camera.Name}, Source={source}, Size={frame.Width}x{frame.Height}, RawPath={camera.InspectionSourcePath}, Display={FormatImageSourceSize(camera.Frame)}");
                await _inspectionCycleService.RunSingleCameraFrameAsync(
                    camera,
                    frame,
                    HomeWorkspace.ProductCode,
                    HomeWorkspace.SerialNumber,
                    _lifetimeCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetAlarm($"{camera.Name} 单相机检测失败：{ex.Message}");
            AddLog("WARN", $"{camera.Name} 单相机检测失败：{ex.Message}");
            AppDiagnostics.Error("camera-detail", $"Inspect camera image failed. Camera={camera.Name}", ex);
        }
        finally
        {
            camera.IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowOverview()
    {
        SelectNavigationItem(
            NavigationItems.FirstOrDefault(item => item.Kind == NavigationItemKind.Overview) ?? NavigationItems[0]);
    }

    [RelayCommand]
    private void ShowDetail()
    {
        var item = NavigationItems.FirstOrDefault(item =>
            item.Kind == NavigationItemKind.Camera &&
            ReferenceEquals(item.Camera, SelectedCamera));

        SelectNavigationItem(item ?? NavigationItems.FirstOrDefault(item => item.Kind == NavigationItemKind.Camera) ?? NavigationItems[0]);
    }

    [RelayCommand]
    private void OpenCamera(CameraViewModel? camera)
    {
        if (camera == null)
        {
            return;
        }

        SelectedCamera = camera;
        var item = NavigationItems.FirstOrDefault(navigationItem =>
            navigationItem.Kind == NavigationItemKind.Camera &&
            ReferenceEquals(navigationItem.Camera, camera));

        SelectNavigationItem(item ?? NavigationItems[0]);
    }

    private ObservableCollection<NavigationItemViewModel> CreateNavigationItems()
    {
        var items = new ObservableCollection<NavigationItemViewModel>
        {
            new(NavigationItemKind.Overview, "所有相机", "\uE80F")
        };

        foreach (var camera in Cameras)
        {
            items.Add(new NavigationItemViewModel(
                NavigationItemKind.Camera,
                camera.Name,
                "\uE722",
                camera));
        }

        items.Add(new NavigationItemViewModel(NavigationItemKind.ProductModelManagement, "型号管理", "\uE8F1", hasTopDivider: true));
        items.Add(new NavigationItemViewModel(NavigationItemKind.TaskSettings, "任务设置", "\uE8FD"));
        items.Add(new NavigationItemViewModel(NavigationItemKind.ClipTrainingLibrary, "模型训练库", "\uE950"));
        items.Add(new NavigationItemViewModel(NavigationItemKind.InspectionResultQuery, "数据查询", "\uE9D9"));
        items.Add(new NavigationItemViewModel(NavigationItemKind.CameraSettings, "相机设置", "\uE713"));
        items.Add(new NavigationItemViewModel(NavigationItemKind.ParameterSettings, "参数设置", "\uE9E9"));
        return items;
    }

    private void SelectNavigationItem(NavigationItemViewModel item)
    {
        AppDiagnostics.Debug("main", $"SelectNavigationItem: {item.Kind}, ProductCode={_inspectionContext.ProductCode}, SerialNumber={_inspectionContext.SerialNumber}, SelectedCamera={SelectedCamera?.Name ?? "null"}");
        foreach (var navigationItem in NavigationItems)
        {
            navigationItem.IsSelected = ReferenceEquals(navigationItem, item);
        }

        SelectedNavigationItem = item;

        switch (item.Kind)
        {
            case NavigationItemKind.Overview:
                IsOverviewMode = true;
                HomeWorkspace.ShowOverview();
                CurrentWorkspace = HomeWorkspace;
                break;

            case NavigationItemKind.Camera:
                IsOverviewMode = false;
                if (item.Camera != null)
                {
                    SelectedCamera = item.Camera;
                    HomeWorkspace.ShowCamera(item.Camera);
                }

                CurrentWorkspace = HomeWorkspace;
                break;

            case NavigationItemKind.ProductModelManagement:
                IsOverviewMode = false;
                ProductModelManagementWorkspace.RefreshPublicParams();
                CurrentWorkspace = ProductModelManagementWorkspace;
                break;

            case NavigationItemKind.TaskSettings:
                IsOverviewMode = false;
                TaskSettingsWorkspace.RefreshPublicParams();
                CurrentWorkspace = TaskSettingsWorkspace;
                break;

            case NavigationItemKind.ClipTrainingLibrary:
                IsOverviewMode = false;
                ClipTrainingLibraryWorkspace.RefreshProductModels();
                ClipTrainingLibraryWorkspace.RefreshPublicParams();
                CurrentWorkspace = ClipTrainingLibraryWorkspace;
                break;

            case NavigationItemKind.InspectionResultQuery:
                IsOverviewMode = false;
                InspectionResultQueryWorkspace.RefreshPublicParams();
                CurrentWorkspace = InspectionResultQueryWorkspace;
                break;

            case NavigationItemKind.CameraSettings:
                IsOverviewMode = false;
                CurrentWorkspace = CameraSettingsWorkspace;
                break;

            case NavigationItemKind.ParameterSettings:
                IsOverviewMode = false;
                CurrentWorkspace = ParameterSettingsWorkspace;
                break;
        }
    }

    private void AddLog(string level, string message)
    {
        WriteTraceLog(level, message);
        Logs.Add(new LogEntry(level, message));
        while (Logs.Count > 80)
        {
            Logs.RemoveAt(0);
        }
    }

    private void SetAlarm(string message)
    {
        AlarmText = message.Trim();
        if (!string.IsNullOrWhiteSpace(AlarmText))
        {
            AppDiagnostics.Warn("alarm", AlarmText);
        }
    }

    private void ClearAlarm()
    {
        if (string.IsNullOrWhiteSpace(AlarmText))
        {
            return;
        }

        AppDiagnostics.Info("alarm", $"Cleared alarm: {AlarmText}");
        AlarmText = string.Empty;
    }

    private static void WriteTraceLog(string level, string message)
    {
        var normalizedLevel = level.Trim().ToUpperInvariant();
        if (normalizedLevel is "ERROR" or "ERR")
        {
            AppDiagnostics.Error("ui-log", $"[{level}] {message}");
            return;
        }

        if (normalizedLevel is "WARN" or "WARNING" or "NG")
        {
            AppDiagnostics.Warn("ui-log", $"[{level}] {message}");
            return;
        }

        AppDiagnostics.Info("ui-log", $"[{level}] {message}");
    }

    private static string FormatCameraForTrace(CameraViewModel camera)
    {
        return
            $"Name={camera.Name}, Id={camera.ConfigurationId}, Enabled={camera.IsEnabled}, Configured={camera.IsAcquisitionConfigured}, ExplicitTarget={camera.HasExplicitAcquisitionTarget}, Connected={camera.IsConnected}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}, OpenCvSource={camera.OpenCvSource}, CameraIndex={camera.CameraIndex}, Trigger={camera.TriggerMode}";
    }

    private static string FormatLoadedImageInfo(ImageSource image)
    {
        return image is BitmapSource bitmap
            ? $"{DateTime.Now:HH:mm:ss}  {bitmap.PixelWidth:0}x{bitmap.PixelHeight:0}  文件"
            : $"{DateTime.Now:HH:mm:ss}  文件";
    }

    private static LoadedInspectionImage LoadInspectionImageFromFile(string imagePath)
    {
        var image = Cv2.ImRead(Path.GetFullPath(imagePath), ImreadModes.Color);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidOperationException($"图片读取失败：{imagePath}");
        }

        try
        {
            return new LoadedInspectionImage(image, MatImageSourceConverter.CreateImageSource(image));
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static string FormatImageSourceSize(ImageSource? image)
    {
        return image is BitmapSource bitmap
            ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}"
            : "--";
    }

    private sealed class LoadedInspectionImage(Mat mat, ImageSource image) : IDisposable
    {
        public Mat Mat { get; } = mat;

        public ImageSource Image { get; } = image;

        public void Dispose()
        {
            Mat.Dispose();
        }
    }

    private async Task ConnectConfiguredCamerasSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectConfiguredCamerasAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Warn("startup", "Startup camera connection canceled.");
        }
        catch (Exception ex)
        {
            var message = CameraErrorFormatter.ToUserMessage(ex);
            AddLog("WARN", $"启动连接相机失败：{message}");
            SetAlarm(message);
            AppDiagnostics.Error("startup", "Startup camera connection failed.", ex);
        }
    }

    private async Task ConnectConfiguredCamerasAsync(CancellationToken cancellationToken)
    {
        var enabledCameras = Cameras.Where(camera => camera.IsEnabled).ToArray();
        foreach (var camera in enabledCameras.Where(camera => !camera.IsAcquisitionConfigured))
        {
            AddLog("EVENT", $"{camera.Name} 启动连接跳过：未配置设备");
            AppDiagnostics.Info("startup", $"Startup connect skipped: not configured. {FormatCameraForTrace(camera)}");
        }

        var cameras = enabledCameras.Where(camera => camera.IsAcquisitionConfigured).ToArray();
        AppDiagnostics.Info(
            "startup",
            $"Connecting configured cameras. Count={cameras.Length}, Cameras={string.Join(", ", cameras.Select(camera => camera.Name))}");
        if (cameras.Length == 0)
        {
            AddLog("EVENT", "启动连接相机跳过：没有已启用并配置好的相机");
            return;
        }

        AddLog("INFO", $"启动连接相机：{string.Join(", ", cameras.Select(camera => camera.Name))}");

        foreach (var camera in cameras)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            camera.IsBusy = true;
            try
            {
                var result = await _cameraService.TryConnectAsync(camera, cancellationToken);
                if (!result.Success)
                {
                    camera.IsConnected = false;
                    camera.LastFrameInfo = "连接失败";
                    AddLog("WARN", $"{camera.Name} 连接失败：{result.Message}");
                    SetAlarm($"{camera.Name} {result.Message}");
                    if (result.Exception != null)
                    {
                        AppDiagnostics.Error("startup", $"{camera.Name} connect failed.", result.Exception);
                    }

                    continue;
                }

                camera.IsConnected = true;
                camera.LastFrameInfo = "已连接";
                AddLog("INFO", $"{camera.Name} 已连接");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppDiagnostics.Warn("startup", $"Connect configured cameras canceled at {camera.Name}.");
                return;
            }
            catch (Exception ex)
            {
                var message = CameraErrorFormatter.ToUserMessage(ex);
                camera.IsConnected = false;
                camera.LastFrameInfo = "连接失败";
                AddLog("WARN", $"{camera.Name} 连接失败：{message}");
                SetAlarm($"{camera.Name} {message}");
                AppDiagnostics.Error("startup", $"{camera.Name} connect failed.", ex);
            }
            finally
            {
                camera.IsBusy = false;
            }
        }
    }

    private static async Task RefreshUiBeforeTaskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }
        else
        {
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void OnNavigationItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationItemViewModel.Title) &&
            ReferenceEquals(sender, SelectedNavigationItem))
        {
            OnPropertyChanged(nameof(CurrentNavigationTitle));
        }
    }

    private void OnProductModelsChanged(object? sender, EventArgs e)
    {
        TaskSettingsWorkspace.RefreshProductModels();
        ClipTrainingLibraryWorkspace.RefreshProductModels();
    }

    private void OnWorkspaceAlarmRaised(object? sender, string message)
    {
        SetAlarm(message);
    }

    private void OnWorkspaceAlarmCleared(object? sender, EventArgs e)
    {
        ClearAlarm();
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraViewModel.IsEnabled) or nameof(CameraViewModel.IsAcquisitionConfigured) or nameof(CameraViewModel.HasExplicitAcquisitionTarget))
        {
            if (sender is CameraViewModel { IsAcquisitionConfigured: false } camera)
            {
                camera.ClearInspectionSource();
            }

            RefreshResultSummary();
        }
    }

    private void OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        AppDiagnostics.Debug("scanner", $"[HANDLER_START] Barcode='{e.Barcode}', Port='{e.PortName}', Time={e.ReceivedAt:HH:mm:ss.fff}, Thread={Environment.CurrentManagedThreadId}");

        HomeWorkspace.ApplyScannedBarcode(e.Barcode);
        var target = e.Barcode.Trim().Length >= 15 ? "成品号" : "序列号";
        AddLog("SCAN", $"{target}：{e.Barcode}");
        AppDiagnostics.Info("scanner", $"Barcode scanned. Target={target}, Length={e.Barcode.Trim().Length}, Value={e.Barcode}");

        AppDiagnostics.Debug("scanner", $"[HANDLER_END] Barcode='{e.Barcode}' processed");
    }

    private void OnIoModuleConnectionStateChanged(object? sender, IoConnectionStateChangedEventArgs e)
    {
        var level = e.IsConnected ? "INFO" : IsRunning ? "WARN" : "EVENT";
        AddLog(level, $"IO模块：{e.StatusText}");
    }

    private void OnIoModuleOutputsWritten(object? sender, IoOutputsWrittenEventArgs e)
    {
        AddLog("INFO", "IO输出已写入");
    }

    private void LoadCameraSettings()
    {
        try
        {
            AppDiagnostics.Info("startup", $"Loading camera settings. Path={_cameraConfigPath}");
            ResetCameraRuntimeProfiles();
            if (!File.Exists(_cameraConfigPath))
            {
                AppDiagnostics.Warn("startup", "Camera settings file not found. All cameras stay disabled until camera settings are saved.");
                return;
            }

            var settings = CameraSettingsStorage.Load(_cameraConfigPath);
            AppDiagnostics.Info("startup", $"Camera settings loaded. ProfileCount={settings.Cameras.Count}, SelectedCameraId={settings.SelectedCameraId}");
            
            // Debug: log loaded camera details
            foreach (var profile in settings.Cameras)
            {
                AppDiagnostics.Info("startup", $"  Loaded profile: Id={profile.Id}, Name={profile.Name}, Enabled={profile.Enabled}, Provider={profile.ProviderId}, DeviceId={profile.DeviceId}");
            }
            
            foreach (var camera in Cameras)
            {
                // First try to match by Id (ConfigurationId), then by Index as fallback
                var profile = settings.Cameras.FirstOrDefault(item =>
                    string.Equals(item.Id, camera.ConfigurationId, StringComparison.OrdinalIgnoreCase));
                
                // If no match by Id, try to match by Index (camera-01 matches Camera 1)
                if (profile == null)
                {
                    var expectedId = $"camera-{camera.Index:00}";
                    profile = settings.Cameras.FirstOrDefault(item =>
                        string.Equals(item.Id, expectedId, StringComparison.OrdinalIgnoreCase));
                }
                
                if (profile != null)
                {
                    camera.ApplyProfile(profile.Normalize(camera.Index));
                    AppDiagnostics.Info("startup", $"Applied camera profile. Id={profile.Id}, Name={profile.Name}, DeviceId={profile.DeviceId}");
                }
                else
                {
                    AppDiagnostics.Info("startup", $"No saved camera profile for {camera.ConfigurationId} (Index={camera.Index}). Available: {string.Join(", ", settings.Cameras.Select(c => c.Id))}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"相机配置加载失败：{ex.Message}");
            AppDiagnostics.Error("startup", "Camera settings load failed.", ex);
            throw;
        }
    }

    private void ResetCameraRuntimeProfiles()
    {
        foreach (var camera in Cameras)
        {
            camera.IsEnabled = false;
            camera.IsConnected = false;
            camera.IsBusy = false;
            camera.DeviceId = string.Empty;
            camera.OpenCvSource = string.Empty;
            camera.DeviceDisplayName = string.Empty;
            camera.ClearInspectionSource();
        }
    }

    private void RefreshResultSummary()
    {
        var enabledCameraNames = Cameras
            .Where(camera => camera.IsEnabled)
            .Select(camera => camera.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = Results.Count - 1; i >= 0; i--)
        {
            if (!enabledCameraNames.Contains(Results[i].CameraName))
            {
                Results.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(NgCount));
        HomeWorkspace.RefreshSummary();
    }

    private void OnProductModelChanged(object? sender, ProductModelDefinition productModel)
    {
        ParameterSettingsWorkspace.RefreshOkThresholdForCurrentProduct();
        AppDiagnostics.Info("main", $"Product model changed to {productModel.Id}, OK threshold refreshed.");
    }

    private void OnOkThresholdApplied(object? sender, EventArgs e)
    {
        TaskSettingsWorkspace.RefreshTaskThresholds();
        AppDiagnostics.Info("main", "OK threshold applied, task settings UI refreshed.");
    }

    private void OnInspectionContextProductCodeChanged(object? sender, EventArgs e)
    {
        AppDiagnostics.Debug("scanner", $"[CONTEXT_PRODUCT_CHANGED] Notifying UI update. NewValue='{ProductCode}'");
        OnPropertyChanged(nameof(ProductCode));
    }

    private void OnInspectionContextSerialNumberChanged(object? sender, EventArgs e)
    {
        AppDiagnostics.Debug("scanner", $"[CONTEXT_SN_CHANGED] Notifying UI update. NewValue='{SerialNumber}'");
        OnPropertyChanged(nameof(SerialNumber));
    }

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        ProductModelManagementWorkspace.ProductModelsChanged -= OnProductModelsChanged;
        ProductModelManagementWorkspace.ProductModelChanged -= OnProductModelChanged;
        ProductModelManagementWorkspace.AlarmRaised -= OnWorkspaceAlarmRaised;
        ProductModelManagementWorkspace.AlarmCleared -= OnWorkspaceAlarmCleared;
        TaskSettingsWorkspace.AlarmRaised -= OnWorkspaceAlarmRaised;
        TaskSettingsWorkspace.AlarmCleared -= OnWorkspaceAlarmCleared;
        ClipTrainingLibraryWorkspace.AlarmRaised -= OnWorkspaceAlarmRaised;
        ClipTrainingLibraryWorkspace.AlarmCleared -= OnWorkspaceAlarmCleared;
        ParameterSettingsWorkspace.OkThresholdApplied -= OnOkThresholdApplied;
        _inspectionContext.ProductCodeChanged -= OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged -= OnInspectionContextSerialNumberChanged;
        foreach (var camera in Cameras)
        {
            camera.PropertyChanged -= OnCameraPropertyChanged;
            camera.Dispose();
        }

        _barcodeScanner.BarcodeScanned -= OnBarcodeScanned;
        _barcodeScanner.Dispose();
        _ioModule.ConnectionStateChanged -= OnIoModuleConnectionStateChanged;
        _ioModule.OutputsWritten -= OnIoModuleOutputsWritten;
        _productionLoop.Dispose();
        SecondaryBoardWorkspace.Dispose();
        _ioModule.Dispose();
        _clipClassificationService.Dispose();
        _taskImageAlignmentService.Dispose();
        _s3UploadService.Dispose();
        _lifetimeCancellation.Dispose();
        _cameraService.Dispose();
    }
}
