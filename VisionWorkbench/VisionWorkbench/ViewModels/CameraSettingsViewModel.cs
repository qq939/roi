using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoInferenceDemo;
using VisionWorkbench.Models;
using VisionWorkbench.Services;
using Forms = System.Windows.Forms;

namespace VisionWorkbench.ViewModels;

public sealed partial class CameraSettingsViewModel : ObservableObject
{
    private readonly CameraAcquisitionService _cameraService;
    private readonly string _cameraConfigPath;
    private readonly RuntimeInspectionContext _inspectionContext;
    private bool _refreshingDeviceOptions;

    public CameraSettingsViewModel(
        ObservableCollection<CameraViewModel> cameras,
        CameraAcquisitionService cameraService,
        string cameraConfigPath,
        CameraViewModel? selectedCamera,
        RuntimeInspectionContext inspectionContext)
    {
        Cameras = cameras;
        _cameraService = cameraService;
        _cameraConfigPath = cameraConfigPath;
        _inspectionContext = inspectionContext;
        this.selectedCamera = selectedCamera ?? cameras[0];
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;
        ProviderOptions = new ObservableCollection<CameraProviderDescriptor>(_cameraService.GetProviders());
        DeviceOptions = new ObservableCollection<CameraDeviceInfo>();
        foreach (var camera in Cameras)
        {
            camera.PropertyChanged += OnCameraPropertyChanged;
        }

        RefreshSavedDeviceOption();
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        var camera = _inspectionContext.SelectedCamera;
        if (camera != null && !ReferenceEquals(SelectedCamera, camera))
        {
            SelectedCamera = camera;
        }
    }

    public ObservableCollection<CameraViewModel> Cameras { get; }

    public ObservableCollection<CameraProviderDescriptor> ProviderOptions { get; }

    public ObservableCollection<CameraDeviceInfo> DeviceOptions { get; }

    public IReadOnlyList<CameraTriggerMode> TriggerModes { get; } = Enum.GetValues<CameraTriggerMode>();

    public IReadOnlyList<CameraWatermarkPositionOption> WatermarkPositions { get; } =
    [
        new(CameraImageWatermarkPosition.TopLeft, "左上"),
        new(CameraImageWatermarkPosition.TopRight, "右上"),
        new(CameraImageWatermarkPosition.BottomLeft, "左下"),
        new(CameraImageWatermarkPosition.BottomRight, "右下")
    ];

    public int ConnectedCount => Cameras.Count(camera => camera.IsConnected);

    public string SelectedProviderId
    {
        get => SelectedCamera.ProviderId;
        set
        {
            var providerId = string.IsNullOrWhiteSpace(value) ? CameraProviderIds.HikRobot : value.Trim();
            if (string.Equals(SelectedCamera.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedCamera.ProviderId = providerId;
            SelectedCamera.DeviceId = string.Empty;
            SelectedCamera.DeviceDisplayName = string.Empty;
            RefreshSavedDeviceOption();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDeviceId));
        }
    }

    public string SelectedDeviceId
    {
        get => SelectedCamera.DeviceId;
        set
        {
            if (_refreshingDeviceOptions)
            {
                return;
            }

            SetSelectedDeviceId(value);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedCount))]
    private CameraViewModel selectedCamera;

    [ObservableProperty]
    private string operationMessage = "选择相机并配置";

    partial void OnSelectedCameraChanged(CameraViewModel value)
    {
        // 反向同步到顶部公共参数
        if (!ReferenceEquals(_inspectionContext.SelectedCamera, value))
        {
            _inspectionContext.SelectedCamera = value;
        }
        OperationMessage = value.ConnectionStateText;
        RefreshSavedDeviceOption();
        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(SelectedDeviceId));
    }

    [RelayCommand]
    private void SelectCamera(CameraViewModel? camera)
    {
        if (camera != null)
        {
            SelectedCamera = camera;
        }
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.IsConnected))
        {
            OnPropertyChanged(nameof(ConnectedCount));
            if (ReferenceEquals(sender, SelectedCamera))
            {
                OperationMessage = SelectedCamera.ConnectionStateText;
            }
        }

        if (ReferenceEquals(sender, SelectedCamera) &&
            e.PropertyName is nameof(CameraViewModel.DeviceId) or nameof(CameraViewModel.ProviderId))
        {
            OnPropertyChanged(nameof(SelectedProviderId));
            OnPropertyChanged(nameof(SelectedDeviceId));
        }
    }

    [RelayCommand]
    private async Task EnumerateDevicesAsync()
    {
        var camera = SelectedCamera;
        camera.IsBusy = true;
        try
        {
            AppDiagnostics.Info("camera-settings", $"Enumerate devices requested. Camera={camera.Name}, Provider={camera.ProviderId}");
            var devices = await _cameraService.EnumerateDevicesAsync(camera.ProviderId);
            ReplaceDeviceOptions(camera, devices);

            if (devices.Count == 1 && string.IsNullOrWhiteSpace(camera.DeviceId))
            {
                SetSelectedDeviceId(devices[0].DeviceId);
            }
            else
            {
                UpdateSelectedDeviceDisplayName();
            }

            OperationMessage = devices.Count == 0
                ? $"{camera.ProviderId} 未返回设备"
                : $"发现 {devices.Count} 个设备";
            AppDiagnostics.Info("camera-settings", $"Enumerate devices completed. Camera={camera.Name}, Provider={camera.ProviderId}, Count={devices.Count}, SelectedDeviceId={camera.DeviceId}");
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
            AppDiagnostics.Error("camera-settings", $"Enumerate devices failed. Camera={camera.Name}, Provider={camera.ProviderId}", ex);
        }
        finally
        {
            camera.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectSelectedCameraAsync()
    {
        var camera = SelectedCamera;
        if (!CanAcquire(camera))
        {
            return;
        }

        camera.IsBusy = true;
        try
        {
            AppDiagnostics.Info("camera-settings", $"Connect requested. Camera={camera.Name}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}");
            var result = await _cameraService.TryConnectAsync(camera);
            if (!result.Success)
            {
                camera.IsConnected = false;
                OperationMessage = result.Message;
                if (result.Exception != null)
                {
                    AppDiagnostics.Error("camera-settings", $"Connect failed. Camera={camera.Name}", result.Exception);
                }

                return;
            }

            camera.IsConnected = true;
            camera.LastFrameInfo = "已连接";
            await RefreshExposureSettingsCoreAsync(camera, updateOperationMessage: false);
            OperationMessage = camera.ExposureReadbackText == "未读取"
                ? $"{camera.Name} 已连接"
                : $"{camera.Name} 已连接，{camera.ExposureReadbackText}";
            AppDiagnostics.Info("camera-settings", $"Connect completed. Camera={camera.Name}");
        }
        catch (Exception ex)
        {
            camera.IsConnected = false;
            OperationMessage = CameraErrorFormatter.ToUserMessage(ex);
            AppDiagnostics.Error("camera-settings", $"Connect failed. Camera={camera.Name}", ex);
        }
        finally
        {
            camera.IsBusy = false;
            OnPropertyChanged(nameof(ConnectedCount));
        }
    }

    [RelayCommand]
    private async Task ReadExposureSettingsAsync()
    {
        var camera = SelectedCamera;
        if (!CanAcquire(camera))
        {
            return;
        }

        camera.IsBusy = true;
        try
        {
            await RefreshExposureSettingsCoreAsync(camera, updateOperationMessage: true);
        }
        finally
        {
            camera.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CaptureSelectedCameraAsync()
    {
        var camera = SelectedCamera;
        if (!CanAcquire(camera))
        {
            return;
        }

        camera.IsBusy = true;
        try
        {
            AppDiagnostics.Info("camera-settings", $"Capture requested. Camera={camera.Name}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}");
            using var result = await _cameraService.CaptureOnceAsync(camera);
            camera.SetInspectionSourceFromCapture(result.Image, result.Frame, result.DisplayName, result.ReportedFps);
            camera.IsConnected = false;
            OperationMessage = $"{camera.Name} 取图完成";
            AppDiagnostics.Info("camera-settings", $"Capture completed. Camera={camera.Name}, Display={result.DisplayName}");
        }
        catch (Exception ex)
        {
            camera.IsConnected = false;
            OperationMessage = CameraErrorFormatter.ToUserMessage(ex);
            AppDiagnostics.Error("camera-settings", $"Capture failed. Camera={camera.Name}", ex);
        }
        finally
        {
            camera.IsBusy = false;
            OnPropertyChanged(nameof(ConnectedCount));
        }
    }

    [RelayCommand]
    private void DisconnectSelectedCamera()
    {
        var camera = SelectedCamera;
        AppDiagnostics.Info("camera-settings", $"Disconnect requested. Camera={camera.Name}");
        _cameraService.Disconnect(camera);
        camera.IsConnected = false;
        camera.ClearInspectionSource();
        camera.LastFrameInfo = "已断开";
        OperationMessage = $"{camera.Name} 已断开";
        OnPropertyChanged(nameof(ConnectedCount));
    }

    [RelayCommand]
    private void SaveCameraSettings()
    {
        try
        {
            AppDiagnostics.Info("camera-settings", $"Save camera settings requested. Path={_cameraConfigPath}");
            var settings = new CameraSettings
            {
                Cameras = Cameras.Select(camera => camera.ToProfile()).ToList(),
                SelectedCameraId = SelectedCamera.ConfigurationId
            };
            CameraSettingsStorage.Save(_cameraConfigPath, settings);
            OperationMessage = "相机配置已保存";
            AppDiagnostics.Info("camera-settings", $"Save camera settings completed. Count={settings.Cameras.Count}, Selected={settings.SelectedCameraId}, Cameras={FormatCameraSettingsForLog(settings.Cameras)}");
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
            AppDiagnostics.Error("camera-settings", $"Save camera settings failed. Path={_cameraConfigPath}", ex);
        }
    }

    [RelayCommand]
    private async Task ConnectAllCamerasAsync()
    {
        var enabledCameras = Cameras.Where(c => c.IsEnabled).ToList();
        if (enabledCameras.Count == 0)
        {
            OperationMessage = "没有已启用的相机";
            return;
        }

        OperationMessage = $"正在连接 {enabledCameras.Count} 个相机...";
        var successCount = 0;
        var failCount = 0;

        var tasks = enabledCameras.Select(async camera =>
        {
            try
            {
                if (!camera.IsAcquisitionConfigured)
                {
                    Interlocked.Increment(ref failCount);
                    return;
                }

                var result = await _cameraService.TryConnectAsync(camera);
                if (result.Success)
                {
                    camera.IsConnected = true;
                    camera.LastFrameInfo = "已连接";
                    Interlocked.Increment(ref successCount);
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                }
            }
            catch
            {
                Interlocked.Increment(ref failCount);
            }
        });

        await Task.WhenAll(tasks);

        OperationMessage = $"已连接 {successCount}/{enabledCameras.Count} 个相机，失败 {failCount} 个";
        OnPropertyChanged(nameof(ConnectedCount));
    }

    [RelayCommand]
    private async Task DisconnectAllCamerasAsync()
    {
        var connectedCameras = Cameras.Where(c => c.IsConnected).ToList();
        if (connectedCameras.Count == 0)
        {
            OperationMessage = "没有已连接的相机";
            return;
        }

        foreach (var camera in connectedCameras)
        {
            _cameraService.Disconnect(camera);
            camera.IsConnected = false;
            camera.ClearInspectionSource();
            camera.LastFrameInfo = "已断开";
        }

        OperationMessage = $"已断开 {connectedCameras.Count} 个相机";
        OnPropertyChanged(nameof(ConnectedCount));
    }

    [RelayCommand]
    private async Task CaptureAllCamerasAsync()
    {
        var connectedCameras = Cameras.Where(c => c.IsConnected && c.IsAcquisitionConfigured).ToList();
        if (connectedCameras.Count == 0)
        {
            OperationMessage = "没有已连接的相机";
            return;
        }

        OperationMessage = $"正在拍摄 {connectedCameras.Count} 个相机...";
        var successCount = 0;
        var failCount = 0;

        var tasks = connectedCameras.Select(async camera =>
        {
            try
            {
                using var result = await _cameraService.CaptureOnceAsync(camera);
                camera.SetInspectionSourceFromCapture(result.Image, result.Frame, result.DisplayName, result.ReportedFps);
                camera.IsConnected = false;
                Interlocked.Increment(ref successCount);
            }
            catch
            {
                Interlocked.Increment(ref failCount);
            }
        });

        await Task.WhenAll(tasks);

        OperationMessage = $"已拍摄 {successCount}/{connectedCameras.Count} 个相机，失败 {failCount} 个";
        OnPropertyChanged(nameof(ConnectedCount));
    }

    [RelayCommand]
    private void ChooseOriginalImageWatermarkColor()
    {
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true
        };
        if (TryToDrawingColor(SelectedCamera.OriginalImageWatermarkColor, out var color))
        {
            dialog.Color = color;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SelectedCamera.OriginalImageWatermarkColor = ToRgbHex(dialog.Color);
            OperationMessage = $"已选择水印颜色：{SelectedCamera.OriginalImageWatermarkColor}";
        }
    }

    private bool CanAcquire(CameraViewModel camera)
    {
        if (!camera.IsEnabled)
        {
            OperationMessage = $"{camera.Name} 未启用";
            return false;
        }

        if (!camera.IsAcquisitionConfigured)
        {
            OperationMessage = $"{camera.Name} 未选择设备";
            return false;
        }

        return true;
    }

    private async Task RefreshExposureSettingsCoreAsync(CameraViewModel camera, bool updateOperationMessage)
    {
        try
        {
            var settings = await _cameraService.ReadExposureSettingsAsync(camera);
            camera.ApplyExposureSettings(settings);
            if (updateOperationMessage)
            {
                OperationMessage = settings.IsSupported
                    ? $"已读取 {camera.Name} 曝光参数：{camera.ExposureReadbackText}"
                    : settings.Message;
            }

            AppDiagnostics.Info(
                "camera-settings",
                $"Exposure settings read. Camera={camera.Name}, Supported={settings.IsSupported}, Auto={settings.AutoExposure?.ToString() ?? "unknown"}, ExposureUs={settings.ExposureTimeUs?.ToString("0.##") ?? "unknown"}, RangeUs={settings.MinimumExposureTimeUs?.ToString("0.##") ?? "unknown"}-{settings.MaximumExposureTimeUs?.ToString("0.##") ?? "unknown"}");
        }
        catch (Exception ex)
        {
            camera.ExposureReadbackText = $"读取失败：{CameraErrorFormatter.ToUserMessage(ex)}";
            camera.ExposureRangeText = "--";
            if (updateOperationMessage)
            {
                OperationMessage = camera.ExposureReadbackText;
            }

            AppDiagnostics.Error("camera-settings", $"Read exposure settings failed. Camera={camera.Name}", ex);
        }
    }

    private void RefreshSavedDeviceOption()
    {
        ReplaceDeviceOptions(SelectedCamera, Array.Empty<CameraDeviceInfo>());
    }

    private void ReplaceDeviceOptions(CameraViewModel camera, IReadOnlyCollection<CameraDeviceInfo> devices)
    {
        _refreshingDeviceOptions = true;
        try
        {
            DeviceOptions.Clear();
            foreach (var device in devices)
            {
                DeviceOptions.Add(device);
            }

            EnsureDeviceOption(camera);
        }
        finally
        {
            _refreshingDeviceOptions = false;
            OnPropertyChanged(nameof(SelectedDeviceId));
        }
    }

    private void EnsureDeviceOption(CameraViewModel camera)
    {
        if (string.IsNullOrWhiteSpace(camera.DeviceId) ||
            DeviceOptions.Any(device =>
                string.Equals(device.ProviderId, camera.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                MatchesDeviceIdentifier(device, camera.DeviceId)))
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(camera.DeviceDisplayName)
            ? camera.DeviceId
            : camera.DeviceDisplayName;
        DeviceOptions.Add(new CameraDeviceInfo(camera.ProviderId, camera.DeviceId, displayName));
    }

    private void UpdateSelectedDeviceDisplayName()
    {
        var selectedDevice = DeviceOptions.FirstOrDefault(device =>
            string.Equals(device.ProviderId, SelectedCamera.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            MatchesDeviceIdentifier(device, SelectedCamera.DeviceId));
        if (selectedDevice != null)
        {
            SelectedCamera.DeviceDisplayName = selectedDevice.DisplayName;
            if (!string.Equals(SelectedCamera.DeviceId, selectedDevice.DeviceId, StringComparison.Ordinal))
            {
                SelectedCamera.DeviceId = selectedDevice.DeviceId;
                OnPropertyChanged(nameof(SelectedDeviceId));
                AppDiagnostics.Info("camera-settings", $"Migrated selected device binding to enumerated camera name. Camera={SelectedCamera.Name}, Slot={SelectedCamera.ConfigurationId}, DeviceId={SelectedCamera.DeviceId}, Display={selectedDevice.DisplayName}");
            }
        }
    }

    private static bool MatchesDeviceIdentifier(CameraDeviceInfo device, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var value = expected.Trim();
        return string.Equals(device.DeviceId, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.SerialNumber, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.UserDefinedName, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.DisplayName, value, StringComparison.OrdinalIgnoreCase);
    }

    private void SetSelectedDeviceId(string? value)
    {
        var deviceId = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (!string.Equals(SelectedCamera.DeviceId, deviceId, StringComparison.Ordinal))
        {
            SelectedCamera.DeviceId = deviceId;
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            SelectedCamera.DeviceDisplayName = string.Empty;
        }
        else
        {
            UpdateSelectedDeviceDisplayName();
        }

        OnPropertyChanged(nameof(SelectedDeviceId));
        AppDiagnostics.Info("camera-settings", $"Selected device changed. Camera={SelectedCamera.Name}, Slot={SelectedCamera.ConfigurationId}, Provider={SelectedCamera.ProviderId}, DeviceId={SelectedCamera.DeviceId}");
    }

    private static string FormatCameraSettingsForLog(IEnumerable<CameraProfile> cameras)
    {
        return string.Join(
            "; ",
            cameras.Select(camera =>
                $"{camera.Id}:{camera.Name},Provider={camera.ProviderId},DeviceId={camera.DeviceId},Index={camera.CameraIndex},Enabled={camera.Enabled},AutoExposure={camera.AutoExposure},ExposureUs={camera.ExposureTimeUs:0.##}"));
    }

    private static bool TryToDrawingColor(string value, out System.Drawing.Color color)
    {
        color = System.Drawing.Color.White;
        try
        {
            var mediaColor = (Color)ColorConverter.ConvertFromString(value);
            color = System.Drawing.Color.FromArgb(mediaColor.A, mediaColor.R, mediaColor.G, mediaColor.B);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToRgbHex(System.Drawing.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

public sealed record CameraWatermarkPositionOption(
    CameraImageWatermarkPosition Value,
    string DisplayName);
