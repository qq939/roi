using System.Collections.ObjectModel;
using VisionWorkbench.Models;

namespace VisionWorkbench.ViewModels;

public sealed class CameraOverviewViewModel
{
    public CameraOverviewViewModel(ObservableCollection<CameraViewModel> cameras)
    {
        Cameras = cameras;
    }

    public ObservableCollection<CameraViewModel> Cameras { get; }
}
