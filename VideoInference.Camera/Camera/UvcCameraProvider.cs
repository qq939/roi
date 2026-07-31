using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Channels;
using DirectShowLib;
using OpenCvSharp;

namespace VideoInferenceDemo;

public sealed class UvcCameraProvider : ICameraProvider
{
    public string ProviderId => CameraProviderIds.Uvc;
    public string DisplayName => "DirectShow UVC Camera";

    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<CameraDeviceInfo>();
        }

        try
        {
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devices == null || devices.Length == 0)
            {
                return Array.Empty<CameraDeviceInfo>();
            }

            var results = new List<CameraDeviceInfo>(devices.Length);
            foreach (var dsDevice in devices)
            {
                if (dsDevice == null)
                    continue;

                try
                {
                    var name = dsDevice.Name ?? string.Empty;
                    var devicePath = dsDevice.DevicePath ?? string.Empty;
                    var deviceId = string.IsNullOrWhiteSpace(devicePath) ? name : devicePath;

                    var serialNumber = ExtractSerialFromPath(devicePath);

                    results.Add(new CameraDeviceInfo(
                        ProviderId: CameraProviderIds.Uvc,
                        DeviceId: deviceId,
                        DisplayName: string.IsNullOrWhiteSpace(name) ? "Unknown UVC Camera" : name,
                        SerialNumber: serialNumber,
                        ModelName: name));
                }
                finally
                {
                    dsDevice.Dispose();
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            CameraDiagnostics.Error("uvc-provider", $"Device enumeration failed: {ex.Message}");
            return Array.Empty<CameraDeviceInfo>();
        }
    }

    public ICameraSession Open(CameraOpenOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DirectShow UVC camera provider requires Windows.");
        }

        var normalized = options.Normalize();
        if (normalized.TriggerMode == CameraTriggerMode.HardwareLine0)
        {
            throw new NotSupportedException("DirectShow UVC camera provider does not support HardwareLine0 trigger mode.");
        }

        CameraDiagnostics.Info(
            "uvc-provider",
            $"Opening camera. DeviceId={normalized.DeviceId}, CameraIndex={normalized.CameraIndex}, TargetFps={normalized.TargetFps:F2}");
        return new UvcCameraSession(normalized);
    }

    private static string? ExtractSerialFromPath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return null;

        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length >= 3 && devicePath.Contains("USB", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = parts[2];
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.Length < 60 &&
                    !candidate.Contains('{') &&
                    !candidate.Contains('}') &&
                    !candidate.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (parts.Length >= 4)
                {
                    candidate = parts[3];
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        candidate.Length < 60 &&
                        !candidate.Contains('{') &&
                        !candidate.Contains('}') &&
                        !candidate.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private sealed class UvcCameraSession : ICameraSession
    {
        private const int ReadTimeoutMs = 1500;

        private readonly string _sourceId;
        private readonly string _displayName;
        private double _reportedFps;
        private readonly CameraTriggerMode _triggerMode;

        private readonly object _lock = new();
        private IGraphBuilder? _graph;
        private ICaptureGraphBuilder2? _captureGraphBuilder;
        private IBaseFilter? _sourceFilter;
        private IBaseFilter? _sampleGrabberFilter;
        private ISampleGrabber? _sampleGrabber;
        private IBaseFilter? _nullRenderer;
        private IMediaControl? _mediaControl;

        private readonly Channel<byte[]> _frameChannel;
        private readonly MonotonicPtsClock _ptsClock = new();
        private int _frameWidth;
        private int _frameHeight;
        private bool _disposed;

        public UvcCameraSession(CameraOpenOptions options)
        {
            _triggerMode = options.TriggerMode;
            _frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            var device = ResolveDevice(options);
            if (device == null)
            {
                throw new InvalidOperationException(
                    $"UVC camera not found. Tried DeviceId='{options.DeviceId}', CameraIndex={options.CameraIndex}." +
                    " Ensure the camera is connected and not in use by another application.");
            }

            _sourceId = $"{CameraProviderIds.Uvc}:{DeviceIdOrIndex(options, device)}";
            _displayName = device.DisplayName;
            _reportedFps = options.TargetFps > 0 ? options.TargetFps : 30;

            BuildGraph(device, options.TargetFps);
        }

        public string SourceId => _sourceId;
        public string DisplayName => _displayName;
        public double ReportedFps => _reportedFps;
        public CameraTriggerMode TriggerMode => _triggerMode;

        public bool TryRead(Mat destination, CancellationToken cancellationToken, out CameraFrameMetadata metadata)
        {
            metadata = default!;

            try
            {
                byte[]? frameData = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_frameChannel.Reader.TryRead(out frameData))
                        break;

                    var waited = _frameChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    if (waited.Wait(ReadTimeoutMs, cancellationToken))
                    {
                        if (_frameChannel.Reader.TryRead(out frameData))
                            break;
                    }
                    else
                    {
                        return false;
                    }
                }

                if (frameData == null || frameData.Length == 0)
                    return false;

                if (_frameWidth <= 0 || _frameHeight <= 0)
                    return false;

                destination.Create(_frameHeight, _frameWidth, MatType.CV_8UC3);

                Marshal.Copy(frameData, 0, destination.Data, frameData.Length);

                // MEDIASUBTYPE_RGB24 follows Windows DIB convention: B, G, R byte order,
                // which matches OpenCV's BGR convention directly — no channel swap needed.

                metadata = new CameraFrameMetadata(
                    _ptsClock.Next(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CameraPtsSource.MonotonicClockFallback);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                CameraDiagnostics.Error("uvc-session", $"TryRead error: {ex.Message}");
                return false;
            }
        }

        public bool TryTrigger(CancellationToken cancellationToken)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            CameraDiagnostics.Info("uvc-session", $"Closing {_displayName}. SourceId={_sourceId}");
            _frameChannel.Writer.TryComplete();

            StopGraph();

            if (OperatingSystem.IsWindows())
            {
                ReleaseFilter(ref _nullRenderer);
                ReleaseFilter(ref _sampleGrabberFilter);
                ReleaseCom(ref _sampleGrabber);
                ReleaseFilter(ref _sourceFilter);
                ReleaseCom(ref _mediaControl);
                ReleaseCom(ref _captureGraphBuilder);
                ReleaseCom(ref _graph);
            }
        }

        private static CameraDeviceInfo? ResolveDevice(CameraOpenOptions options)
        {
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devices == null || devices.Length == 0)
                return null;

            try
            {
                if (!string.IsNullOrWhiteSpace(options.DeviceId))
                {
                    foreach (var device in devices)
                    {
                        try
                        {
                            var devicePath = device.DevicePath ?? string.Empty;
                            var name = device.Name ?? string.Empty;

                            if (string.Equals(devicePath, options.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, options.DeviceId, StringComparison.OrdinalIgnoreCase))
                            {
                                var dId = string.IsNullOrWhiteSpace(devicePath) ? name : devicePath;
                                return new CameraDeviceInfo(
                                    CameraProviderIds.Uvc, dId,
                                    string.IsNullOrWhiteSpace(name) ? "UVC Camera" : name);
                            }
                        }
                        finally
                        {
                            device.Dispose();
                        }
                    }
                }

                if (options.CameraIndex >= 0 && options.CameraIndex < devices.Length)
                {
                    var device = devices[options.CameraIndex];
                    try
                    {
                        var devicePath = device.DevicePath ?? string.Empty;
                        var name = device.Name ?? string.Empty;
                        var dId = string.IsNullOrWhiteSpace(devicePath) ? name : devicePath;
                        return new CameraDeviceInfo(
                            CameraProviderIds.Uvc, dId,
                            string.IsNullOrWhiteSpace(name) ? "UVC Camera" : name);
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }

                return null;
            }
            finally
            {
            }
        }

        private void BuildGraph(CameraDeviceInfo device, double targetFps)
        {
            try
            {
                var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                IMoniker? targetMoniker = null;
                try
                {
                    foreach (var dsDevice in devices)
                    {
                        bool matched = false;
                        try
                        {
                            var devicePath = dsDevice.DevicePath ?? string.Empty;
                            var name = dsDevice.Name ?? string.Empty;
                            if ((!string.IsNullOrWhiteSpace(devicePath) &&
                                 string.Equals(devicePath, device.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(name, device.DeviceId, StringComparison.OrdinalIgnoreCase))
                            {
                                targetMoniker = dsDevice.Mon;
                                matched = true;
                                break;
                            }
                        }
                        finally
                        {
                            if (!matched)
                                dsDevice.Dispose();
                        }
                    }

                    if (targetMoniker is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not find device '{device.DeviceId}'. " +
                            "The camera may have been disconnected.");
                    }

                    _graph = (IGraphBuilder)new FilterGraph();
                    _captureGraphBuilder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();

                    var hr = _captureGraphBuilder.SetFiltergraph(_graph);
                    DsError.ThrowExceptionForHR(hr);

                    // Bind moniker to source filter
                    var sourceFilterGuid = typeof(IBaseFilter).GUID;
                    targetMoniker.BindToObject(null!, null, ref sourceFilterGuid, out var sourceFilterObj);
                    _sourceFilter = (IBaseFilter)sourceFilterObj;
                    hr = _graph.AddFilter(_sourceFilter, "Video Capture");
                    DsError.ThrowExceptionForHR(hr);

                    // Negotiate target frame rate with the camera via IAMStreamConfig
                    if (targetFps > 0)
                    {
                        NegotiateFrameRate(targetFps);
                    }

                    // Create and add SampleGrabber
                    _sampleGrabberFilter = (IBaseFilter)new SampleGrabber();
                    _sampleGrabber = (ISampleGrabber)_sampleGrabberFilter;

                    var mediaType = new AMMediaType
                    {
                        majorType = MediaType.Video,
                        subType = MediaSubType.RGB24,
                        fixedSizeSamples = true
                    };
                    hr = _sampleGrabber.SetMediaType(mediaType);
                    DsError.ThrowExceptionForHR(hr);

                    hr = _graph.AddFilter(_sampleGrabberFilter, "Sample Grabber");
                    DsError.ThrowExceptionForHR(hr);

                    // Create and add NullRenderer
                    _nullRenderer = (IBaseFilter)new NullRenderer();
                    hr = _graph.AddFilter(_nullRenderer, "Null Renderer");
                    DsError.ThrowExceptionForHR(hr);

                    // Render the capture stream: Source -> SampleGrabber -> NullRenderer
                    hr = _captureGraphBuilder.RenderStream(
                        PinCategory.Capture, MediaType.Video,
                        _sourceFilter, _sampleGrabberFilter, _nullRenderer);
                    DsError.ThrowExceptionForHR(hr);

                    // Get actual frame dimensions from the connected media type
                    ExtractFrameDimensions();

                    // Configure the SampleGrabber callback
                    var callback = new SampleGrabberCallback(_frameChannel);
                    hr = _sampleGrabber.SetCallback(callback, 1);
                    DsError.ThrowExceptionForHR(hr);

                    _sampleGrabber.SetBufferSamples(true);

                    // Get IMediaControl for graph lifecycle
                    _mediaControl = (IMediaControl)_graph;

                    // Start the graph
                    hr = _mediaControl.Run();
                    DsError.ThrowExceptionForHR(hr);

                    CameraDiagnostics.Info("uvc-session",
                        $"Graph built and running: {_frameWidth}x{_frameHeight}");
                }
                finally
                {
                    foreach (var dsDevice in devices)
                    {
                        if (targetMoniker is null || dsDevice.Mon != targetMoniker)
                            dsDevice.Dispose();
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void NegotiateFrameRate(double targetFps)
        {
            if (_captureGraphBuilder == null || _sourceFilter == null)
                return;

            try
            {
                var hr = _captureGraphBuilder.FindInterface(
                    PinCategory.Capture, MediaType.Video,
                    _sourceFilter, typeof(IAMStreamConfig).GUID,
                    out var streamConfigObj);
                if (hr != 0 || streamConfigObj == null)
                    return;

                var streamConfig = (IAMStreamConfig)streamConfigObj;
                try
                {
                    hr = streamConfig.GetFormat(out var mediaType);
                    if (hr != 0 || mediaType == null)
                        return;

                    try
                    {
                        if (mediaType.formatType == DirectShowLib.FormatType.VideoInfo)
                        {
                            var vih = (VideoInfoHeader)Marshal.PtrToStructure(
                                mediaType.formatPtr, typeof(VideoInfoHeader))!;
                            var currentFps = vih.AvgTimePerFrame > 0
                                ? 10_000_000.0 / vih.AvgTimePerFrame
                                : 0;
                            var desiredFrameTime = (long)(10_000_000.0 / targetFps);
                            vih.AvgTimePerFrame = desiredFrameTime;
                            Marshal.StructureToPtr(vih, mediaType.formatPtr, true);

                            hr = streamConfig.SetFormat(mediaType);
                            if (hr == 0)
                            {
                                _reportedFps = targetFps;
                                CameraDiagnostics.Info("uvc-session",
                                    $"Negotiated frame rate: {targetFps:F1} fps (was {currentFps:F1} fps)");
                            }
                            else
                            {
                                CameraDiagnostics.Info("uvc-session",
                                    $"Camera rejected requested frame rate {targetFps:F1} fps. Keeping native rate.");
                            }
                        }
                        else
                        {
                            CameraDiagnostics.Info("uvc-session",
                                $"Format type {mediaType.formatType} does not support frame rate negotiation.");
                        }
                    }
                    finally
                    {
                        DsUtils.FreeAMMediaType(mediaType);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(streamConfig);
                }
            }
            catch (Exception ex)
            {
                CameraDiagnostics.Info("uvc-session",
                    $"Frame rate negotiation failed: {ex.Message}. Using native camera rate.");
            }
        }

        private void ExtractFrameDimensions()
        {
            if (_sampleGrabber == null)
                return;

            var mt = new AMMediaType();
            try
            {
                var hr = _sampleGrabber.GetConnectedMediaType(mt);
                if (hr == 0 && mt.formatPtr != IntPtr.Zero)
                {
                    if (mt.formatType == DirectShowLib.FormatType.VideoInfo)
                    {
                        var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader))!;
                        _frameWidth = vih.BmiHeader.Width;
                        _frameHeight = vih.BmiHeader.Height;
                    }
                    else if (mt.formatType == DirectShowLib.FormatType.VideoInfo2)
                    {
                        var vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2))!;
                        _frameWidth = vih2.BmiHeader.Width;
                        _frameHeight = vih2.BmiHeader.Height;
                    }
                    else
                    {
                        var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader))!;
                        _frameWidth = vih.BmiHeader.Width;
                        _frameHeight = vih.BmiHeader.Height;
                    }
                }
                else
                {
                    CameraDiagnostics.Info("uvc-session",
                        $"GetConnectedMediaType returned hr=0x{hr:X8}, using 640x480.");
                    _frameWidth = 640;
                    _frameHeight = 480;
                }
            }
            catch (Exception ex)
            {
                CameraDiagnostics.Info("uvc-session",
                    $"Failed to extract frame dimensions: {ex.Message}, using 640x480.");
                _frameWidth = 640;
                _frameHeight = 480;
            }
            finally
            {
                DsUtils.FreeAMMediaType(mt);
            }
        }

        private void StopGraph()
        {
            lock (_lock)
            {
                try
                {
                    _mediaControl?.Stop();
                }
                catch (Exception ex)
                {
                    CameraDiagnostics.Info("uvc-session", $"StopGraph error: {ex.Message}");
                }
            }
        }

        private static void ReleaseFilter(ref IBaseFilter? filter)
        {
            if (filter is null)
                return;

            try
            {
#pragma warning disable CA1416
                Marshal.ReleaseComObject(filter);
#pragma warning restore CA1416
            }
            catch
            {
            }

            filter = null;
        }

        private static void ReleaseCom<T>(ref T? comObject) where T : class
        {
            if (comObject is null)
                return;

            try
            {
#pragma warning disable CA1416
                Marshal.ReleaseComObject(comObject);
#pragma warning restore CA1416
            }
            catch
            {
            }

            comObject = null;
        }

        private static string DeviceIdOrIndex(CameraOpenOptions options, CameraDeviceInfo device)
        {
            return !string.IsNullOrWhiteSpace(options.DeviceId)
                ? options.DeviceId
                : options.CameraIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class SampleGrabberCallback : ISampleGrabberCB
        {
            private readonly Channel<byte[]> _channel;

            public SampleGrabberCallback(Channel<byte[]> channel)
            {
                _channel = channel;
            }

            public int BufferCB(double sampleTime, IntPtr buffer, int bufferLength)
            {
                if (buffer == IntPtr.Zero || bufferLength <= 0)
                    return 0;

                try
                {
                    var frameData = new byte[bufferLength];
                    Marshal.Copy(buffer, frameData, 0, bufferLength);
                    _channel.Writer.TryWrite(frameData);
                }
                catch
                {
                }

                return 0;
            }

            public int SampleCB(double sampleTime, IMediaSample sample)
            {
                return 0;
            }
        }
    }
}
