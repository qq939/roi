using System.IO;
using OpenCvSharp;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.Services.Clip;

public sealed class ClipFrameImageMaterializer
{
    private readonly string _rootDirectory;

    public ClipFrameImageMaterializer(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? new VisionRuntimePaths().ClipQueriesDirectory;
    }

    public string SaveFrame(Mat frame, string vectorSetId, RoiRegion? roi = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
        {
            throw new ArgumentException("Frame is empty.", nameof(frame));
        }

        Directory.CreateDirectory(_rootDirectory);
        using var image = CropFrame(frame, roi);
        var fileName = $"{Sanitize(vectorSetId)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        var path = Path.Combine(_rootDirectory, fileName);
        Cv2.ImWrite(path, image);
        return path;
    }

    public static Mat CropFrame(Mat frame, RoiRegion? roi)
    {
        if (roi == null || roi.IsFullImage)
        {
            return frame.Clone();
        }

        var width = Math.Max(1, (int)Math.Round(roi.Width));
        var height = Math.Max(1, (int)Math.Round(roi.Height));
        var center = new Point2f((float)roi.X, (float)roi.Y);
        using var rotation = Cv2.GetRotationMatrix2D(center, roi.AngleDegrees, 1.0);
        using var rotated = new Mat();
        Cv2.WarpAffine(
            frame,
            rotated,
            rotation,
            frame.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);

        var crop = new Mat();
        Cv2.GetRectSubPix(rotated, new Size(width, height), center, crop);
        return crop;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "clip_query";
        }

        var invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
