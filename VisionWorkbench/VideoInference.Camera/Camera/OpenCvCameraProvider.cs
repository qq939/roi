using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;

namespace VideoInferenceDemo;

public sealed class OpenCvCameraProvider : ICameraProvider
{
    public string ProviderId => CameraProviderIds.OpenCv;

    public string DisplayName => "OpenCV Webcam";

    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices()
    {
        CameraDiagnostics.Info("opencv-provider", "Device enumeration requested. OpenCV provider uses camera index selection.");
        return Array.Empty<CameraDeviceInfo>();
    }

    public ICameraSession Open(CameraOpenOptions options)
    {
        var normalized = options.Normalize();
        if (normalized.TriggerMode == CameraTriggerMode.HardwareLine0)
        {
            throw new NotSupportedException("OpenCV camera provider does not support HardwareLine0 trigger mode.");
        }

        CameraDiagnostics.Info(
            "opencv-provider",
            $"Opening camera. Index={normalized.CameraIndex}, TargetFps={normalized.TargetFps:F2}");
        return new OpenCvCameraSession(normalized);
    }

    private sealed class OpenCvCameraSession : ICameraSession
    {
        private readonly VideoCapture _capture;
        private readonly MonotonicPtsClock _ptsClock = new();

        public OpenCvCameraSession(CameraOpenOptions options)
        {
            _capture = CreateCapture(options);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                var selector = CameraOptionHelpers.GetSelector(options);
                CameraDiagnostics.Error("opencv-provider", $"Failed to open OpenCV source {selector}.");
                throw new InvalidOperationException($"Failed to open OpenCV source {selector}.");
            }

            if (options.TargetFps > 0)
            {
                _capture.Set(VideoCaptureProperties.Fps, options.TargetFps);
            }

            var sourceFps = _capture.Get(VideoCaptureProperties.Fps);
            ReportedFps = sourceFps > 0 ? sourceFps : options.TargetFps;
            DisplayName = CameraOptionHelpers.UsesOpenCvSource(options.ProviderId, options.OpenCvSource)
                ? $"OpenCV Source {options.OpenCvSource}"
                : $"OpenCV Camera {options.CameraIndex}";
            SourceId = $"{CameraProviderIds.OpenCv}:{CameraOptionHelpers.GetSelector(options)}";
            TriggerMode = options.TriggerMode;
            CameraDiagnostics.Info(
                "opencv-provider",
                $"Opened {DisplayName}. SourceId={SourceId}, TriggerMode={TriggerMode}, ReportedFps={ReportedFps:F2}");
        }

        public string SourceId { get; }

        public string DisplayName { get; }

        public double ReportedFps { get; }

        public CameraTriggerMode TriggerMode { get; }

        public bool TryRead(Mat destination, CancellationToken cancellationToken, out CameraFrameMetadata metadata)
        {
            metadata = default!;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_capture.Read(destination) && !destination.Empty())
                {
                    metadata = new CameraFrameMetadata(
                        _ptsClock.Next(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        CameraPtsSource.MonotonicClockFallback);
                    return true;
                }

                Thread.Sleep(1);
            }

            return false;
        }

        public bool TryTrigger(CancellationToken cancellationToken)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        public void Dispose()
        {
            CameraDiagnostics.Info("opencv-provider", $"Closing {DisplayName}. SourceId={SourceId}");
            _capture.Dispose();
        }

        private static VideoCapture CreateCapture(CameraOpenOptions options)
        {
            var apiPreference = ParseBackend(options.OpenCvBackend);
            if (CameraOptionHelpers.UsesOpenCvSource(options.ProviderId, options.OpenCvSource))
            {
                return apiPreference.HasValue
                    ? new VideoCapture(options.OpenCvSource!, apiPreference.Value)
                    : new VideoCapture(options.OpenCvSource!);
            }

            return apiPreference.HasValue
                ? new VideoCapture(options.CameraIndex, apiPreference.Value)
                : new VideoCapture(options.CameraIndex);
        }

        private static VideoCaptureAPIs? ParseBackend(string? backend)
        {
            if (string.IsNullOrWhiteSpace(backend))
            {
                return null;
            }

            return backend.Trim().ToLowerInvariant() switch
            {
                "any" => VideoCaptureAPIs.ANY,
                "ffmpeg" => VideoCaptureAPIs.FFMPEG,
                "gstreamer" => VideoCaptureAPIs.GSTREAMER,
                "msmf" => VideoCaptureAPIs.MSMF,
                "dshow" => VideoCaptureAPIs.DSHOW,
                "v4l2" => VideoCaptureAPIs.V4L2,
                _ => null
            };
        }
    }
}
