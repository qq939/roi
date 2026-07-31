using System;
using System.IO;

namespace VideoInferenceDemo;

public sealed class CameraRecordingOptions
{
    public bool Enabled { get; set; }
    public string CameraName { get; set; } = "Camera 1";
    public string RootDirectory { get; set; } = "Recordings";
    public int SegmentMinutes { get; set; } = 5;
    public string ContainerFormat { get; set; } = "mkv";
    public string VideoEncoder { get; set; } = "hevc_nvenc";
    public int QueueCapacity { get; set; } = 120;
    public int BitrateMbps { get; set; } = 8;
    public double RecordingFps { get; set; }

    public static CameraRecordingOptions Disabled { get; } = new()
    {
        Enabled = false
    };

    public CameraRecordingOptions Normalize()
    {
        var normalized = new CameraRecordingOptions
        {
            Enabled = Enabled,
            CameraName = SanitizePathSegment(string.IsNullOrWhiteSpace(CameraName) ? "Camera" : CameraName.Trim()),
            RootDirectory = string.IsNullOrWhiteSpace(RootDirectory) ? "Recordings" : RootDirectory.Trim(),
            SegmentMinutes = SegmentMinutes <= 0 ? 5 : SegmentMinutes,
            ContainerFormat = NormalizeContainer(ContainerFormat),
            VideoEncoder = string.IsNullOrWhiteSpace(VideoEncoder) ? "hevc_nvenc" : VideoEncoder.Trim(),
            QueueCapacity = Math.Max(10, QueueCapacity),
            BitrateMbps = BitrateMbps > 0 ? BitrateMbps : 8,
            RecordingFps = IsValidFps(RecordingFps) ? RecordingFps : 0
        };

        if (!Path.IsPathRooted(normalized.RootDirectory))
        {
            normalized.RootDirectory = Path.Combine(AppContext.BaseDirectory, normalized.RootDirectory);
        }

        return normalized;
    }

    public string ResolveFileExtension()
    {
        return ContainerFormat;
    }

    public double ResolveRecordingFps(double preferredFps, double sourceFps)
    {
        if (IsValidFps(RecordingFps))
        {
            return RecordingFps;
        }

        if (IsValidFps(preferredFps))
        {
            return preferredFps;
        }

        return IsValidFps(sourceFps) ? sourceFps : 30;
    }

    private static string NormalizeContainer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "mkv";
        }

        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        return normalized is "mkv" or "mp4" ? normalized : "mkv";
    }

    private static bool IsValidFps(double value)
    {
        return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "Camera" : value;
    }
}
