using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.ViewModels;

public sealed class ClipTrainingTaskItemViewModel
{
    public ClipTrainingTaskItemViewModel(
        InspectionTaskDefinition definition,
        IReadOnlyList<CameraViewModel> cameras)
    {
        Definition = definition;
        var camera = cameras.FirstOrDefault(item =>
            string.Equals(item.ConfigurationId, definition.CameraId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, definition.CameraId, StringComparison.OrdinalIgnoreCase));

        CameraName = camera?.Name ?? definition.CameraId;
        CameraIdCandidates = camera == null
            ? [definition.CameraId]
            : [definition.CameraId, camera.ConfigurationId, camera.Name];
    }

    public InspectionTaskDefinition Definition { get; }

    public string CameraName { get; }

    public IReadOnlyList<string> CameraIdCandidates { get; }

    public string DisplayName => $"{CameraName} / {Definition.Name}";

    public string VectorSetId => Definition.EnsureClipVectorSet().VectorSetId;
}
