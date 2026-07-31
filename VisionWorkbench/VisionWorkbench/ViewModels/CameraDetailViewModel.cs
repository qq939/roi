using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;

namespace VisionWorkbench.ViewModels;

public sealed partial class CameraDetailViewModel : ObservableObject
{
    public CameraDetailViewModel(CameraViewModel selectedCamera)
    {
        this.selectedCamera = selectedCamera;
    }

    [ObservableProperty]
    private CameraViewModel selectedCamera;
}
