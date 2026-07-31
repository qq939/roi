using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using OpenCvSharp;
using RoiAlignment.Core;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace RoiAlignment.Demo.Wpf;

public partial class MainWindow : System.Windows.Window
{
    private readonly List<RoiShape> _referenceRois = [];
    private readonly List<Shape> _referenceRoiVisuals = [];
    private readonly List<Shape> _runtimeRoiVisuals = [];
    private System.Windows.Shapes.Rectangle? _draftRectangle;
    private WpfPoint _dragStart;
    private string? _referencePath;
    private string? _runtimePath;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void LoadReference_Click(object sender, RoutedEventArgs e)
    {
        var path = PickImageFile();
        if (path is null)
        {
            return;
        }

        _referencePath = path;
        ReferencePathTextBlock.Text = path;
        ReferenceImage.Source = LoadBitmap(path);
        ResizeCanvasToImage(ReferenceCanvas, ReferenceImage.Source);
        ClearReferenceRois();
        ClearRuntimeRois();
        SetStatus("参考图已加载，可以在左侧拖拽绘制 ROI。");
    }

    private void LoadRuntime_Click(object sender, RoutedEventArgs e)
    {
        var path = PickImageFile();
        if (path is null)
        {
            return;
        }

        _runtimePath = path;
        RuntimePathTextBlock.Text = path;
        RuntimeImage.Source = LoadBitmap(path);
        ResizeCanvasToImage(RuntimeCanvas, RuntimeImage.Source);
        ClearRuntimeRois();
        SetStatus("实际图已加载。");
    }

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (_referencePath is null || _runtimePath is null)
        {
            SetStatus("请先加载参考图和实际图。", isError: true);
            return;
        }

        if (_referenceRois.Count == 0)
        {
            SetStatus("请先在参考图上拖拽绘制至少一个 ROI。", isError: true);
            return;
        }

        try
        {
            Cursor = Cursors.Wait;
            SetStatus("正在对齐...");
            var method = ReadFeatureMethod();
            var totalStopwatch = Stopwatch.StartNew();
            var loadStopwatch = Stopwatch.StartNew();
            using var reference = Cv2.ImRead(_referencePath, ImreadModes.Color);
            using var runtime = Cv2.ImRead(_runtimePath, ImreadModes.Color);
            loadStopwatch.Stop();

            var options = new AlignmentOptions
            {
                FeatureMethod = method,
                MinGoodMatches = ReadMinMatches(),
                MinInliers = Math.Max(4, ReadMinMatches() / 2),
                MinInlierRatio = 0.2,
                MaxReprojectionRmse = 6
            };

            var templateStopwatch = Stopwatch.StartNew();
            var template = AlignmentTemplateBuilder
                .FromImage(reference)
                .UseFeatureMethod(method)
                .UseAffinePartial()
                .Build();
            templateStopwatch.Stop();

            var result = new RoiAligner(options).AlignImage(template, runtime, _referenceRois);
            totalStopwatch.Stop();

            ClearRuntimeRois();
            MetricsTextBlock.Text = FormatMetrics(
                result,
                method,
                loadStopwatch.Elapsed,
                templateStopwatch.Elapsed,
                totalStopwatch.Elapsed);

            if (!result.Success)
            {
                SetStatus($"对齐失败：{result.FailureReason}", isError: true);
                return;
            }

            DrawRuntimeRois(result.AlignedRois);
            SetStatus($"对齐成功，置信度 {result.Confidence:P0}。");
        }
        catch (Exception ex)
        {
            SetStatus($"对齐异常：{ex.Message}", isError: true);
        }
        finally
        {
            Cursor = Cursors.Arrow;
        }
    }

    private void ClearRois_Click(object sender, RoutedEventArgs e)
    {
        ClearReferenceRois();
        ClearRuntimeRois();
        SetStatus("ROI 已清空。");
    }

    private void ReferenceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceImage.Source is null)
        {
            return;
        }

        _dragStart = e.GetPosition(ReferenceCanvas);
        _draftRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(45, 0, 191, 255))
        };
        Canvas.SetLeft(_draftRectangle, _dragStart.X);
        Canvas.SetTop(_draftRectangle, _dragStart.Y);
        ReferenceCanvas.Children.Add(_draftRectangle);
        ReferenceCanvas.CaptureMouse();
    }

    private void ReferenceCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draftRectangle is null || !ReferenceCanvas.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(ReferenceCanvas);
        var rect = NormalizeRect(_dragStart, current);
        Canvas.SetLeft(_draftRectangle, rect.X);
        Canvas.SetTop(_draftRectangle, rect.Y);
        _draftRectangle.Width = rect.Width;
        _draftRectangle.Height = rect.Height;
    }

    private void ReferenceCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draftRectangle is null)
        {
            return;
        }

        ReferenceCanvas.ReleaseMouseCapture();
        var current = e.GetPosition(ReferenceCanvas);
        var rect = ClampRectToImage(NormalizeRect(_dragStart, current), ReferenceImage.Source);
        ReferenceCanvas.Children.Remove(_draftRectangle);
        _draftRectangle = null;

        if (rect.Width < 5 || rect.Height < 5)
        {
            return;
        }

        var roi = RoiShape.FromXywha(
            $"ROI-{_referenceRois.Count + 1}",
            new Xywha(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0, rect.Width, rect.Height, 0));
        _referenceRois.Add(roi);
        DrawReferenceRoi(roi);
        UpdateRoiCount();
        ClearRuntimeRois();
        SetStatus($"{roi.Name} 已添加。");
    }

    private static string? PickImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All Files|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void ResizeCanvasToImage(Canvas canvas, ImageSource? image)
    {
        if (image is null)
        {
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        canvas.Width = image.Width;
        canvas.Height = image.Height;
    }

    private static WpfRect NormalizeRect(WpfPoint first, WpfPoint second)
    {
        var x = Math.Min(first.X, second.X);
        var y = Math.Min(first.Y, second.Y);
        return new WpfRect(x, y, Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));
    }

    private static WpfRect ClampRectToImage(WpfRect rect, ImageSource? image)
    {
        if (image is null)
        {
            return WpfRect.Empty;
        }

        var x1 = Math.Clamp(rect.Left, 0, image.Width);
        var y1 = Math.Clamp(rect.Top, 0, image.Height);
        var x2 = Math.Clamp(rect.Right, 0, image.Width);
        var y2 = Math.Clamp(rect.Bottom, 0, image.Height);
        return new WpfRect(new WpfPoint(x1, y1), new WpfPoint(x2, y2));
    }

    private void DrawReferenceRoi(RoiShape roi)
    {
        var shape = CreatePolygon(roi, Brushes.DeepSkyBlue, Color.FromArgb(45, 0, 191, 255));
        _referenceRoiVisuals.Add(shape);
        ReferenceCanvas.Children.Add(shape);
    }

    private void DrawRuntimeRois(IReadOnlyList<RoiShape> rois)
    {
        foreach (var roi in rois)
        {
            var shape = CreatePolygon(roi, Brushes.LimeGreen, Color.FromArgb(55, 50, 205, 50));
            _runtimeRoiVisuals.Add(shape);
            RuntimeCanvas.Children.Add(shape);
        }
    }

    private static Polygon CreatePolygon(RoiShape roi, Brush stroke, Color fill)
    {
        var polygon = new Polygon
        {
            Stroke = stroke,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(fill),
            Points = new PointCollection(roi.Points.Select(point => new WpfPoint(point.X, point.Y)))
        };
        return polygon;
    }

    private void ClearReferenceRois()
    {
        foreach (var visual in _referenceRoiVisuals)
        {
            ReferenceCanvas.Children.Remove(visual);
        }

        _referenceRoiVisuals.Clear();
        _referenceRois.Clear();
        UpdateRoiCount();
    }

    private void ClearRuntimeRois()
    {
        foreach (var visual in _runtimeRoiVisuals)
        {
            RuntimeCanvas.Children.Remove(visual);
        }

        _runtimeRoiVisuals.Clear();
        MetricsTextBlock.Text = "方法: -, 匹配: -, Good: -, Inliers: -, RMSE: -, 耗时: -";
    }

    private FeatureMethod ReadFeatureMethod()
    {
        var selected = (FeatureMethodComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return selected switch
        {
            "AKAZE" => FeatureMethod.Akaze,
            "ORB" => FeatureMethod.Orb,
            _ => FeatureMethod.Sift
        };
    }

    private int ReadMinMatches()
    {
        return int.TryParse(MinMatchesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 4, 200)
            : 12;
    }

    private static string FormatMetrics(
        AlignmentResult result,
        FeatureMethod method,
        TimeSpan loadTime,
        TimeSpan templateTime,
        TimeSpan totalTime)
    {
        var rmse = double.IsInfinity(result.ReprojectionRmse)
            ? "-"
            : result.ReprojectionRmse.ToString("0.###", CultureInfo.InvariantCulture);
        var timing = result.Timing;
        return
            $"方法: {method}, 匹配: {result.MatcherDescription}, Good: {result.GoodMatches}, Inliers: {result.Inliers}, RMSE: {rmse}, " +
            $"加载: {FormatElapsed(loadTime)}, 模板: {FormatElapsed(templateTime)}, 运行特征: {FormatElapsed(timing.RuntimeFeatureExtraction)}, " +
            $"匹配: {FormatElapsed(timing.Matching)}, RANSAC: {FormatElapsed(timing.TransformEstimation)}, ROI: {FormatElapsed(timing.RoiTransform)}, 总计: {FormatElapsed(totalTime)}";
    }

    private static string FormatElapsed(TimeSpan elapsed) => $"{elapsed.TotalMilliseconds:0} ms";

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Foreground = isError ? Brushes.Firebrick : Brushes.Black;
        StatusTextBlock.Text = message;
    }

    private void UpdateRoiCount()
    {
        RoiCountTextBlock.Text = $"ROI: {_referenceRois.Count}";
    }
}
