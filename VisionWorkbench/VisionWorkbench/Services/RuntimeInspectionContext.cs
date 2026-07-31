using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public sealed class RuntimeInspectionContext : ObservableObject
{
    private string _productCode = string.Empty;
    private string _serialNumber = string.Empty;
    private CameraViewModel? _selectedCamera;

    public string ProductCode
    {
        get => _productCode;
        set
        {
            if (SetProperty(ref _productCode, value))
            {
                ProductCodeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string SerialNumber
    {
        get => _serialNumber;
        set
        {
            if (SetProperty(ref _serialNumber, value))
            {
                SerialNumberChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public CameraViewModel? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (SetProperty(ref _selectedCamera, value))
            {
                SelectedCameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? ProductCodeChanged;
    public event EventHandler? SerialNumberChanged;
    public event EventHandler? SelectedCameraChanged;

    public bool HasProductCode => !string.IsNullOrWhiteSpace(ProductCode);
    public bool HasSerialNumber => !string.IsNullOrWhiteSpace(SerialNumber);
    public bool IsReady => HasProductCode && HasSerialNumber;
}
