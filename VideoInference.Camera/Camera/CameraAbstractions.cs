using System;
using System.Collections.Generic;
using System.Threading;
using OpenCvSharp;

namespace VideoInferenceDemo;

public static class CameraProviderIds
{
    public const string OpenCv = "opencv";
    public const string HikRobot = "hikrobot";
    public const string Uvc = "uvc";
}

public enum CameraTriggerMode
{
    Software,
    Continuous,
    HardwareLine0
}

public enum CameraRotation
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}

public enum CameraMirrorMode
{
    None,
    Horizontal,
    Vertical,
    Both
}

public static class CameraOptionHelpers
{
    public static bool UsesDeviceIdentifier(string? providerId, string? deviceId)
    {
        return !string.Equals(providerId, CameraProviderIds.OpenCv, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(deviceId);
    }

    public static bool UsesOpenCvSource(string? providerId, string? source)
    {
        return string.Equals(providerId, CameraProviderIds.OpenCv, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(source);
    }

    public static string GetSelector(CameraOpenOptions? options)
    {
        var normalized = (options ?? new CameraOpenOptions(CameraProviderIds.OpenCv, 0, null, 0)).Normalize();
        if (UsesOpenCvSource(normalized.ProviderId, normalized.OpenCvSource))
        {
            return normalized.OpenCvSource!;
        }

        return UsesDeviceIdentifier(normalized.ProviderId, normalized.DeviceId)
            ? normalized.DeviceId!
            : normalized.CameraIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed record CameraProviderDescriptor(string ProviderId, string DisplayName);

public sealed record CameraOpenOptions(
    string ProviderId,
    int CameraIndex,
    string? DeviceId,
    double TargetFps,
    string? OpenCvSource = null,
    string? OpenCvBackend = null,
    CameraTriggerMode TriggerMode = CameraTriggerMode.Software,
    CameraRotation Rotation = CameraRotation.None,
    CameraMirrorMode MirrorMode = CameraMirrorMode.None)
{
    public CameraOpenOptions Normalize()
    {
        var providerId = string.IsNullOrWhiteSpace(ProviderId) ? CameraProviderIds.OpenCv : ProviderId.Trim();
        var deviceId = string.IsNullOrWhiteSpace(DeviceId) ? null : DeviceId.Trim();
        var openCvSource = string.IsNullOrWhiteSpace(OpenCvSource) ? null : OpenCvSource.Trim();
        var openCvBackend = string.IsNullOrWhiteSpace(OpenCvBackend) ? null : OpenCvBackend.Trim();
        return this with
        {
            ProviderId = providerId,
            CameraIndex = Math.Max(0, CameraIndex),
            DeviceId = deviceId,
            OpenCvSource = openCvSource,
            OpenCvBackend = openCvBackend,
            TriggerMode = Enum.IsDefined(TriggerMode) ? TriggerMode : CameraTriggerMode.Software,
            Rotation = Enum.IsDefined(Rotation) ? Rotation : CameraRotation.None,
            MirrorMode = Enum.IsDefined(MirrorMode) ? MirrorMode : CameraMirrorMode.None
        };
    }
}

public sealed record CameraDeviceInfo(
    string ProviderId,
    string DeviceId,
    string DisplayName,
    string? SerialNumber = null,
    string? UserDefinedName = null,
    string? ModelName = null);

public enum CameraPtsSource
{
    DeviceTimestamp,
    HostTimestamp,
    MonotonicClockFallback
}

public sealed record CameraFrameMetadata(
    long PtsMs,
    long CaptureUtcMs,
    CameraPtsSource PtsSource,
    long? RawDeviceTimestamp = null,
    long? RawHostTimestamp = null,
    uint? FrameNumber = null);

public sealed class CameraFrameArrivedEventArgs : EventArgs
{
    public CameraFrameArrivedEventArgs(
        string sourceId,
        string displayName,
        CameraFrameMetadata metadata)
    {
        SourceId = sourceId;
        DisplayName = displayName;
        Metadata = metadata;
    }

    public string SourceId { get; }

    public string DisplayName { get; }

    public CameraFrameMetadata Metadata { get; }
}

public interface ICameraFrameCallbackSource
{
    event EventHandler<CameraFrameArrivedEventArgs>? FrameArrived;

    void ClearPendingFrames();
}

public interface ICameraProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    IReadOnlyList<CameraDeviceInfo> EnumerateDevices();
    ICameraSession Open(CameraOpenOptions options);
}

public interface ICameraSession : IDisposable
{
    string SourceId { get; }
    string DisplayName { get; }
    double ReportedFps { get; }
    CameraTriggerMode TriggerMode { get; }
    bool TryRead(Mat destination, CancellationToken cancellationToken, out CameraFrameMetadata metadata);
    bool TryTrigger(CancellationToken cancellationToken);

    bool TryCapture(Mat destination, CancellationToken cancellationToken, out CameraFrameMetadata metadata)
    {
        metadata = default!;
        return (TriggerMode != CameraTriggerMode.Software || TryTrigger(cancellationToken)) &&
               TryRead(destination, cancellationToken, out metadata);
    }
}
