using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoInferenceDemo;

public sealed class CameraProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Camera 1";
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; }
    public string ProviderId { get; set; } = CameraProviderIds.OpenCv;
    public int CameraIndex { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string OpenCvSource { get; set; } = string.Empty;
    public string OpenCvBackend { get; set; } = string.Empty;
    public CameraTriggerMode TriggerMode { get; set; } = CameraTriggerMode.Software;
    public CameraRotation Rotation { get; set; } = CameraRotation.None;
    public CameraMirrorMode MirrorMode { get; set; } = CameraMirrorMode.None;
    public double TargetFps { get; set; } = 10;
    public bool UseSourcePtsForVideo { get; set; } = true;
    public string PrimaryTaskId { get; set; } = string.Empty;
    public string SelectedSopProfileId { get; set; } = string.Empty;
    public bool EnableSopAnalysis { get; set; } = true;
    public int AnalysisFrameWindowSize { get; set; } = 100;
    public int AnalysisStateWindowSize { get; set; } = 30;
    public int AnalysisHoldFrames { get; set; }
    public int SopWindowMs { get; set; } = 1500;
    public int SopMinScoreQ1000 { get; set; } = 450;
    public int SopMinVisibleRatioQ1000 { get; set; } = 600;
    public bool OcrEnabled { get; set; }
    public int OcrRoiX { get; set; }
    public int OcrRoiY { get; set; }
    public int OcrRoiWidth { get; set; } = 200;
    public int OcrRoiHeight { get; set; } = 40;
    public bool EnableCameraRecording { get; set; }
    public string RecordingRootDirectory { get; set; } = "Recordings";
    public int RecordingSegmentMinutes { get; set; } = 5;
    public string RecordingContainerFormat { get; set; } = "mkv";
    public string RecordingVideoEncoder { get; set; } = "hevc_nvenc";
    public string RecordingCodecFourcc { get; set; } = "MJPG";
    public int RecordingQueueCapacity { get; set; } = 120;
    public int RecordingBitrateMbps { get; set; } = 8;
    public double RecordingFps { get; set; }

    public CameraProfile Clone()
    {
        return new CameraProfile
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            AutoStart = AutoStart,
            ProviderId = ProviderId,
            CameraIndex = CameraIndex,
            DeviceId = DeviceId,
            OpenCvSource = OpenCvSource,
            OpenCvBackend = OpenCvBackend,
            TriggerMode = TriggerMode,
            Rotation = Rotation,
            MirrorMode = MirrorMode,
            TargetFps = TargetFps,
            UseSourcePtsForVideo = UseSourcePtsForVideo,
            PrimaryTaskId = PrimaryTaskId,
            SelectedSopProfileId = SelectedSopProfileId,
            EnableSopAnalysis = EnableSopAnalysis,
            AnalysisFrameWindowSize = AnalysisFrameWindowSize,
            AnalysisStateWindowSize = AnalysisStateWindowSize,
            AnalysisHoldFrames = AnalysisHoldFrames,
            SopWindowMs = SopWindowMs,
            SopMinScoreQ1000 = SopMinScoreQ1000,
            SopMinVisibleRatioQ1000 = SopMinVisibleRatioQ1000,
            OcrEnabled = OcrEnabled,
            OcrRoiX = OcrRoiX,
            OcrRoiY = OcrRoiY,
            OcrRoiWidth = OcrRoiWidth,
            OcrRoiHeight = OcrRoiHeight,
            EnableCameraRecording = EnableCameraRecording,
            RecordingRootDirectory = RecordingRootDirectory,
            RecordingSegmentMinutes = RecordingSegmentMinutes,
            RecordingContainerFormat = RecordingContainerFormat,
            RecordingVideoEncoder = RecordingVideoEncoder,
            RecordingCodecFourcc = RecordingCodecFourcc,
            RecordingQueueCapacity = RecordingQueueCapacity,
            RecordingBitrateMbps = RecordingBitrateMbps,
            RecordingFps = RecordingFps
        };
    }

    public CameraProfile Normalize(int ordinal)
    {
        return new CameraProfile
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? $"Camera {Math.Max(1, ordinal)}" : Name.Trim(),
            Enabled = Enabled,
            AutoStart = AutoStart,
            ProviderId = string.IsNullOrWhiteSpace(ProviderId) ? CameraProviderIds.OpenCv : ProviderId.Trim(),
            CameraIndex = Math.Max(0, CameraIndex),
            DeviceId = DeviceId?.Trim() ?? string.Empty,
            OpenCvSource = OpenCvSource?.Trim() ?? string.Empty,
            OpenCvBackend = OpenCvBackend?.Trim() ?? string.Empty,
            TriggerMode = Enum.IsDefined(TriggerMode) ? TriggerMode : CameraTriggerMode.Software,
            Rotation = Enum.IsDefined(Rotation) ? Rotation : CameraRotation.None,
            MirrorMode = Enum.IsDefined(MirrorMode) ? MirrorMode : CameraMirrorMode.None,
            TargetFps = TargetFps > 0 ? TargetFps : 10,
            UseSourcePtsForVideo = UseSourcePtsForVideo,
            PrimaryTaskId = PrimaryTaskId?.Trim() ?? string.Empty,
            SelectedSopProfileId = SelectedSopProfileId?.Trim() ?? string.Empty,
            EnableSopAnalysis = EnableSopAnalysis,
            AnalysisFrameWindowSize = AnalysisFrameWindowSize > 0 ? AnalysisFrameWindowSize : 100,
            AnalysisStateWindowSize = AnalysisStateWindowSize > 0 ? AnalysisStateWindowSize : 30,
            AnalysisHoldFrames = Math.Max(0, AnalysisHoldFrames),
            SopWindowMs = SopWindowMs > 0 ? SopWindowMs : 1500,
            SopMinScoreQ1000 = SopMinScoreQ1000 > 0 ? SopMinScoreQ1000 : 450,
            SopMinVisibleRatioQ1000 = SopMinVisibleRatioQ1000 > 0 ? SopMinVisibleRatioQ1000 : 600,
            OcrEnabled = OcrEnabled,
            OcrRoiX = Math.Max(0, OcrRoiX),
            OcrRoiY = Math.Max(0, OcrRoiY),
            OcrRoiWidth = Math.Max(16, OcrRoiWidth),
            OcrRoiHeight = Math.Max(8, OcrRoiHeight),
            EnableCameraRecording = EnableCameraRecording,
            RecordingRootDirectory = string.IsNullOrWhiteSpace(RecordingRootDirectory) ? "Recordings" : RecordingRootDirectory.Trim(),
            RecordingSegmentMinutes = RecordingSegmentMinutes > 0 ? RecordingSegmentMinutes : 5,
            RecordingContainerFormat = string.IsNullOrWhiteSpace(RecordingContainerFormat) ? "mkv" : RecordingContainerFormat.Trim(),
            RecordingVideoEncoder = string.IsNullOrWhiteSpace(RecordingVideoEncoder) ? "hevc_nvenc" : RecordingVideoEncoder.Trim(),
            RecordingCodecFourcc = string.IsNullOrWhiteSpace(RecordingCodecFourcc) || RecordingCodecFourcc.Trim().Length < 4
                ? "MJPG"
                : RecordingCodecFourcc.Trim(),
            RecordingQueueCapacity = RecordingQueueCapacity > 0 ? RecordingQueueCapacity : 120,
            RecordingBitrateMbps = RecordingBitrateMbps > 0 ? RecordingBitrateMbps : 8,
            RecordingFps = RecordingFps >= 0 ? RecordingFps : 0
        };
    }

    public CameraOpenOptions BuildOpenOptions(double? targetFps = null)
    {
        return new CameraOpenOptions(
                ProviderId,
                CameraIndex,
                DeviceId,
                targetFps ?? TargetFps,
                OpenCvSource,
                OpenCvBackend,
                TriggerMode,
                Rotation,
                MirrorMode)
            .Normalize();
    }

    public CameraRecordingOptions BuildRecordingOptions()
    {
        return new CameraRecordingOptions
        {
            Enabled = EnableCameraRecording,
            CameraName = Name,
            RootDirectory = RecordingRootDirectory,
            SegmentMinutes = RecordingSegmentMinutes,
            ContainerFormat = RecordingContainerFormat,
            VideoEncoder = RecordingVideoEncoder,
            QueueCapacity = RecordingQueueCapacity,
            BitrateMbps = RecordingBitrateMbps,
            RecordingFps = RecordingFps
        }.Normalize();
    }

    public static CameraProfile CreateDefault(int ordinal)
    {
        return new CameraProfile().Normalize(Math.Max(1, ordinal));
    }
}
