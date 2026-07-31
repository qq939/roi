using System.IO;
using System.Linq;
using OpenCvSharp;
using VideoInferenceDemo;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.Services;

public sealed class InspectionImageArchiveService
{
    private const string RawImageDirectoryName = "原图";
    private const string RenderedResultDirectoryName = "结果渲染图";
    private const string CropDirectoryName = "crop";
    private const int LabelMinSpacing = 5;

    private readonly object _syncRoot = new();
    private string _rootDirectory;
    private static readonly List<Rect> _placedLabels = new();

    public InspectionImageArchiveService(string? rootDirectory = null)
    {
        _rootDirectory = NormalizeRootDirectory(rootDirectory);
        TryEnsureRootDirectory(_rootDirectory);
    }

    public string RootDirectory
    {
        get
        {
            lock (_syncRoot)
            {
                return _rootDirectory;
            }
        }
    }

    public void SetRootDirectory(string? rootDirectory)
    {
        var normalized = NormalizeRootDirectory(rootDirectory);
        Directory.CreateDirectory(normalized);
        lock (_syncRoot)
        {
            _rootDirectory = normalized;
        }
    }

    public string SaveCameraFrame(
        Mat frame,
        string productCode,
        string serialNumber,
        string cameraName,
        DateTime timestamp,
        CameraImageWatermarkOptions watermarkOptions,
        InspectionJudgment? judgment = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(watermarkOptions);
        if (frame.Empty())
        {
            throw new ArgumentException("Camera frame is empty.", nameof(frame));
        }

        var directory = Path.Combine(
            RootDirectory,
            timestamp.ToString("yyyyMMdd"),
            SanitizePathSegment(productCode),
            RawImageDirectoryName,
            SanitizePathSegment(cameraName));
        var fileName = BuildArchiveImageFileName(timestamp, productCode, serialNumber, cameraName, judgment);
        var path = EnsureUniquePath(Path.Combine(directory, fileName));
        var normalizedWatermark = watermarkOptions.Normalize();
        if (normalizedWatermark.Enabled)
        {
            using var watermarkedFrame = FileNameWatermarkRenderer.Render(
                frame,
                Path.GetFileName(path),
                normalizedWatermark);
            SaveJpeg(watermarkedFrame, path);
        }
        else
        {
            SaveJpeg(frame, path);
        }

        return path;
    }

    public string SaveTaskCrop(
        Mat alignedFrame,
        string productCode,
        string serialNumber,
        string cameraName,
        InspectionTaskDefinition task,
        DateTime timestamp,
        InspectionJudgment? judgment = null)
    {
        ArgumentNullException.ThrowIfNull(alignedFrame);
        if (alignedFrame.Empty())
        {
            throw new ArgumentException("Aligned frame is empty.", nameof(alignedFrame));
        }

        var taskName = string.IsNullOrWhiteSpace(task.Name) ? task.Id : task.Name;
        var directory = Path.Combine(
            RootDirectory,
            timestamp.ToString("yyyyMMdd"),
            SanitizePathSegment(productCode),
            CropDirectoryName,
            SanitizePathSegment(cameraName));
        var baseFileName = BuildArchiveImageFileName(timestamp, productCode, serialNumber, cameraName, judgment);
        var fileName = Path.GetFileNameWithoutExtension(baseFileName) + $"_{SanitizeFileName(taskName)}.jpg";
        var path = EnsureUniquePath(Path.Combine(directory, fileName));

        using var crop = ClipFrameImageMaterializer.CropFrame(alignedFrame, task.Roi);
        SaveJpeg(crop, path);
        return path;
    }

    public string SaveRenderedResult(
        Mat alignedFrame,
        string productCode,
        string serialNumber,
        string cameraName,
        DateTime timestamp,
        IReadOnlyList<InspectionRenderAnnotation> annotations,
        InspectionJudgment? judgment = null,
        string? statusMessage = null)
    {
        ArgumentNullException.ThrowIfNull(alignedFrame);
        ArgumentNullException.ThrowIfNull(annotations);
        if (alignedFrame.Empty())
        {
            throw new ArgumentException("Aligned frame is empty.", nameof(alignedFrame));
        }

        var directory = Path.Combine(
            RootDirectory,
            timestamp.ToString("yyyyMMdd"),
            SanitizePathSegment(productCode),
            RenderedResultDirectoryName,
            SanitizePathSegment(cameraName));
        var fileName = BuildArchiveImageFileName(timestamp, productCode, serialNumber, cameraName, judgment);
        var path = EnsureUniquePath(Path.Combine(directory, fileName));

        using var rendered = alignedFrame.Clone();
        DrawResultAnnotations(rendered, annotations, statusMessage);
        SaveJpeg(rendered, path);
        return path;
    }

    public static void DrawResultAnnotations(
        Mat image,
        IReadOnlyList<InspectionRenderAnnotation> annotations,
        string? statusMessage)
    {
        _placedLabels.Clear();

        if (annotations.Count > 0)
        {
            using var fillLayer = image.Clone();
            foreach (var annotation in annotations)
            {
                Cv2.FillPoly(fillLayer, [GetRoiCorners(annotation.Roi, image.Size())], GetResultColor(annotation.Judgment));
            }

            Cv2.AddWeighted(fillLayer, 0.14, image, 0.86, 0, image);
        }

        for (var index = 0; index < annotations.Count; index++)
        {
            DrawResultAnnotation(image, annotations[index], index + 1);
        }

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            DrawStatusBanner(image, statusMessage, new Scalar(40, 40, 220));
        }

        _placedLabels.Clear();
    }

    private static void DrawResultAnnotation(Mat image, InspectionRenderAnnotation annotation, int ordinal)
    {
        var color = GetResultColor(annotation.Judgment);
        var corners = GetRoiCorners(annotation.Roi, image.Size());
        Cv2.Polylines(image, [corners], true, color, 2, LineTypes.AntiAlias);

        if (!annotation.Roi.IsFullImage)
        {
            DrawMeasurementMarkers(image, annotation, color);
        }

        var taskName = !string.IsNullOrWhiteSpace(annotation.TaskName) 
            ? annotation.TaskName 
            : $"T{ordinal}";
        var label = $"{taskName} {annotation.ResultText}";
        var labelAnchor = corners.OrderBy(point => point.Y).ThenBy(point => point.X).First();
        DrawLabel(image, label, labelAnchor, color);
    }

    private static void DrawMeasurementMarkers(Mat image, InspectionRenderAnnotation annotation, Scalar color)
    {
        if (annotation.FirstEdgeIndex is { } firstEdge)
        {
            DrawMeasurementEdge(image, annotation.Roi, firstEdge, color, "E1");
        }

        if (annotation.SecondEdgeIndex is { } secondEdge)
        {
            DrawMeasurementEdge(image, annotation.Roi, secondEdge, color, "E2");
        }
    }

    private static void DrawMeasurementEdge(Mat image, RoiRegion roi, double edgeIndex, Scalar color, string label)
    {
        var edge = Math.Clamp(edgeIndex, 0, Math.Max(1, roi.Width));
        var localX = edge - roi.Width / 2.0;
        var start = TransformRoiLocalPoint(roi, localX, -roi.Height / 2.0);
        var end = TransformRoiLocalPoint(roi, localX, roi.Height / 2.0);
        Cv2.Line(image, start, end, color, 3, LineTypes.AntiAlias);
        DrawLabel(image, label, start, color);
    }

    private static void DrawLabel(Mat image, string text, Point anchor, Scalar background)
    {
        const double fontScale = 0.52;
        const int thickness = 1;
        var textSize = GetTextSize(text, fontScale, thickness);
        var boxWidth = textSize.Width + 8;
        var boxHeight = textSize.Height + 7;

        var positions = new[]
        {
            new { X = anchor.X + 3, Y = anchor.Y - textSize.Height - 8 },
            new { X = anchor.X + 3, Y = anchor.Y + 3 },
            new { X = anchor.X - boxWidth - 3, Y = anchor.Y - textSize.Height - 8 },
            new { X = anchor.X - boxWidth - 3, Y = anchor.Y + 3 },
            new { X = anchor.X + 3, Y = anchor.Y + 3 }
        };

        var bestPos = positions.First();
        foreach (var pos in positions)
        {
            var candidateBox = new Rect(
                Math.Clamp(pos.X, 0, Math.Max(0, image.Width - boxWidth)),
                Math.Clamp(pos.Y, 0, Math.Max(0, image.Height - boxHeight)),
                boxWidth,
                boxHeight);

            if (!IsOverlapping(candidateBox))
            {
                bestPos = pos;
                break;
            }
        }

        var left = Math.Clamp(bestPos.X, 0, Math.Max(0, image.Width - boxWidth));
        var top = Math.Clamp(bestPos.Y, 0, Math.Max(0, image.Height - boxHeight));
        var box = new Rect(left, top, boxWidth, boxHeight);

        _placedLabels.Add(box);
        Cv2.Rectangle(image, box, background, -1, LineTypes.AntiAlias);
        PutText(image, text, new Point(left + 4, top + textSize.Height + 2), fontScale, Scalar.White, thickness);
    }

    private static bool IsOverlapping(Rect candidate)
    {
        foreach (var placed in _placedLabels)
        {
            var expanded = new Rect(
                placed.X - LabelMinSpacing,
                placed.Y - LabelMinSpacing,
                placed.Width + LabelMinSpacing * 2,
                placed.Height + LabelMinSpacing * 2);

            if (candidate.X < expanded.X + expanded.Width &&
                candidate.X + candidate.Width > expanded.X &&
                candidate.Y < expanded.Y + expanded.Height &&
                candidate.Y + candidate.Height > expanded.Y)
            {
                return true;
            }
        }
        return false;
    }

    private static void DrawStatusBanner(Mat image, string text, Scalar background)
    {
        const double fontScale = 0.65;
        const int thickness = 2;
        var textSize = GetTextSize(text, fontScale, thickness);
        var box = new Rect(8, 8, textSize.Width + 16, textSize.Height + 14);
        Cv2.Rectangle(image, box, background, -1, LineTypes.AntiAlias);
        PutText(image, text, new Point(16, 8 + textSize.Height + 4), fontScale, Scalar.White, thickness);
    }

    private static Size GetTextSize(string text, double fontScale, int thickness)
    {
        if (!text.Any(char.IsLetterOrDigit))
        {
            return new Size((int)(text.Length * fontScale * 16), (int)(fontScale * 24));
        }
        return Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
    }

    private static void PutText(Mat image, string text, Point origin, double fontScale, Scalar color, int thickness)
    {
        if (text.ContainsChinese())
        {
            DrawChineseText(image, text, origin, fontScale, color, thickness);
        }
        else
        {
            Cv2.PutText(image, text, origin, HersheyFonts.HersheySimplex, fontScale, color, thickness, LineTypes.AntiAlias);
        }
    }

    private static void DrawChineseText(Mat image, string text, Point origin, double fontScale, Scalar color, int thickness)
    {
        try
        {
            var fontSize = (int)(fontScale * 20);
            var bmp = new System.Drawing.Bitmap(image.Width, image.Height);
            try
            {
                var g = System.Drawing.Graphics.FromImage(bmp);
                try
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    var font = new System.Drawing.Font("Microsoft YaHei", fontSize, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
                    try
                    {
                        var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(
                            (int)(color.Val2 * 255),
                            (int)(color.Val1 * 255),
                            (int)(color.Val0 * 255)));
                        try
                        {
                            var textSize = g.MeasureString(text, font);
                            g.DrawString(text, font, brush, origin.X, origin.Y - textSize.Height);
                        }
                        finally { brush.Dispose(); }
                    }
                    finally { font.Dispose(); }
                }
                finally { g.Dispose(); }

                var bmpData = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var mat = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);
                try
                {
                    Cv2.CvtColor(mat, mat, ColorConversionCodes.BGRA2BGR);
                    
                    for (int y = 0; y < mat.Height; y++)
                    {
                        for (int x = 0; x < mat.Width; x++)
                        {
                            var b = mat.Get<Vec4b>(y, x);
                            if (b.Item3 > 0)
                            {
                                image.Set(y, x, new Vec3b(b.Item0, b.Item1, b.Item2));
                            }
                        }
                    }
                }
                finally
                {
                    mat.Release();
                    bmp.UnlockBits(bmpData);
                }
            }
            finally { bmp.Dispose(); }
        }
        catch
        {
            Cv2.PutText(image, text, origin, HersheyFonts.HersheySimplex, fontScale, color, thickness, LineTypes.AntiAlias);
        }
    }

    private static Point[] GetRoiCorners(RoiRegion roi, Size imageSize)
    {
        if (roi.IsFullImage)
        {
            return
            [
                new Point(0, 0),
                new Point(Math.Max(0, imageSize.Width - 1), 0),
                new Point(Math.Max(0, imageSize.Width - 1), Math.Max(0, imageSize.Height - 1)),
                new Point(0, Math.Max(0, imageSize.Height - 1))
            ];
        }

        var halfWidth = roi.Width / 2.0;
        var halfHeight = roi.Height / 2.0;
        return
        [
            TransformRoiLocalPoint(roi, -halfWidth, -halfHeight),
            TransformRoiLocalPoint(roi, halfWidth, -halfHeight),
            TransformRoiLocalPoint(roi, halfWidth, halfHeight),
            TransformRoiLocalPoint(roi, -halfWidth, halfHeight)
        ];
    }

    private static Point TransformRoiLocalPoint(RoiRegion roi, double localX, double localY)
    {
        var radians = roi.AngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point(
            (int)Math.Round(roi.X + localX * cos - localY * sin),
            (int)Math.Round(roi.Y + localX * sin + localY * cos));
    }

    private static Scalar GetResultColor(InspectionJudgment judgment) =>
        judgment == InspectionJudgment.OK
            ? new Scalar(52, 168, 83)
            : new Scalar(43, 56, 220);

    private static void SaveJpeg(Mat image, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!Cv2.ImWrite(path, image))
        {
            throw new InvalidOperationException($"Image save failed: {path}");
        }
    }

    private static string NormalizeRootDirectory(string? rootDirectory)
    {
        var value = string.IsNullOrWhiteSpace(rootDirectory)
            ? InspectionWorkspaceConfiguration.DefaultImageArchiveRootDirectory
            : rootDirectory.Trim();
        return Path.GetFullPath(value);
    }

    private static void TryEnsureRootDirectory(string rootDirectory)
    {
        try
        {
            Directory.CreateDirectory(rootDirectory);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn("image-archive", $"Image archive directory could not be created. Path={rootDirectory}, Error={ex.Message}");
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index:000}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not create a unique image path for {path}.");
    }

    private static string BuildArchiveImageFileName(
        DateTime timestamp,
        string productCode,
        string serialNumber,
        string cameraName,
        InspectionJudgment? judgment)
    {
        var normalizedSerialNumber = string.IsNullOrWhiteSpace(serialNumber)
            ? string.Empty
            : SanitizeFileName(serialNumber);
        var judgmentText = judgment.HasValue
            ? judgment.Value == InspectionJudgment.OK ? "OK" : "NG"
            : string.Empty;
        return $"{SanitizeFileName(productCode)}-{normalizedSerialNumber}-{timestamp:yyyyMMdd_HHmmss_fff}-{SanitizeFileName(cameraName)}-{judgmentText}.jpg";
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string SanitizeFileName(string value)
    {
        return SanitizePathSegment(value).Replace(' ', '_');
    }
}

public sealed record InspectionRenderAnnotation(
    RoiRegion Roi,
    InspectionJudgment Judgment,
    string ResultText,
    string? TaskName = null,
    double? FirstEdgeIndex = null,
    double? SecondEdgeIndex = null);

internal static class StringExtensions
{
    public static bool ContainsChinese(this string text)
    {
        return text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff');
    }
}
