using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;
using VisionWorkbench.Services;

namespace VisionWorkbench.ViewModels;

public sealed partial class HomeWorkspaceViewModel : ObservableObject
{
    private readonly RuntimeInspectionContext _inspectionContext;

    public HomeWorkspaceViewModel(
        ObservableCollection<CameraViewModel> cameras,
        ObservableCollection<InspectionResultRow> results,
        ObservableCollection<LogEntry> logs,
        CameraViewModel selectedCamera,
        BarcodeScannerSerialPortService barcodeScanner,
        Mt3aModbusTcpIoClient ioModule,
        RuntimeInspectionContext? inspectionContext = null)
    {
        Cameras = cameras;
        Results = results;
        Logs = logs;
        BarcodeScanner = barcodeScanner;
        IoModule = ioModule;
        _inspectionContext = inspectionContext ?? new RuntimeInspectionContext();
        Logs.CollectionChanged += OnLogsChanged;
        Results.CollectionChanged += OnResultsChanged;
        IoModule.InputsChanged += OnIoInputsChanged;
        OverviewWorkspace = new CameraOverviewViewModel(cameras);
        DetailWorkspace = new CameraDetailViewModel(selectedCamera);
        this.selectedCamera = selectedCamera;
        currentImageWorkspace = OverviewWorkspace;
        isOverviewMode = true;

        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;
    }

    public RuntimeInspectionContext InspectionContext => _inspectionContext;

    public ObservableCollection<CameraViewModel> Cameras { get; }

    public ObservableCollection<InspectionResultRow> Results { get; }

    public ObservableCollection<LogEntry> Logs { get; }

    public BarcodeScannerSerialPortService BarcodeScanner { get; }

    public Mt3aModbusTcpIoClient IoModule { get; }

    public CameraOverviewViewModel OverviewWorkspace { get; }

    public CameraDetailViewModel DetailWorkspace { get; }

    public int OkCount => Results.Count(result => result.Result == "OK");

    public int NgCount => Results.Count(result => result.Result != "OK");

    public string TotalResultText
    {
        get
        {
            if (Results.Count == 0)
            {
                return "待检测";
            }

            return Results.Any(result => !string.Equals(result.Result, "OK", StringComparison.OrdinalIgnoreCase))
                ? "NG"
                : "OK";
        }
    }

    public Brush TotalResultBackground => TotalResultText switch
    {
        "OK" => UiBrushes.Success,
        "NG" => UiBrushes.Danger,
        _ => UiBrushes.TextMuted
    };

    public string EventText => string.Join(Environment.NewLine, Logs.Select(log => log.Text));

    [ObservableProperty]
    private object currentImageWorkspace;

    [ObservableProperty]
    private CameraViewModel selectedCamera;

    [ObservableProperty]
    private bool isOverviewMode;

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

    [ObservableProperty]
    private bool triggerButtonActive;

    [ObservableProperty]
    private bool resetButtonActive;

    private void OnInspectionContextProductCodeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ProductCode));
    }

    private void OnInspectionContextSerialNumberChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SerialNumber));
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        var camera = _inspectionContext.SelectedCamera;
        if (camera != null && !ReferenceEquals(SelectedCamera, camera))
        {
            SelectedCamera = camera;
        }
    }

    partial void OnSelectedCameraChanged(CameraViewModel value)
    {
        AppDiagnostics.Debug("home", $"OnSelectedCameraChanged: {value?.Name ?? "null"}");
        _inspectionContext.SelectedCamera = value;
        DetailWorkspace.SelectedCamera = value;
    }

    public void ShowOverview()
    {
        IsOverviewMode = true;
        CurrentImageWorkspace = OverviewWorkspace;
    }

    public void ShowCamera(CameraViewModel camera)
    {
        SelectedCamera = camera;
        IsOverviewMode = false;
        CurrentImageWorkspace = DetailWorkspace;
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(NgCount));
        OnPropertyChanged(nameof(TotalResultText));
        OnPropertyChanged(nameof(TotalResultBackground));
    }

    public void ResetInspectionState()
    {
        if (Results.Count > 0)
        {
            Results.Clear();
            return;
        }

        RefreshSummary();
    }

    public void ResetWorkpieceState()
    {
        ProductCode = string.Empty;
        SerialNumber = string.Empty;
        ResetInspectionState();
    }

    public event Action? SnScanned;

    public void ApplyScannedBarcode(string barcode)
    {
        var value = barcode.Trim();
        AppDiagnostics.Debug("scanner", $"[APPLY_BARCODE] Raw='{barcode}', Trimmed='{value}', Length={value.Length}");
        if (string.IsNullOrWhiteSpace(value))
        {
            AppDiagnostics.Debug("scanner", $"[APPLY_BARCODE] Empty barcode, ignoring");
            return;
        }

        if (BarcodeScannerSerialPortService.IsProductCodeBarcode(value))
        {
            AppDiagnostics.Debug("scanner", $"[APPLY_BARCODE] Setting ProductCode='{value}'");
            ProductCode = value;
        }
        else
        {
            AppDiagnostics.Debug("scanner", $"[APPLY_BARCODE] Setting SerialNumber='{value}'");
            SerialNumber = value;
            // 扫码枪扫描 SN 号码后，触发启动相机事件
            SnScanned?.Invoke();
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EventText));
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSummary();
    }

    private void OnIoInputsChanged(object? sender, IoInputsChangedEventArgs e)
    {
        var resetInput = e.Inputs.Length > 1 && e.Inputs[1];
        TriggerButtonActive = e.Inputs.Length > 0 && e.Inputs[0];
        if (resetInput && !ResetButtonActive)
        {
            ResetWorkpieceState();
        }

        ResetButtonActive = resetInput;
    }
}
