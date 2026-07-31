using System.Windows.Media;
using OpenCvSharp;
using VideoInferenceDemo;

namespace VisionWorkbench.Services;

public sealed record CameraCaptureResult(
    ImageSource Image,
    CameraFrameMetadata Metadata,
    string DisplayName,
    double ReportedFps,
    Mat Frame) : IDisposable
{
    public void Dispose()
    {
        Frame.Dispose();
    }
}

public sealed record CameraConnectResult(
    bool Success,
    string Message,
    Exception? Exception = null)
{
    public static CameraConnectResult Ok(string message) => new(true, message);

    public static CameraConnectResult Fail(Exception exception) =>
        new(false, CameraErrorFormatter.ToUserMessage(exception), exception);
}
