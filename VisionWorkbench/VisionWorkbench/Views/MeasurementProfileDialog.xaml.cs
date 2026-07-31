using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenCvSharp;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public partial class MeasurementProfileDialog : System.Windows.Window
{
    private const double PlotLeft = 62;
    private const double PlotTop = 16;
    private const double PlotRight = 18;
    private const double PlotBottom = 34;
    private const double PlotGap = 28;

    private readonly Mat _roiImage;
    private readonly Measurement1DService _measurementService = new();
    private readonly MeasurementOptions _baseOptions;
    private bool _isUpdating;
    private MeasurementOptions? _workingOptions;
    private MeasurementProfileAnalysis? _analysis;

    public MeasurementProfileDialog(Mat roiImage, MeasurementOptions options)
    {
        InitializeComponent();
        _roiImage = roiImage.Clone();
        _baseOptions = CloneOptions(options);
        AppliedOptions = null;
        Closed += (_, _) => _roiImage.Dispose();

        var polarityOptions = new[]
        {
            new PolarityOption("黑到白", MeasurementEdgePolarity.BlackToWhite),
            new PolarityOption("白到黑", MeasurementEdgePolarity.WhiteToBlack)
        };

        FirstPolarityComboBox.ItemsSource = polarityOptions;
        SecondPolarityComboBox.ItemsSource = polarityOptions;
        SetControls(CloneOptions(_baseOptions));
        Recalculate();
    }

    public MeasurementOptions? AppliedOptions { get; private set; }

    private void SetControls(MeasurementOptions options)
    {
        _isUpdating = true;
        FirstPolarityComboBox.SelectedValue = options.FirstEdgePolarity;
        SecondPolarityComboBox.SelectedValue = options.SecondEdgePolarity;
        ScaleTextBox.Text = Format(options.PixelToMillimeterScale, "0.####");
        ThresholdTextBox.Text = Format(options.EdgeThreshold, "0.##");
        SmoothWindowTextBox.Text = options.SmoothWindow.ToString(CultureInfo.CurrentCulture);
        MinGapTextBox.Text = Format(options.MinEdgeGapPx, "0.##");
        _isUpdating = false;
    }

    private void Parameter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdating || !IsLoaded)
        {
            return;
        }

        Recalculate();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_workingOptions == null)
        {
            return;
        }

        AppliedOptions = CloneOptions(_workingOptions);
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Recalculate()
    {
        if (!TryReadOptions(out var options, out var error))
        {
            _workingOptions = null;
            _analysis = null;
            StatusTextBlock.Text = error;
            ChartCanvas.Children.Clear();
            return;
        }

        _workingOptions = options;
        _analysis = _measurementService.Analyze(_roiImage, options);
        StatusTextBlock.Text = BuildStatusText(_analysis);
        DrawChart();
    }

    private bool TryReadOptions(out MeasurementOptions options, out string error)
    {
        options = new MeasurementOptions();
        error = string.Empty;

        if (FirstPolarityComboBox.SelectedValue is not MeasurementEdgePolarity first ||
            SecondPolarityComboBox.SelectedValue is not MeasurementEdgePolarity second)
        {
            error = "请选择边缘极性";
            return false;
        }

        if (!TryParseDouble(ScaleTextBox.Text, out var scale) || scale <= 0)
        {
            error = "比例必须大于 0";
            return false;
        }

        if (!TryParseDouble(ThresholdTextBox.Text, out var threshold) || threshold <= 0)
        {
            error = "阈值必须大于 0";
            return false;
        }

        if (!int.TryParse(SmoothWindowTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var smoothWindow) ||
            smoothWindow < 1)
        {
            error = "平滑窗口必须大于等于 1";
            return false;
        }

        if (!TryParseDouble(MinGapTextBox.Text, out var minGap) || minGap < 0)
        {
            error = "边距必须大于等于 0";
            return false;
        }

        options.FirstEdgePolarity = first;
        options.SecondEdgePolarity = second;
        options.PixelToMillimeterScale = scale;
        options.MinDistanceMm = _baseOptions.MinDistanceMm;
        options.MaxDistanceMm = _baseOptions.MaxDistanceMm;
        options.EdgeThreshold = threshold;
        options.SmoothWindow = smoothWindow;
        options.MinEdgeGapPx = minGap;
        options.Normalize();
        return true;
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        if (_analysis == null || _analysis.SmoothedProfile.Count == 0)
        {
            return;
        }

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        var (profilePlot, gradientPlot) = CreatePlotAreas(width, height);
        if (profilePlot.Width <= 20 || profilePlot.Height <= 20 || gradientPlot.Height <= 20)
        {
            return;
        }

        var pointCount = _analysis.SmoothedProfile.Count;
        DrawRegions(profilePlot, _analysis);
        DrawProfileAxes(profilePlot, pointCount);
        DrawGradientAxes(gradientPlot, pointCount, _workingOptions?.EdgeThreshold ?? 0);
        DrawProfile(_analysis.RawProfile, profilePlot, Brushes.DimGray, 1.0);
        DrawProfile(_analysis.SmoothedProfile, profilePlot, Brushes.DeepSkyBlue, 1.8);
        DrawProfile(_analysis.Gradient, gradientPlot, Brushes.HotPink, 1.6);
        DrawEdge(_analysis.FirstEdgeIndex, profilePlot, gradientPlot, Brushes.Orange, "E1");
        DrawEdge(_analysis.SecondEdgeIndex, profilePlot, gradientPlot, Brushes.LimeGreen, "E2");
    }

    private (ChartPlotArea Profile, ChartPlotArea Gradient) CreatePlotAreas(double width, double height)
    {
        var availableHeight = Math.Max(0, height - PlotTop - PlotBottom - PlotGap);
        var profileHeight = Math.Max(0, availableHeight * 0.62);
        var gradientHeight = Math.Max(0, availableHeight - profileHeight);
        var plotWidth = Math.Max(0, width - PlotLeft - PlotRight);
        var profilePlot = new ChartPlotArea(
            PlotLeft,
            PlotTop,
            plotWidth,
            profileHeight,
            0,
            255);

        var maxGradient = BuildGradientRange(_analysis?.Gradient ?? Array.Empty<double>(), _workingOptions?.EdgeThreshold ?? 0);
        var gradientPlot = new ChartPlotArea(
            PlotLeft,
            profilePlot.Bottom + PlotGap,
            plotWidth,
            gradientHeight,
            -maxGradient,
            maxGradient);

        return (profilePlot, gradientPlot);
    }

    private void DrawProfileAxes(ChartPlotArea plot, int pointCount)
    {
        var axisBrush = new SolidColorBrush(Color.FromRgb(162, 172, 184));
        var gridBrush = new SolidColorBrush(Color.FromArgb(56, 162, 172, 184));

        foreach (var value in new[] { 0, 64, 128, 192, 255 })
        {
            var y = ToCanvasY(value, plot);
            AddLine(plot.Left, plot.Right, y, y, gridBrush, 0.7);
            AddAxisText(value.ToString(CultureInfo.CurrentCulture), 6, y - 8, axisBrush, 11);
        }

        DrawXGrid(plot, pointCount, axisBrush, gridBrush, drawLabels: false);
        AddLine(plot.Left, plot.Right, plot.Bottom, plot.Bottom, axisBrush, 1.2);
        AddLine(plot.Left, plot.Left, plot.Top, plot.Bottom, axisBrush, 1.2);
        AddAxisText("灰度", 8, 2, axisBrush, 12);
        AddLegend(plot);
    }

    private void DrawGradientAxes(ChartPlotArea plot, int pointCount, double threshold)
    {
        var axisBrush = new SolidColorBrush(Color.FromRgb(162, 172, 184));
        var gridBrush = new SolidColorBrush(Color.FromArgb(56, 162, 172, 184));
        var thresholdBrush = new SolidColorBrush(Color.FromArgb(170, 245, 158, 11));

        foreach (var value in new[] { plot.MinValue, 0, plot.MaxValue }.Distinct())
        {
            var y = ToCanvasY(value, plot);
            AddLine(plot.Left, plot.Right, y, y, gridBrush, value == 0 ? 1.0 : 0.7);
            AddAxisText(value.ToString("0.#", CultureInfo.CurrentCulture), 6, y - 8, axisBrush, 11);
        }

        if (threshold > 0)
        {
            DrawThresholdLine(threshold, plot, thresholdBrush, $"+{threshold:0.##}");
            DrawThresholdLine(-threshold, plot, thresholdBrush, $"-{threshold:0.##}");
        }

        DrawXGrid(plot, pointCount, axisBrush, gridBrush, drawLabels: true);
        AddLine(plot.Left, plot.Right, plot.Bottom, plot.Bottom, axisBrush, 1.2);
        AddLine(plot.Left, plot.Left, plot.Top, plot.Bottom, axisBrush, 1.2);
        AddAxisText("边缘响应", 8, plot.Top + 4, axisBrush, 12);
        AddAxisText("位置(px)", plot.Right - 52, plot.Bottom + 20, axisBrush, 12);
    }

    private void DrawXGrid(
        ChartPlotArea plot,
        int pointCount,
        Brush axisBrush,
        Brush gridBrush,
        bool drawLabels)
    {
        var xTicks = BuildXTicks(pointCount);
        foreach (var index in xTicks)
        {
            var x = ToCanvasX(index, Math.Max(2, pointCount), plot);
            AddLine(x, x, plot.Top, plot.Bottom, gridBrush, 0.7);
            if (drawLabels)
            {
                AddAxisText(index.ToString(CultureInfo.CurrentCulture), x - 12, plot.Bottom + 6, axisBrush, 11);
            }
        }
    }

    private void DrawThresholdLine(double value, ChartPlotArea plot, Brush stroke, string label)
    {
        if (value < plot.MinValue || value > plot.MaxValue)
        {
            return;
        }

        var y = ToCanvasY(value, plot);
        var line = new Line
        {
            X1 = plot.Left,
            X2 = plot.Right,
            Y1 = y,
            Y2 = y,
            Stroke = stroke,
            StrokeThickness = 1.0,
            StrokeDashArray = [4, 3]
        };
        ChartCanvas.Children.Add(line);
        AddAxisText(label, plot.Right - 42, y - 14, stroke, 11);
    }

    private void AddLegend(ChartPlotArea plot)
    {
        AddAxisText("原始", plot.Right - 138, plot.Top + 4, Brushes.DimGray, 11);
        AddAxisText("平滑", plot.Right - 94, plot.Top + 4, Brushes.DeepSkyBlue, 11);
        AddAxisText("边缘响应", plot.Right - 50, plot.Top + 4, Brushes.HotPink, 11);
    }

    private int[] BuildXTicks(int pointCount)
    {
        if (pointCount <= 1)
        {
            return [0];
        }

        var max = pointCount - 1;
        return new[] { 0, max / 4, max / 2, max * 3 / 4, max }
            .Distinct()
            .ToArray();
    }

    private void AddLine(double x1, double x2, double y1, double y2, Brush stroke, double thickness)
    {
        ChartCanvas.Children.Add(new Line
        {
            X1 = x1,
            X2 = x2,
            Y1 = y1,
            Y2 = y2,
            Stroke = stroke,
            StrokeThickness = thickness
        });
    }

    private void AddAxisText(string text, double left, double top, Brush foreground, double fontSize)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize
        };
        Canvas.SetLeft(textBlock, left);
        Canvas.SetTop(textBlock, top);
        ChartCanvas.Children.Add(textBlock);
    }

    private void DrawRegions(ChartPlotArea plot, MeasurementProfileAnalysis analysis)
    {
        var count = analysis.SmoothedProfile.Count;
        if (count < 2)
        {
            return;
        }

        var first = analysis.FirstEdgeIndex.HasValue ? ToCanvasX(analysis.FirstEdgeIndex.Value, count, plot) : (double?)null;
        var second = analysis.SecondEdgeIndex.HasValue ? ToCanvasX(analysis.SecondEdgeIndex.Value, count, plot) : (double?)null;

        if (first.HasValue)
        {
            AddRegion(plot.Left, first.Value, plot, Color.FromArgb(28, 255, 150, 0));
            if (second.HasValue)
            {
                AddRegion(first.Value, second.Value, plot, Color.FromArgb(24, 40, 167, 69));
                AddRegion(second.Value, plot.Right, plot, Color.FromArgb(28, 0, 120, 255));
            }
            else
            {
                AddRegion(first.Value, plot.Right, plot, Color.FromArgb(28, 0, 120, 255));
            }
        }
    }

    private void AddRegion(double x1, double x2, ChartPlotArea plot, Color color)
    {
        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        if (right - left <= 0.5)
        {
            return;
        }

        var rectangle = new Rectangle
        {
            Width = right - left,
            Height = plot.Height,
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, plot.Top);
        ChartCanvas.Children.Add(rectangle);
    }

    private void DrawProfile(
        IReadOnlyList<double> values,
        ChartPlotArea plot,
        Brush stroke,
        double thickness)
    {
        if (values.Count < 2)
        {
            return;
        }

        var polyline = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            SnapsToDevicePixels = true
        };

        for (var index = 0; index < values.Count; index++)
        {
            var x = ToCanvasX(index, values.Count, plot);
            var y = ToCanvasY(values[index], plot);
            polyline.Points.Add(new System.Windows.Point(x, y));
        }

        ChartCanvas.Children.Add(polyline);
    }

    private void DrawEdge(
        double? edgeIndex,
        ChartPlotArea profilePlot,
        ChartPlotArea gradientPlot,
        Brush stroke,
        string label)
    {
        if (!edgeIndex.HasValue || _analysis == null || _analysis.SmoothedProfile.Count < 2)
        {
            return;
        }

        var x = ToCanvasX(edgeIndex.Value, _analysis.SmoothedProfile.Count, profilePlot);
        ChartCanvas.Children.Add(new Line
        {
            X1 = x,
            X2 = x,
            Y1 = profilePlot.Top,
            Y2 = gradientPlot.Bottom,
            Stroke = stroke,
            StrokeThickness = 2
        });

        var text = new TextBlock
        {
            Text = label,
            Foreground = stroke,
            FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(text, Math.Clamp(x + 4, profilePlot.Left, Math.Max(profilePlot.Left, profilePlot.Right - 24)));
        Canvas.SetTop(text, profilePlot.Top + 18);
        ChartCanvas.Children.Add(text);
    }

    private string BuildStatusText(MeasurementProfileAnalysis analysis)
    {
        var judgment = analysis.Judgment.ToString();
        var distance = analysis.DistanceMm.HasValue
            ? $"距离 {analysis.DistanceMm.Value:0.00} mm / {analysis.DistancePx:0.00} px"
            : "距离 --";
        var edges = $"E1 {FormatNullable(analysis.FirstEdgeIndex, "0.00")} / E2 {FormatNullable(analysis.SecondEdgeIndex, "0.00")}";
        var strength = $"强度 {FormatNullable(analysis.FirstEdgeStrength, "0.00")} / {FormatNullable(analysis.SecondEdgeStrength, "0.00")}";
        var message = string.IsNullOrWhiteSpace(analysis.FailureReason) ? string.Empty : $"，{analysis.FailureReason}";
        return $"{judgment}，{distance}，{edges}，{strength}{message}";
    }

    private static double ToCanvasX(double index, int count, ChartPlotArea plot)
    {
        return count <= 1
            ? plot.Left
            : plot.Left + Math.Clamp(index / (count - 1), 0, 1) * plot.Width;
    }

    private static double ToCanvasY(double value, ChartPlotArea plot)
    {
        var range = plot.MaxValue - plot.MinValue;
        var normalized = range <= 0
            ? 0
            : Math.Clamp((value - plot.MinValue) / range, 0, 1);
        return plot.Top + (1 - normalized) * plot.Height;
    }

    private static double BuildGradientRange(IReadOnlyList<double> values, double threshold)
    {
        var maxAbs = Math.Max(threshold, 1);
        foreach (var value in values)
        {
            if (double.IsFinite(value))
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(value));
            }
        }

        return Math.Ceiling(maxAbs * 1.15);
    }

    private static MeasurementOptions CloneOptions(MeasurementOptions source)
    {
        var clone = new MeasurementOptions
        {
            FirstEdgePolarity = source.FirstEdgePolarity,
            SecondEdgePolarity = source.SecondEdgePolarity,
            PixelToMillimeterScale = source.PixelToMillimeterScale,
            MinDistanceMm = source.MinDistanceMm,
            MaxDistanceMm = source.MaxDistanceMm,
            EdgeThreshold = source.EdgeThreshold,
            SmoothWindow = source.SmoothWindow,
            MinEdgeGapPx = source.MinEdgeGapPx
        };
        clone.Normalize();
        return clone;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Format(double value, string format)
    {
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private static string FormatNullable(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format, CultureInfo.CurrentCulture) : "--";
    }

    private sealed record PolarityOption(string Text, MeasurementEdgePolarity Value);

    private readonly record struct ChartPlotArea(
        double Left,
        double Top,
        double Width,
        double Height,
        double MinValue,
        double MaxValue)
    {
        public double Right => Left + Width;

        public double Bottom => Top + Height;
    }
}
