using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.ViewModels;

public sealed partial class SecondaryBoardViewModel : ObservableObject, IDisposable
{
    private readonly InspectionWorkspaceConfiguration _configuration;
    private readonly InspectionConfigurationStorage _storage;
    private readonly DispatcherTimer _saveTimer;

    public SecondaryBoardViewModel(
        InspectionWorkspaceConfiguration configuration,
        HomeWorkspaceViewModel homeWorkspace,
        InspectionConfigurationStorage storage)
    {
        _configuration = configuration;
        HomeWorkspace = homeWorkspace;
        _storage = storage;
        _configuration.SecondaryBoard ??= new SecondaryBoardSettings();
        _configuration.SecondaryBoard.Normalize();
        isEnabled = _configuration.SecondaryBoard.Enabled;
        layout = _configuration.SecondaryBoard.Layout;
        backgroundColor = _configuration.SecondaryBoard.BackgroundColor;
        Cameras = new ObservableCollection<SecondaryBoardCameraViewModel>(
            homeWorkspace.Cameras.Select(camera => new SecondaryBoardCameraViewModel(
                camera,
                GetOrCreateViewport(camera),
                QueueSave)));
        HomeWorkspace.PropertyChanged += HomeWorkspace_PropertyChanged;
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _saveTimer.Tick += SaveTimer_Tick;
    }

    public HomeWorkspaceViewModel HomeWorkspace { get; }

    public ObservableCollection<SecondaryBoardCameraViewModel> Cameras { get; }

    public IReadOnlyList<string> LayoutOptions { get; } =
    [
        SecondaryBoardSettings.LayoutThreeByTwo,
        SecondaryBoardSettings.LayoutTwoByThree
    ];

    public int Columns => string.Equals(Layout, SecondaryBoardSettings.LayoutTwoByThree, StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    public int Rows => string.Equals(Layout, SecondaryBoardSettings.LayoutTwoByThree, StringComparison.OrdinalIgnoreCase) ? 3 : 2;

    public Brush BackgroundBrush => CreateBrush(BackgroundColor);

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns))]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private string layout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private string backgroundColor;

    partial void OnIsEnabledChanged(bool value)
    {
        _configuration.SecondaryBoard.Enabled = value;
        SaveNow();
    }

    partial void OnLayoutChanged(string value)
    {
        var normalized = SecondaryBoardSettings.IsSupportedLayout(value)
            ? value
            : SecondaryBoardSettings.LayoutThreeByTwo;
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            Layout = normalized;
            return;
        }

        _configuration.SecondaryBoard.Layout = normalized;
        SaveNow();
    }

    partial void OnBackgroundColorChanged(string value)
    {
        if (!SecondaryBoardSettings.TryNormalizeColor(value, out var normalized))
        {
            normalized = SecondaryBoardSettings.DefaultBackgroundColor;
        }

        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            BackgroundColor = normalized;
            return;
        }

        _configuration.SecondaryBoard.BackgroundColor = normalized;
        SaveNow();
    }

    public void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _configuration.SecondaryBoard.Normalize();
            _storage.Save(_configuration);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("secondary-board", "Secondary board settings save failed.", ex);
        }
    }

    public void Dispose()
    {
        HomeWorkspace.PropertyChanged -= HomeWorkspace_PropertyChanged;
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        SaveNow();
    }

    private CameraViewportSettings GetOrCreateViewport(CameraViewModel camera)
    {
        var settings = _configuration.SecondaryBoard;
        var productKey = GetCurrentProductViewportKey();
        if (!settings.ProductCameraViewports.TryGetValue(productKey, out var cameraViewports))
        {
            cameraViewports = new Dictionary<string, CameraViewportSettings>(StringComparer.OrdinalIgnoreCase);
            settings.ProductCameraViewports[productKey] = cameraViewports;
        }

        if (!cameraViewports.TryGetValue(camera.ConfigurationId, out var viewport))
        {
            if (!settings.CameraViewports.TryGetValue(camera.ConfigurationId, out viewport))
            {
                viewport = new CameraViewportSettings();
            }

            cameraViewports[camera.ConfigurationId] = viewport;
        }

        viewport.Normalize();
        return viewport;
    }

    private string GetCurrentProductViewportKey()
    {
        var productCode = HomeWorkspace.ProductCode.Trim();
        return string.IsNullOrWhiteSpace(productCode)
            ? _configuration.SelectedProductModelId
            : productCode;
    }

    private void ReloadProductViewports()
    {
        foreach (var camera in Cameras)
        {
            camera.SetViewport(GetOrCreateViewport(camera.Camera));
        }
    }

    private void HomeWorkspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeWorkspaceViewModel.ProductCode))
        {
            ReloadProductViewports();
        }
    }

    private static Brush CreateBrush(string color)
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

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        SaveNow();
    }
}

public sealed partial class SecondaryBoardCameraViewModel : ObservableObject
{
    private CameraViewportSettings _viewport;
    private readonly Action _queueSave;

    public SecondaryBoardCameraViewModel(
        CameraViewModel camera,
        CameraViewportSettings viewport,
        Action queueSave)
    {
        Camera = camera;
        _viewport = viewport;
        _queueSave = queueSave;
        scale = viewport.Scale;
        offsetX = viewport.OffsetX;
        offsetY = viewport.OffsetY;
    }

    public CameraViewModel Camera { get; }

    [ObservableProperty]
    private double scale;

    [ObservableProperty]
    private double offsetX;

    [ObservableProperty]
    private double offsetY;

    partial void OnScaleChanged(double value)
    {
        _viewport.Scale = value;
        _queueSave();
    }

    partial void OnOffsetXChanged(double value)
    {
        _viewport.OffsetX = value;
        _queueSave();
    }

    partial void OnOffsetYChanged(double value)
    {
        _viewport.OffsetY = value;
        _queueSave();
    }

    public void SetViewport(CameraViewportSettings viewport)
    {
        _viewport = viewport;
        _viewport.Normalize();
        Scale = _viewport.Scale;
        OffsetX = _viewport.OffsetX;
        OffsetY = _viewport.OffsetY;
    }
}
