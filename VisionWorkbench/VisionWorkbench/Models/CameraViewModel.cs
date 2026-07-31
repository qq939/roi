using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageBox;
using OpenCvSharp;
using VideoInferenceDemo;

namespace VisionWorkbench.Models;

public sealed partial class CameraViewModel : ObservableObject, IDisposable
{
    public CameraViewModel(
        int index,
        string name,
        string position,
        string result,
        string cycleTime,
        ImageSource frame,
        IEnumerable<ImageOverlayItem> overlays)
    {
        Index = index;
        this.name = name;
        Position = position;
        Result = result;
        CycleTime = cycleTime;
        Frame = frame;
        Overlays = new ObservableCollection<ImageOverlayItem>(overlays);
        cameraIndex = Math.Max(0, index - 1);
    }

    public int Index { get; }

    public string ConfigurationId => $"camera-{Index:00}";

    public string Position { get; }

    public string Result { get; }

    public string CycleTime { get; }

    public bool HasResult => !string.IsNullOrWhiteSpace(Result);

    public bool HasCycleTime => !string.IsNullOrWhiteSpace(CycleTime);

    public string Resolution => "1280 x 900";

    public ObservableCollection<ImageOverlayItem> Overlays { get; }

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private ImageSource frame;

    [ObservableProperty]
    private string inspectionSourcePath = string.Empty;

    [ObservableProperty]
    private string inspectionSourceDescription = string.Empty;

    private Mat? inspectionSourceMat;

    public void SetInspectionSourceFromLoadedImage(string path, ImageSource displayImage, Mat sourceMat)
    {
        ArgumentNullException.ThrowIfNull(displayImage);
        ArgumentNullException.ThrowIfNull(sourceMat);
        if (sourceMat.Empty())
        {
            throw new ArgumentException("Inspection source image is empty.", nameof(sourceMat));
        }

        Frame = displayImage;
        InspectionSourcePath = Path.GetFullPath(path);
        InspectionSourceDescription = "缓存原始图";
        ReplaceInspectionSource(sourceMat);
    }

    public void SetInspectionSourceFromCapture(
        ImageSource displayImage,
        Mat sourceMat,
        string displayName,
        double reportedFps)
    {
        ArgumentNullException.ThrowIfNull(displayImage);
        ArgumentNullException.ThrowIfNull(sourceMat);
        if (sourceMat.Empty())
        {
            throw new ArgumentException("Inspection source image is empty.", nameof(sourceMat));
        }

        Frame = displayImage;
        InspectionSourcePath = string.Empty;
        InspectionSourceDescription = "相机原始图";
        DeviceDisplayName = displayName;
        LastFrameInfo = reportedFps > 0
            ? $"{DateTime.Now:HH:mm:ss}  {displayImage.Width:0}x{displayImage.Height:0}  {reportedFps:0.##}fps"
            : $"{DateTime.Now:HH:mm:ss}  {displayImage.Width:0}x{displayImage.Height:0}";
        ReplaceInspectionSource(sourceMat);
    }

    public Mat CloneInspectionSourceMat()
    {
        if (inspectionSourceMat == null || inspectionSourceMat.Empty())
        {
            throw new InvalidOperationException("请先读取图片或拍照");
        }

        return inspectionSourceMat.Clone();
    }

    public void ClearInspectionSource()
    {
        InspectionSourcePath = string.Empty;
        InspectionSourceDescription = string.Empty;
        inspectionSourceMat?.Dispose();
        inspectionSourceMat = null;
    }

    public void Dispose()
    {
        ClearInspectionSource();
    }

    private void ReplaceInspectionSource(Mat sourceMat)
    {
        inspectionSourceMat?.Dispose();
        inspectionSourceMat = sourceMat.Clone();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionConfigured))]
    [NotifyPropertyChangedFor(nameof(HasExplicitAcquisitionTarget))]
    private string providerId = CameraProviderIds.HikRobot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionConfigured))]
    private int cameraIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionConfigured))]
    [NotifyPropertyChangedFor(nameof(HasExplicitAcquisitionTarget))]
    private string deviceId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionConfigured))]
    [NotifyPropertyChangedFor(nameof(HasExplicitAcquisitionTarget))]
    private string openCvSource = string.Empty;

    [ObservableProperty]
    private string openCvBackend = "dshow";

    [ObservableProperty]
    private double targetFps = 10;

    [ObservableProperty]
    private CameraTriggerMode triggerMode = CameraTriggerMode.Software;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualExposureEnabled))]
    private bool autoExposure = true;

    [ObservableProperty]
    private double exposureTimeUs = CameraProfile.DefaultExposureTimeUs;

    [ObservableProperty]
    private bool enableOriginalImageFileNameWatermark = true;

    [ObservableProperty]
    private int originalImageWatermarkFontSize = CameraImageWatermarkOptions.DefaultFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalImageWatermarkBrush))]
    private string originalImageWatermarkColor = CameraImageWatermarkOptions.DefaultColor;

    [ObservableProperty]
    private CameraImageWatermarkPosition originalImageWatermarkPosition = CameraImageWatermarkPosition.BottomRight;

    [ObservableProperty]
    private string exposureReadbackText = "未读取";

    [ObservableProperty]
    private string exposureRangeText = "--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateText))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrush))]
    private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateText))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrush))]
    private bool isBusy;

    [ObservableProperty]
    private string lastFrameInfo = "未取图";

    [ObservableProperty]
    private string deviceDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateText))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrush))]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionConfigured))]
    private bool isEnabled;

    public bool HasExplicitAcquisitionTarget =>
        CameraOptionHelpers.UsesDeviceIdentifier(ProviderId, DeviceId) ||
        CameraOptionHelpers.UsesOpenCvSource(ProviderId, OpenCvSource);

    public bool IsAcquisitionConfigured =>
        IsEnabled && HasExplicitAcquisitionTarget;

    public bool IsManualExposureEnabled => !AutoExposure;

    public Brush OriginalImageWatermarkBrush => CreateWatermarkBrush(OriginalImageWatermarkColor);

    public string ConnectionStateText => !IsEnabled ? "停用" : IsBusy ? "处理中" : IsConnected ? "已连接" : "未连接";

    public Brush ResultBrush => Result == "OK"
        ? UiBrushes.Success
        : Result == "NG"
            ? UiBrushes.Danger
            : UiBrushes.TextMuted;

    public Brush ResultBackBrush => Result == "OK"
        ? UiBrushes.SuccessSoft
        : Result == "NG"
            ? UiBrushes.DangerSoft
            : Brushes.Transparent;

    public Brush ConnectionBrush => !IsEnabled ? UiBrushes.TextMuted : IsBusy ? UiBrushes.Warning : IsConnected ? UiBrushes.Success : UiBrushes.TextMuted;

    public CameraOpenOptions BuildOpenOptions(bool configureDevice = true)
    {
        return new CameraOpenOptions(
                ProviderId,
                CameraIndex,
                DeviceId,
                TargetFps,
                OpenCvSource,
                OpenCvBackend,
                TriggerMode,
                AutoExposure: configureDevice ? AutoExposure : null,
                ExposureTimeUs: configureDevice && !AutoExposure ? NormalizeExposureTimeUs(ExposureTimeUs) : null,
                ConfigureDevice: configureDevice)
            .Normalize();
    }

    public void ApplyProfile(CameraProfile profile)
    {
        Name = string.IsNullOrWhiteSpace(profile.Name) ? $"Camera {Index}" : profile.Name.Trim();
        IsEnabled = profile.Enabled;
        ProviderId = profile.ProviderId;
        CameraIndex = profile.CameraIndex;
        DeviceId = profile.DeviceId;
        OpenCvSource = profile.OpenCvSource;
        OpenCvBackend = profile.OpenCvBackend;
        TargetFps = profile.TargetFps;
        TriggerMode = profile.TriggerMode;
        AutoExposure = profile.AutoExposure;
        ExposureTimeUs = profile.ExposureTimeUs;
        EnableOriginalImageFileNameWatermark = profile.EnableOriginalImageFileNameWatermark;
        OriginalImageWatermarkFontSize = profile.OriginalImageWatermarkFontSize;
        OriginalImageWatermarkColor = profile.OriginalImageWatermarkColor;
        OriginalImageWatermarkPosition = profile.OriginalImageWatermarkPosition;
    }

    public CameraProfile ToProfile()
    {
        return new CameraProfile
        {
            Id = ConfigurationId,
            Name = Name,
            Enabled = IsEnabled,
            AutoStart = false,
            ProviderId = ProviderId,
            CameraIndex = CameraIndex,
            DeviceId = DeviceId,
            OpenCvSource = OpenCvSource,
            OpenCvBackend = OpenCvBackend,
            TargetFps = TargetFps,
            TriggerMode = TriggerMode,
            AutoExposure = AutoExposure,
            ExposureTimeUs = NormalizeExposureTimeUs(ExposureTimeUs),
            EnableOriginalImageFileNameWatermark = EnableOriginalImageFileNameWatermark,
            OriginalImageWatermarkFontSize = OriginalImageWatermarkFontSize,
            OriginalImageWatermarkColor = OriginalImageWatermarkColor,
            OriginalImageWatermarkPosition = OriginalImageWatermarkPosition
        }.Normalize(Index);
    }

    public CameraImageWatermarkOptions BuildOriginalImageWatermarkOptions()
    {
        return new CameraImageWatermarkOptions
        {
            Enabled = EnableOriginalImageFileNameWatermark,
            FontSize = OriginalImageWatermarkFontSize,
            Color = OriginalImageWatermarkColor,
            Position = OriginalImageWatermarkPosition
        }.Normalize();
    }

    public void ApplyExposureSettings(CameraExposureSettings settings)
    {
        if (!settings.IsSupported)
        {
            ExposureReadbackText = settings.Message;
            ExposureRangeText = "--";
            return;
        }

        if (settings.AutoExposure.HasValue)
        {
            AutoExposure = settings.AutoExposure.Value;
        }

        if (settings.ExposureTimeUs is > 0)
        {
            ExposureTimeUs = settings.ExposureTimeUs.Value;
        }

        ExposureRangeText = settings.MinimumExposureTimeUs is { } minimum && settings.MaximumExposureTimeUs is { } maximum
            ? $"{minimum:0.##} - {maximum:0.##} us"
            : "设备未提供范围";
        var mode = settings.AutoExposure == true ? "自动" : settings.AutoExposure == false ? "手动" : "模式未知";
        ExposureReadbackText = settings.ExposureTimeUs is { } exposure
            ? $"{mode}，{exposure:0.##} us"
            : mode;
    }

    partial void OnExposureTimeUsChanged(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            ExposureTimeUs = CameraProfile.DefaultExposureTimeUs;
        }
    }

    private static double NormalizeExposureTimeUs(double value) =>
        double.IsFinite(value) && value > 0 ? value : CameraProfile.DefaultExposureTimeUs;

    private static Brush CreateWatermarkBrush(string color)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return UiBrushes.TextMuted;
        }
    }
}
