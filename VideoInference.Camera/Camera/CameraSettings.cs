using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoInferenceDemo;

public sealed class CameraSettings
{
    public List<CameraProfile> Cameras { get; set; } = new();
    public List<CameraSopProfile> SopProfiles { get; set; } = new();
    public string SelectedCameraId { get; set; } = string.Empty;

    public CameraProfile? ResolveSelectedCamera()
    {
        if (Cameras.Count == 0)
        {
            return null;
        }

        return Cameras.FirstOrDefault(camera => string.Equals(camera.Id, SelectedCameraId, StringComparison.OrdinalIgnoreCase))
               ?? Cameras[0];
    }

    public CameraProfile? ResolvePreferredInteractiveCamera()
    {
        if (Cameras.Count == 0)
        {
            return null;
        }

        var selected = ResolveSelectedCamera();
        if (selected?.Enabled == true)
        {
            return selected;
        }

        return Cameras.FirstOrDefault(camera => camera.Enabled)
               ?? selected
               ?? Cameras[0];
    }

    public CameraProfile? ResolvePreferredVideoCamera()
    {
        return ResolvePreferredInteractiveCamera();
    }

    public IReadOnlyList<CameraProfile> GetAutoStartCameras()
    {
        return Cameras
            .Where(camera => camera.Enabled && camera.AutoStart)
            .ToArray();
    }

    public IReadOnlyList<CameraProfile> GetEnabledCameras()
    {
        return Cameras
            .Where(camera => camera.Enabled)
            .ToArray();
    }

    public static CameraSettings CreateDefault()
    {
        var camera = CameraProfile.CreateDefault(1);
        return new CameraSettings
        {
            Cameras = new List<CameraProfile> { camera },
            SopProfiles = new List<CameraSopProfile>(),
            SelectedCameraId = camera.Id
        };
    }
}
