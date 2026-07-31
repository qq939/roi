using System.Windows.Media;
using OpenCvSharp;
using VideoInferenceDemo;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public sealed class CameraAcquisitionService : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CameraProviderRegistry _registry = WindowsCameraProviderRegistry.CreateDefault();
    private readonly Dictionary<int, ICameraSession> _sessions = new();

    public IReadOnlyList<CameraProviderDescriptor> GetProviders() => _registry.DescribeProviders();

    public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateDevicesAsync(string providerId)
    {
        return Task.Run(() =>
        {
            AppDiagnostics.Info("camera-acquisition", $"Enumerate devices. Provider={providerId}");
            var devices = _registry.EnumerateDevices(providerId);
            AppDiagnostics.Info("camera-acquisition", $"Enumerated {devices.Count} device(s). Provider={providerId}");
            return devices;
        });
    }

    public Task<CameraExposureSettings> ReadExposureSettingsAsync(
        CameraViewModel camera,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(camera.Index, out var session))
                {
                    return ReadExposureSettings(session);
                }

                AppDiagnostics.Info(
                    "camera-acquisition",
                    $"Read exposure settings with a temporary session. Camera={camera.Name}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}");
                using var temporarySession = _registry.Open(camera.BuildOpenOptions(configureDevice: false));
                return ReadExposureSettings(temporarySession);
            }
        }, cancellationToken);
    }

    public async Task ConnectAsync(CameraViewModel camera, CancellationToken cancellationToken = default)
    {
        var result = await TryConnectAsync(camera, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message, result.Exception);
        }
    }

    public Task<CameraConnectResult> TryConnectAsync(CameraViewModel camera, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lock (_syncRoot)
                {
                    AppDiagnostics.Info(
                        "camera-acquisition",
                        $"Connect {camera.Name}. Index={camera.Index}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}, OpenCvSource={camera.OpenCvSource}, Trigger={camera.TriggerMode}");
                    DisconnectCore(camera.Index);
                    var session = _registry.Open(camera.BuildOpenOptions());
                    _sessions[camera.Index] = session;
                    AppDiagnostics.Info(
                        "camera-acquisition",
                        $"Connected {camera.Name}. Session={session.DisplayName}, SourceId={session.SourceId}");
                    return CameraConnectResult.Ok($"Connected {camera.Name}. Session={session.DisplayName}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Info(
                    "camera-acquisition",
                    $"Connect failed {camera.Name}. {CameraErrorFormatter.ToUserMessage(ex)}");
                return CameraConnectResult.Fail(ex);
            }
        }, cancellationToken);
    }

    public Task<CameraCaptureResult> CaptureAsync(CameraViewModel camera, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ICameraSession session;
            lock (_syncRoot)
            {
                if (!_sessions.TryGetValue(camera.Index, out session!))
                {
                    throw new InvalidOperationException(
                        $"{camera.Name} has no active camera session. Connect configured cameras before capture.");
                }
            }

            AppDiagnostics.Info("camera-acquisition", $"Capture {camera.Name}. Session={session.DisplayName}");
            using var frame = new Mat();
            if (!session.TryCapture(frame, cancellationToken, out var metadata))
            {
                throw new InvalidOperationException("Camera capture returned no frame.");
            }

            ImageSource image = MatImageSourceConverter.CreateImageSource(frame);
            AppDiagnostics.Info(
                "camera-acquisition",
                $"Captured {camera.Name}. Size={frame.Width}x{frame.Height}, Pts={metadata.PtsMs}, Source={metadata.PtsSource}");
            return new CameraCaptureResult(image, metadata, session.DisplayName, session.ReportedFps, frame.Clone());
        }, cancellationToken);
    }

    public Task<CameraCaptureResult> CaptureOnceAsync(CameraViewModel camera, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ICameraSession session;
            lock (_syncRoot)
            {
                AppDiagnostics.Info(
                    "camera-acquisition",
                    $"Capture once {camera.Name}. Index={camera.Index}, Provider={camera.ProviderId}, DeviceId={camera.DeviceId}, OpenCvSource={camera.OpenCvSource}, Trigger={camera.TriggerMode}");
                DisconnectCore(camera.Index);
                session = _registry.Open(camera.BuildOpenOptions());
            }

            try
            {
                using var frame = new Mat();
                if (!session.TryCapture(frame, cancellationToken, out var metadata))
                {
                    throw new InvalidOperationException("Camera capture returned no frame.");
                }

                ImageSource image = MatImageSourceConverter.CreateImageSource(frame);
                AppDiagnostics.Info(
                    "camera-acquisition",
                    $"Capture once completed {camera.Name}. Session={session.DisplayName}, Size={frame.Width}x{frame.Height}");
                return new CameraCaptureResult(image, metadata, session.DisplayName, session.ReportedFps, frame.Clone());
            }
            finally
            {
                session.Dispose();
            }
        }, cancellationToken);
    }

    public void Disconnect(CameraViewModel camera)
    {
        lock (_syncRoot)
        {
            AppDiagnostics.Info("camera-acquisition", $"Disconnect {camera.Name}. Index={camera.Index}");
            DisconnectCore(camera.Index);
        }
    }

    public void DisconnectAll()
    {
        lock (_syncRoot)
        {
            AppDiagnostics.Info("camera-acquisition", $"Disconnect all camera sessions. Count={_sessions.Count}");
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }
    }

    private void DisconnectCore(int cameraIndex)
    {
        if (!_sessions.Remove(cameraIndex, out var session))
        {
            return;
        }

        session.Dispose();
    }

    private static CameraExposureSettings ReadExposureSettings(ICameraSession session)
    {
        return session is ICameraExposureSettingsSource source
            ? source.ReadExposureSettings()
            : CameraExposureSettings.Unsupported($"{session.DisplayName} does not expose exposure settings.");
    }

    public void Dispose()
    {
        DisconnectAll();
    }
}
