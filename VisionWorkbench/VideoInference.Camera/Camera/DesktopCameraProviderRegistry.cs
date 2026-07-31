namespace VideoInferenceDemo;

public static class DesktopCameraProviderRegistry
{
    public static CameraProviderRegistry CreateDefault() => WindowsCameraProviderRegistry.CreateDefault();
}

public static class WindowsCameraProviderRegistry
{
    public static CameraProviderRegistry CreateDefault()
    {
        return new CameraProviderRegistry(new[]
        {
            new CameraProviderRegistration(
                CameraProviderIds.OpenCv,
                "OpenCV Webcam",
                static () => new OpenCvCameraProvider()),
            new CameraProviderRegistration(
                CameraProviderIds.Uvc,
                "DirectShow UVC Camera",
                static () => new UvcCameraProvider()),
            new CameraProviderRegistration(
                CameraProviderIds.HikRobot,
                "HikRobot SDK",
                static () => new WindowsHikCameraProvider())
        });
    }
}
