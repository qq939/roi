using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using MvCameraControl;
using OpenCvSharp;

namespace VideoInferenceDemo;

public sealed class WindowsHikCameraProvider : ICameraProvider
{
    private const int ContinuousReadDeadlineMs = 1000;
    private const int SoftwareTriggerReadDeadlineMs = 1500;

    public string ProviderId => CameraProviderIds.HikRobot;

    public string DisplayName => "HikRobot SDK";

    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices()
    {
        var devices = EnumerateMvDevices();
        var result = new List<CameraDeviceInfo>(devices.Count);
        foreach (var device in devices)
        {
            result.Add(ToCameraDeviceInfo(device));
        }

        var labels = result.Count == 0
            ? "none"
            : string.Join(", ", result.Select(item => item.DisplayName));
        CameraDiagnostics.Info("hik-provider", $"Enumerated {result.Count} Hik camera(s): {labels}");
        return result;
    }

    public ICameraSession Open(CameraOpenOptions options)
    {
        var normalized = options.Normalize();
        CameraDiagnostics.Info(
            "hik-provider",
            $"Resolving Hik camera. Selector={CameraOptionHelpers.GetSelector(normalized)}, TargetFps={normalized.TargetFps:F2}");
        var deviceInfo = ResolveDevice(normalized);
        CameraDiagnostics.Info("hik-provider", $"Resolved Hik camera to {BuildDisplayName(deviceInfo)}.");
        var device = DeviceFactory.CreateDevice(deviceInfo);
        CameraDiagnostics.Info(
            "hik-provider",
            $"Created Hik device handle. Selector={CameraOptionHelpers.GetSelector(normalized)}, TargetFps={normalized.TargetFps:F2}");

        HikCameraSession? session = null;
        try
        {
            CameraDiagnostics.Info("hik-provider", $"Calling device.Open for {BuildDisplayName(deviceInfo)}.");
            EnsureSuccess(device.Open(DeviceAccessMode.AccessExclusive, 0), "Open Hik camera");
            CameraDiagnostics.Info("hik-provider", $"device.Open succeeded for {BuildDisplayName(deviceInfo)}.");
            ConfigureDevice(device, normalized.TargetFps, normalized.TriggerMode);
            CameraDiagnostics.Info("hik-provider", $"Device parameters configured for {BuildDisplayName(deviceInfo)}.");

            var grabber = device.StreamGrabber ?? throw new InvalidOperationException("Hik stream grabber is unavailable.");
            _ = grabber.SetImageNodeNum(5);
            session = new HikCameraSession(deviceInfo, device, normalized.TargetFps, normalized.TriggerMode);
            CameraDiagnostics.Info("hik-provider", $"Calling StartGrabbing for {BuildDisplayName(deviceInfo)}.");
            EnsureSuccess(grabber.StartGrabbing(), "Start Hik camera grabbing");
            CameraDiagnostics.Info("hik-provider", $"StartGrabbing succeeded for {BuildDisplayName(deviceInfo)}.");

            CameraDiagnostics.Info("hik-provider", $"Opened Hik camera {BuildDisplayName(deviceInfo)}.");
            return session;
        }
        catch (Exception ex)
        {
            CameraDiagnostics.Error("hik-provider", "Failed to open Hik camera.", ex);
            if (session != null)
            {
                session.Dispose();
            }
            else
            {
                try
                {
                    device.Close();
                }
                catch
                {
                }

                device.Dispose();
            }
            throw;
        }
    }

    private static void ConfigureDevice(IDevice device, double targetFps, CameraTriggerMode triggerMode)
    {
        var parameters = device.Parameters;
        if (parameters == null)
        {
            return;
        }

        TrySetEnum(parameters, "AcquisitionMode", "Continuous");
        if (triggerMode == CameraTriggerMode.Software)
        {
            TrySetEnumByCandidates(parameters, ["TriggerSelector"], ["FrameStart"], required: false);
            TrySetEnumByCandidates(parameters, ["TriggerMode"], ["On"], required: true);
            TrySetEnumByCandidates(parameters, ["TriggerSource"], ["Software"], required: true);
        }
        else if (triggerMode == CameraTriggerMode.HardwareLine0)
        {
            TrySetEnumByCandidates(parameters, ["TriggerSelector"], ["FrameStart"], required: false);
            TrySetEnumByCandidates(parameters, ["TriggerMode"], ["On"], required: true);
            ConfigureHardwareTriggerSource(parameters);
            TrySetEnumByCandidates(parameters, ["TriggerActivation"], ["RisingEdge"], required: false);
        }
        else
        {
            TrySetEnumByCandidates(parameters, ["TriggerMode"], ["Off"], required: true);
        }

        if (targetFps > 0)
        {
            TrySetBool(parameters, "AcquisitionFrameRateEnable", true);
            TrySetFloat(parameters, "AcquisitionFrameRate", (float)targetFps);
        }

        LogConfiguredTiming(parameters, targetFps, triggerMode);
    }

    private static CameraDeviceInfo ToCameraDeviceInfo(IDeviceInfo device)
    {
        return new CameraDeviceInfo(
            CameraProviderIds.HikRobot,
            GetStableDeviceId(device),
            BuildDisplayName(device),
            device.SerialNumber,
            device.UserDefinedName,
            device.ModelName);
    }

    private static IDeviceInfo ResolveDevice(CameraOpenOptions options)
    {
        var devices = EnumerateMvDevices();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No Hik cameras were found.");
        }

        if (!string.IsNullOrWhiteSpace(options.DeviceId))
        {
            var device = devices.FirstOrDefault(item => MatchesDeviceIdentifier(item, options.DeviceId));
            if (device != null)
            {
                return device;
            }

            throw new InvalidOperationException($"Hik camera '{options.DeviceId}' was not found.");
        }

        if (options.CameraIndex >= 0 && options.CameraIndex < devices.Count)
        {
            return devices[options.CameraIndex];
        }

        throw new InvalidOperationException($"Hik camera index {options.CameraIndex} is out of range. Found {devices.Count} device(s).");
    }

    private static List<IDeviceInfo> EnumerateMvDevices()
    {
        List<IDeviceInfo> devices = new();
        var result = DeviceEnumerator.EnumDevices(GetSupportedLayerTypes(), out devices);
        EnsureSuccess(result, "Enumerate Hik cameras");
        return devices;
    }

    private static DeviceTLayerType GetSupportedLayerTypes()
    {
        return (DeviceTLayerType)(
            (uint)DeviceTLayerType.MvGigEDevice |
            (uint)DeviceTLayerType.MvUsbDevice);
    }

    private static bool MatchesDeviceIdentifier(IDeviceInfo device, string expected)
    {
        return string.Equals(GetStableDeviceId(device), expected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.SerialNumber, expected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.UserDefinedName, expected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(BuildDisplayName(device), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStableDeviceId(IDeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            return device.SerialNumber.Trim();
        }

        if (!string.IsNullOrWhiteSpace(device.UserDefinedName))
        {
            return device.UserDefinedName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(device.ModelName))
        {
            return device.ModelName.Trim();
        }

        return $"{device.ManufacturerName?.Trim()}-{device.DevTypeInfo}";
    }

    private static string BuildDisplayName(IDeviceInfo device)
    {
        var label = !string.IsNullOrWhiteSpace(device.UserDefinedName)
            ? device.UserDefinedName.Trim()
            : !string.IsNullOrWhiteSpace(device.SerialNumber)
                ? device.SerialNumber.Trim()
                : device.ModelName?.Trim();

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "Unknown Hik camera";
        }

        if (!string.IsNullOrWhiteSpace(device.ModelName) &&
            !string.Equals(label, device.ModelName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{device.ModelName.Trim()} ({label})";
        }

        return label;
    }

    private static void EnsureSuccess(int result, string action)
    {
        if (result == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{action} failed with {ResolveErrorName(result)} (0x{unchecked((uint)result):X8}).");
    }

    private static string ResolveErrorName(int result)
    {
        var field = typeof(MvError)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(item => item.FieldType == typeof(int) && (int)item.GetValue(null)! == result);

        return field?.Name ?? "MV_E_UNKNOWN";
    }

    private static void TrySetEnum(IParameters parameters, string key, string value)
    {
        try
        {
            _ = parameters.SetEnumValueByString(key, value);
        }
        catch
        {
        }
    }

    private static void TrySetEnumByCandidates(
        IParameters parameters,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> values,
        bool required)
    {
        foreach (var key in keys)
        {
            if (!IsWritableNode(parameters, key))
            {
                CameraDiagnostics.Info("hik-provider", $"Skip enum node {key}; node is not writable.");
                continue;
            }

            foreach (var value in values)
            {
                if (TrySetEnumValue(parameters, key, value))
                {
                    return;
                }
            }
        }

        if (required)
        {
            throw new InvalidOperationException(
                $"Unable to set Hik enum node. Keys={string.Join("|", keys)}, Values={string.Join("|", values)}.");
        }
    }

    private static string ConfigureHardwareTriggerSource(IParameters parameters)
    {
        var triggerSourceEntries = ReadSupportedEnumSymbols(parameters, "TriggerSource");
        var lineModes = ProbeLineModes(parameters);
        var candidates = HikHardwareTriggerSourceResolver.BuildCandidateOrder(lineModes, triggerSourceEntries);

        CameraDiagnostics.Info(
            "hik-provider",
            $"Resolving hardware trigger source. TriggerSourceEntries={HikHardwareTriggerSourceResolver.FormatSymbols(triggerSourceEntries)}, LineModes={HikHardwareTriggerSourceResolver.FormatLineModes(lineModes)}, Candidates={HikHardwareTriggerSourceResolver.FormatSymbols(candidates)}");

        foreach (var candidate in candidates)
        {
            if (!TrySetEnumValue(parameters, "TriggerSource", candidate))
            {
                continue;
            }

            TrySetEnumValue(parameters, "LineSelector", candidate, logFailures: false);
            CameraDiagnostics.Info("hik-provider", $"Selected Hik hardware trigger source {candidate}.");
            return candidate;
        }

        throw new InvalidOperationException(
            $"Unable to select Hik hardware trigger source. TriggerSourceEntries={HikHardwareTriggerSourceResolver.FormatSymbols(triggerSourceEntries)}, LineModes={HikHardwareTriggerSourceResolver.FormatLineModes(lineModes)}, Candidates={HikHardwareTriggerSourceResolver.FormatSymbols(candidates)}.");
    }

    private static IReadOnlyList<HikLineModeProbe> ProbeLineModes(IParameters parameters)
    {
        var lineSelectors = ReadSupportedEnumSymbols(parameters, "LineSelector")
            .Where(item => item.StartsWith("Line", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lineSelectors.Count == 0)
        {
            lineSelectors.AddRange(HikHardwareTriggerSourceResolver.DefaultLineCandidates);
        }

        var probes = new List<HikLineModeProbe>(lineSelectors.Count);
        foreach (var line in lineSelectors)
        {
            var selected = TrySetEnumValue(parameters, "LineSelector", line, logFailures: false);
            var mode = selected ? TryReadCurrentEnumSymbol(parameters, "LineMode") : null;
            probes.Add(new HikLineModeProbe(line, mode));
        }

        return probes;
    }

    private static bool TrySetEnumValue(
        IParameters parameters,
        string key,
        string value,
        bool logFailures = true)
    {
        if (!IsWritableNode(parameters, key))
        {
            if (logFailures)
            {
                CameraDiagnostics.Info("hik-provider", $"Skip enum node {key}; node is not writable.");
            }

            return false;
        }

        try
        {
            var result = parameters.SetEnumValueByString(key, value);
            if (result == 0)
            {
                CameraDiagnostics.Info("hik-provider", $"Set enum {key}={value}.");
                return true;
            }

            if (logFailures)
            {
                CameraDiagnostics.Info(
                    "hik-provider",
                    $"Set enum {key}={value} failed with {ResolveErrorName(result)} (0x{unchecked((uint)result):X8}).");
            }
        }
        catch (Exception ex)
        {
            if (logFailures)
            {
                CameraDiagnostics.Info("hik-provider", $"Set enum {key}={value} threw: {ex.Message}");
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ReadSupportedEnumSymbols(IParameters parameters, string key)
    {
        try
        {
            IEnumValue value = null!;
            var result = parameters.GetEnumValue(key, out value);
            if (result != 0 || value?.SupportEnumEntries == null)
            {
                CameraDiagnostics.Info(
                    "hik-provider",
                    $"Read enum symbols {key} failed with {ResolveErrorName(result)} (0x{unchecked((uint)result):X8}).");
                return [];
            }

            var take = value.SupportedNum > 0
                ? Math.Min(checked((int)value.SupportedNum), value.SupportEnumEntries.Length)
                : value.SupportEnumEntries.Length;
            return value.SupportEnumEntries
                .Take(take)
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Symbolic))
                .Select(item => item.Symbolic.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            CameraDiagnostics.Info("hik-provider", $"Read enum symbols {key} threw: {ex.Message}");
            return [];
        }
    }

    private static string? TryReadCurrentEnumSymbol(IParameters parameters, string key)
    {
        try
        {
            IEnumValue value = null!;
            var result = parameters.GetEnumValue(key, out value);
            if (result != 0)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(value?.CurEnumEntry?.Symbolic)
                ? null
                : value.CurEnumEntry.Symbolic.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWritableNode(IParameters parameters, string key)
    {
        try
        {
            var accessMode = XmlAccessMode.Undefined;
            var result = parameters.GetNodeAccessMode(key, out accessMode);
            if (result != 0)
            {
                return true;
            }

            return accessMode is XmlAccessMode.WO or XmlAccessMode.RW;
        }
        catch
        {
            return true;
        }
    }

    private static void TrySetBool(IParameters parameters, string key, bool value)
    {
        try
        {
            _ = parameters.SetBoolValue(key, value);
        }
        catch
        {
        }
    }

    private static void TrySetFloat(IParameters parameters, string key, float value)
    {
        try
        {
            _ = parameters.SetFloatValue(key, value);
        }
        catch
        {
        }
    }

    private static void LogConfiguredTiming(IParameters parameters, double targetFps, CameraTriggerMode triggerMode)
    {
        var acquisitionFrameRate = TryReadFloat(parameters, "AcquisitionFrameRate");
        var resultingFrameRate = TryReadFloat(parameters, "ResultingFrameRate");
        var exposureTime = TryReadFloat(parameters, "ExposureTime");

        CameraDiagnostics.Info(
            "hik-provider",
            $"Configured timing. TriggerMode={triggerMode}, RequestedFps={targetFps:F2}, AcquisitionFrameRate={FormatDiagnosticFloat(acquisitionFrameRate)}, ResultingFrameRate={FormatDiagnosticFloat(resultingFrameRate)}, ExposureTimeUs={FormatDiagnosticFloat(exposureTime)}");
    }

    private static string FormatDiagnosticFloat(double value)
    {
        return value > 0
            ? value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : "n/a";
    }

    private static double TryReadFloat(IParameters parameters, string key)
    {
        try
        {
            IFloatValue value = null!;
            var result = parameters.GetFloatValue(key, out value);
            return result == 0 && value != null ? value.CurValue : 0;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class HikCameraSession : ICameraSession, ICameraFrameCallbackSource
    {
        private readonly IDevice _device;
        private readonly IStreamGrabber _grabber;
        private readonly IPixelTypeConverter _pixelTypeConverter;
        private readonly Channel<CallbackFrame> _callbackFrames;
        private readonly TimestampDeltaPtsNormalizer _hostPtsNormalizer = new();
        private readonly DeviceTimestampPtsNormalizer _devicePtsNormalizer;
        private readonly MonotonicPtsClock _fallbackPtsClock = new();
        private byte[]? _conversionBuffer;
        private bool _firstFrameLogged;
        private int _timingLogCounter;

        public HikCameraSession(IDeviceInfo deviceInfo, IDevice device, double fallbackFps, CameraTriggerMode triggerMode)
        {
            _device = device;
            _grabber = device.StreamGrabber ?? throw new InvalidOperationException("Hik stream grabber is unavailable.");
            _pixelTypeConverter = device.PixelTypeConverter ?? throw new InvalidOperationException("Hik pixel converter is unavailable.");
            _callbackFrames = Channel.CreateBounded<CallbackFrame>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
            SourceId = $"{CameraProviderIds.HikRobot}:{GetStableDeviceId(deviceInfo)}";
            DisplayName = BuildDisplayName(deviceInfo);
            ReportedFps = ReadReportedFps(device.Parameters, fallbackFps);
            TriggerMode = triggerMode;
            _devicePtsNormalizer = new DeviceTimestampPtsNormalizer(ReportedFps);
            _grabber.FrameGrabedEvent += OnFrameGrabbed;
        }

        public string SourceId { get; }

        public string DisplayName { get; }

        public double ReportedFps { get; }

        public CameraTriggerMode TriggerMode { get; }

        public event EventHandler<CameraFrameArrivedEventArgs>? FrameArrived;

        public bool TryRead(Mat destination, CancellationToken cancellationToken, out CameraFrameMetadata metadata)
        {
            metadata = default!;
            var deadlineMs = TriggerMode == CameraTriggerMode.Software
                ? SoftwareTriggerReadDeadlineMs
                : ContinuousReadDeadlineMs;
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(deadlineMs);

            while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                if (!_callbackFrames.Reader.TryRead(out var callbackFrame))
                {
                    var remainingMs = Math.Max(1, (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
                    try
                    {
                        var waitTask = _callbackFrames.Reader.WaitToReadAsync(cancellationToken).AsTask();
                        if (!waitTask.Wait(remainingMs, cancellationToken) || !waitTask.Result)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    continue;
                }

                using (callbackFrame)
                {
                    CopyFrameToMat(callbackFrame.FrameOut.Image, destination);
                    metadata = callbackFrame.Metadata;
                    if (!_firstFrameLogged)
                    {
                        _firstFrameLogged = true;
                        CameraDiagnostics.Info(
                            "hik-provider",
                            $"First frame received from {DisplayName}. FrameNumber={callbackFrame.FrameOut.FrameNum}, Size={callbackFrame.FrameOut.Image.Width}x{callbackFrame.FrameOut.Image.Height}, PixelType={callbackFrame.FrameOut.Image.PixelType}, PtsSource={metadata.PtsSource}, ReportedFps={ReportedFps:F2}");
                    }

                    return true;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            CameraDiagnostics.Error(
                "hik-provider",
                $"Frame callback timed out for {DisplayName}. DeadlineMs={deadlineMs}, TriggerMode={TriggerMode}");
            throw new InvalidOperationException(
                $"Hik frame callback timed out after {deadlineMs} ms.");
        }

        public bool TryTrigger(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (TriggerMode == CameraTriggerMode.HardwareLine0)
            {
                return true;
            }

            ClearPendingFrames();
            var parameters = _device.Parameters ?? throw new InvalidOperationException("Hik parameters are unavailable.");
            var candidates = new[] { "TriggerSoftware", "TriggerSoftwareExecute" };
            foreach (var key in candidates)
            {
                if (!IsCommandNode(parameters, key))
                {
                    continue;
                }

                try
                {
                    var result = parameters.SetCommandValue(key);
                    if (result == 0)
                    {
                        return true;
                    }

                    CameraDiagnostics.Info(
                        "hik-provider",
                        $"Execute command {key} failed with {ResolveErrorName(result)} (0x{unchecked((uint)result):X8}).");
                }
                catch (Exception ex)
                {
                    CameraDiagnostics.Info("hik-provider", $"Execute command {key} threw: {ex.Message}");
                }
            }

            throw new InvalidOperationException("Unable to execute Hik software trigger command.");
        }

        public void Dispose()
        {
            CameraDiagnostics.Info("hik-provider", $"Closing Hik camera {DisplayName}. SourceId={SourceId}");
            _grabber.FrameGrabedEvent -= OnFrameGrabbed;
            _callbackFrames.Writer.TryComplete();
            ClearPendingFrames();

            try
            {
                _grabber.StopGrabbing();
            }
            catch
            {
            }

            try
            {
                _device.Close();
            }
            catch
            {
            }

            _device.Dispose();
        }

        public void ClearPendingFrames()
        {
            while (_callbackFrames.Reader.TryRead(out var callbackFrame))
            {
                callbackFrame.Dispose();
            }
        }

        private void OnFrameGrabbed(object? sender, FrameGrabbedEventArgs e)
        {
            if (e.FrameOut?.Image == null)
            {
                return;
            }

            CallbackFrame? callbackFrame = null;
            try
            {
                var frameCopy = (IFrameOut)e.FrameOut.Clone();
                callbackFrame = new CallbackFrame(frameCopy, BuildFrameMetadata(frameCopy));
                if (!_callbackFrames.Writer.TryWrite(callbackFrame))
                {
                    if (_callbackFrames.Reader.TryRead(out var dropped))
                    {
                        dropped.Dispose();
                    }

                    if (!_callbackFrames.Writer.TryWrite(callbackFrame))
                    {
                        callbackFrame.Dispose();
                        return;
                    }
                }

                FrameArrived?.Invoke(
                    this,
                    new CameraFrameArrivedEventArgs(SourceId, DisplayName, callbackFrame.Metadata));
                callbackFrame = null;
            }
            catch (Exception ex)
            {
                callbackFrame?.Dispose();
                CameraDiagnostics.Error("hik-provider", $"Frame callback failed for {DisplayName}: {ex.Message}");
            }
        }

        private void CopyFrameToMat(IImage image, Mat destination)
        {
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            var expectedSize = checked(width * height * 3);
            destination.Create(height, width, MatType.CV_8UC3);

            if (IsDirectBgr(image.PixelType))
            {
                CopyBytesToMat(image.PixelData, expectedSize, destination);
                return;
            }

            var bufferSize = checked((int)_pixelTypeConverter.GetBufferSizeForConvert(
                MvGvspPixelType.PixelType_Gvsp_BGR8_Packed,
                image.Width,
                image.Height));
            EnsureConversionBuffer(bufferSize);

            ulong convertedSize = (ulong)_conversionBuffer!.Length;
            EnsureSuccess(
                _pixelTypeConverter.ConvertPixelType(
                    image,
                    _conversionBuffer,
                    out convertedSize,
                    MvGvspPixelType.PixelType_Gvsp_BGR8_Packed),
                "Convert Hik frame to BGR");

            CopyBytesToMat(_conversionBuffer, checked((int)convertedSize), destination);
        }

        private void EnsureConversionBuffer(int requiredSize)
        {
            if (_conversionBuffer != null && _conversionBuffer.Length >= requiredSize)
            {
                return;
            }

            _conversionBuffer = new byte[requiredSize];
        }

        private static bool IsDirectBgr(MvGvspPixelType pixelType)
        {
            return pixelType == MvGvspPixelType.PixelType_Gvsp_BGR8_Packed ||
                   pixelType == MvGvspPixelType.PixelType_Gvsp_HB_BGR8_Packed;
        }

        private static void CopyBytesToMat(byte[] source, int byteCount, Mat destination)
        {
            if (source.Length < byteCount)
            {
                throw new InvalidOperationException("Camera frame buffer is smaller than expected.");
            }

            Marshal.Copy(source, 0, destination.Data, byteCount);
        }

        private static double ReadReportedFps(IParameters? parameters, double fallbackFps)
        {
            if (parameters != null)
            {
                var fps = TryReadFloat(parameters, "ResultingFrameRate");
                if (fps > 0)
                {
                    return fps;
                }

                fps = TryReadFloat(parameters, "AcquisitionFrameRate");
                if (fps > 0)
                {
                    return fps;
                }
            }

            return fallbackFps;
        }

        private CameraFrameMetadata BuildFrameMetadata(IFrameOut frameOut)
        {
            var captureUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rawDeviceTimestamp = unchecked((long)frameOut.DevTimeStamp);
            var rawHostTimestamp = unchecked((long)frameOut.HostTimeStamp);

            if (_hostPtsNormalizer.TryNormalize(rawHostTimestamp, out var hostPtsMs))
            {
                LogTimingSample(frameOut, rawDeviceTimestamp, rawHostTimestamp, hostPtsMs, CameraPtsSource.HostTimestamp);
                return new CameraFrameMetadata(
                    hostPtsMs,
                    captureUtcMs,
                    CameraPtsSource.HostTimestamp,
                    rawDeviceTimestamp > 0 ? rawDeviceTimestamp : null,
                    rawHostTimestamp > 0 ? rawHostTimestamp : null,
                    frameOut.FrameNum);
            }

            if (_devicePtsNormalizer.TryNormalize(rawDeviceTimestamp, out var devicePtsMs))
            {
                LogTimingSample(frameOut, rawDeviceTimestamp, rawHostTimestamp, devicePtsMs, CameraPtsSource.DeviceTimestamp);
                return new CameraFrameMetadata(
                    devicePtsMs,
                    captureUtcMs,
                    CameraPtsSource.DeviceTimestamp,
                    rawDeviceTimestamp,
                    rawHostTimestamp,
                    frameOut.FrameNum);
            }

            return new CameraFrameMetadata(
                LogFallbackPts(frameOut, rawDeviceTimestamp, rawHostTimestamp),
                captureUtcMs,
                CameraPtsSource.MonotonicClockFallback,
                rawDeviceTimestamp > 0 ? rawDeviceTimestamp : null,
                rawHostTimestamp > 0 ? rawHostTimestamp : null,
                frameOut.FrameNum);
        }

        private long LogFallbackPts(IFrameOut frameOut, long rawDeviceTimestamp, long rawHostTimestamp)
        {
            var ptsMs = _fallbackPtsClock.Next();
            LogTimingSample(frameOut, rawDeviceTimestamp, rawHostTimestamp, ptsMs, CameraPtsSource.MonotonicClockFallback);
            return ptsMs;
        }

        private void LogTimingSample(IFrameOut frameOut, long rawDeviceTimestamp, long rawHostTimestamp, long ptsMs, CameraPtsSource source)
        {
            var sampleIndex = Interlocked.Increment(ref _timingLogCounter);
            if (sampleIndex <= 5 || sampleIndex % 120 == 0)
            {
                CameraDiagnostics.Info(
                    "hik-provider-timing",
                    $"FrameNum={frameOut.FrameNum}, DevTs={rawDeviceTimestamp}, HostTs={rawHostTimestamp}, PtsMs={ptsMs}, Source={source}");
            }
        }

        private static bool IsCommandNode(IParameters parameters, string key)
        {
            try
            {
                var accessMode = XmlAccessMode.Undefined;
                var accessResult = parameters.GetNodeAccessMode(key, out accessMode);
                if (accessResult == 0 && accessMode is not (XmlAccessMode.WO or XmlAccessMode.RW))
                {
                    return false;
                }

                var interfaceType = XmlInterfaceType.IValue;
                var typeResult = parameters.GetNodeInterfaceType(key, out interfaceType);
                return typeResult != 0 || interfaceType is XmlInterfaceType.ICommand or XmlInterfaceType.IValue;
            }
            catch
            {
                return true;
            }
        }

        private sealed class CallbackFrame : IDisposable
        {
            public CallbackFrame(IFrameOut frameOut, CameraFrameMetadata metadata)
            {
                FrameOut = frameOut;
                Metadata = metadata;
            }

            public IFrameOut FrameOut { get; }

            public CameraFrameMetadata Metadata { get; }

            public void Dispose()
            {
                FrameOut.Dispose();
            }
        }

    }
}
