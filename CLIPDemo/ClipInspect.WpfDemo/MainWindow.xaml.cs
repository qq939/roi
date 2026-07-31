using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipInspect.Core;
using ClipInspect.Matching;
using ClipInspect.Onnx;
using ClipInspect.Storage.Sqlite;
using Microsoft.Win32;

namespace ClipInspect.WpfDemo;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ImagePathItem> _pendingOkItems = new();
    private readonly ObservableCollection<ImagePathItem> _pendingNgItems = new();
    private readonly ObservableCollection<DatabaseSampleItem> _dbOkItems = new();
    private readonly ObservableCollection<DatabaseSampleItem> _dbNgItems = new();
    private string _databasePath = @"D:\CLIP\Cache\clip_vectors.db";
    private string? _inferImagePath;

    public MainWindow()
    {
        InitializeComponent();
        PendingOkListBox.ItemsSource = _pendingOkItems;
        PendingNgListBox.ItemsSource = _pendingNgItems;
        DbOkListBox.ItemsSource = _dbOkItems;
        DbNgListBox.ItemsSource = _dbNgItems;
        DatabasePathTextBox.Text = _databasePath;
    }

    private async void SelectDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择或创建 SQLite 数据库",
            Filter = "SQLite database (*.db)|*.db|SQLite database (*.sqlite)|*.sqlite|All files (*.*)|*.*",
            InitialDirectory = TryGetPath(@"D:\CLIP\Cache"),
            FileName = Path.GetFileName(_databasePath)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _databasePath = dialog.FileName;
        DatabasePathTextBox.Text = _databasePath;
        await RefreshProductsAsync();
    }

    private async void RefreshProducts_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProductsAsync();
    }

    private async void ProductComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProductComboBox.SelectedItem is string productId)
        {
            ProductIdTextBox.Text = productId;
            InferProductComboBox.SelectedItem = productId;
            await LoadSamplesAsync(productId);
        }
    }

    private async void RefreshSamples_Click(object sender, RoutedEventArgs e)
    {
        var productId = ResolveProductId();
        if (productId is null)
        {
            ShowError("请填写或选择 Product。");
            return;
        }

        await LoadSamplesAsync(productId);
    }

    private void AddPendingOk_Click(object sender, RoutedEventArgs e)
    {
        AddPendingImages(_pendingOkItems, "选择 OK 图片");
    }

    private void AddPendingNg_Click(object sender, RoutedEventArgs e)
    {
        AddPendingImages(_pendingNgItems, "选择 NG 图片");
    }

    private void ClearPending_Click(object sender, RoutedEventArgs e)
    {
        _pendingOkItems.Clear();
        _pendingNgItems.Clear();
        SetStatus("待构建列表已清空。");
    }

    private async void BuildToDatabase_Click(object sender, RoutedEventArgs e)
    {
        var productId = ResolveProductId();
        if (productId is null)
        {
            ShowError("请填写 Product。");
            return;
        }

        if (_pendingOkItems.Count == 0 && _pendingNgItems.Count == 0)
        {
            ShowError("请先加载 OK 或 NG 图片。");
            return;
        }

        var modelPath = ResolveDefaultOnnxModelPath();
        if (!File.Exists(modelPath))
        {
            ShowError($"找不到 ONNX 模型：{modelPath}");
            return;
        }

        try
        {
            IsEnabled = false;
            SetStatus("正在编码并写入 SQLite...");

            var store = new SqliteVectorStore(_databasePath);
            var existing = await store.GetProductAsync(productId);
            using var encoder = new OnnxClipImageEncoder(modelPath);

            var encodedOk = await EncodePendingAsync(encoder, _pendingOkItems);
            var encodedNg = await EncodePendingAsync(encoder, _pendingNgItems);
            var featureDim = encodedOk.Count > 0
                ? encodedOk[0].Feature.Length
                : encodedNg.Count > 0
                    ? encodedNg[0].Feature.Length
                    : existing?.FeatureDim ?? 512;

            await store.CreateOrUpdateProductAsync(new SqliteProductConfig
            {
                ProductId = productId,
                Name = productId,
                ModelName = "ViT-B-32",
                Pretrained = "laion2b_s34b_b79k",
                FeatureDim = featureDim,
                TopK = ParseNullableInt(BuildTopKTextBox.Text) ?? existing?.TopK ?? 3,
                Threshold = ParseNullableFloat(BuildThresholdTextBox.Text) ?? existing?.Threshold ?? 0.94f,
                TextWeight = existing?.TextWeight ?? 0
            });

            foreach (var item in encodedOk)
            {
                await store.AddImageSampleAsync(productId, "OK", item.Path, item.Feature, "WpfBuild");
            }

            foreach (var item in encodedNg)
            {
                await store.AddImageSampleAsync(productId, "NG", item.Path, item.Feature, "WpfBuild");
            }

            _pendingOkItems.Clear();
            _pendingNgItems.Clear();
            await RefreshProductsAsync(productId);
            await LoadSamplesAsync(productId);
            SetStatus($"已写入 SQLite：OK {encodedOk.Count} · NG {encodedNg.Count}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void DeleteSelectedOk_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedSampleAsync(DbOkListBox.SelectedItem as DatabaseSampleItem);
    }

    private async void DeleteSelectedNg_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedSampleAsync(DbNgListBox.SelectedItem as DatabaseSampleItem);
    }

    private void DatabaseSample_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var sample = (sender as System.Windows.Controls.ListBox)?.SelectedItem as DatabaseSampleItem;
        if (sample?.ImagePath is not null)
        {
            ShowImage(sample.ImagePath);
        }
    }

    private void SelectInferImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择推理图片",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files (*.*)|*.*",
            InitialDirectory = TryGetPath(@"D:\CLIP\demoImage")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _inferImagePath = dialog.FileName;
        ShowImage(_inferImagePath);
        SetStatus($"已选择推理图片：{Path.GetFileName(_inferImagePath)}");
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (_inferImagePath is null)
        {
            ShowError("请先选择推理图片。");
            return;
        }

        if (InferProductComboBox.SelectedItem is not string productId)
        {
            ShowError("请选择 Product。");
            return;
        }

        var modelPath = ResolveDefaultOnnxModelPath();
        if (!File.Exists(modelPath))
        {
            ShowError($"找不到 ONNX 模型：{modelPath}");
            return;
        }

        try
        {
            IsEnabled = false;
            SetStatus("正在推理...");

            using var encoder = new OnnxClipImageEncoder(modelPath);
            var engine = new ClipInspectionEngine(imageEncoder: encoder);
            var result = await engine.InspectImageFromSqliteAsync(new InspectSqliteImageRequest
            {
                DatabasePath = _databasePath,
                ProductId = productId,
                ImagePath = _inferImagePath,
                TopK = ParseNullableInt(InferTopKTextBox.Text),
                Threshold = ParseNullableFloat(InferThresholdTextBox.Text)
            });

            RenderResult(result);
            SetStatus("推理完成。");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task RefreshProductsAsync(string? selectProductId = null)
    {
        try
        {
            var store = new SqliteVectorStore(_databasePath);
            var products = await store.ListProductsAsync();
            var ids = products.Select(item => item.ProductId).ToArray();
            ProductComboBox.ItemsSource = ids;
            InferProductComboBox.ItemsSource = ids;

            var selected = selectProductId ?? ProductComboBox.SelectedItem as string ?? ids.FirstOrDefault();
            if (selected is not null)
            {
                ProductComboBox.SelectedItem = selected;
                InferProductComboBox.SelectedItem = selected;
                ProductIdTextBox.Text = selected;
            }

            SetStatus($"产品查询完成：{ids.Length} 个。");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task LoadSamplesAsync(string productId)
    {
        var store = new SqliteVectorStore(_databasePath);
        var samples = await store.ListSamplesAsync(productId);
        _dbOkItems.Clear();
        _dbNgItems.Clear();

        foreach (var sample in samples.Where(item => item.Kind == "Image" && item.Enabled))
        {
            var item = new DatabaseSampleItem(sample.Id, sample.Label, sample.ImagePath ?? "", sample.Source);
            if (sample.Label == "OK")
            {
                _dbOkItems.Add(item);
            }
            else if (sample.Label == "NG")
            {
                _dbNgItems.Add(item);
            }
        }

        SetStatus($"样本加载完成：OK {_dbOkItems.Count} · NG {_dbNgItems.Count}");
    }

    private async Task DeleteSelectedSampleAsync(DatabaseSampleItem? sample)
    {
        if (sample is null)
        {
            ShowError("请先选择样本。");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"确定删除 {sample.Label} 样本？\n{sample.ImagePath}",
            "删除样本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var store = new SqliteVectorStore(_databasePath);
        var deleted = await store.DeleteSampleAsync(sample.Id);
        if (ProductComboBox.SelectedItem is string productId)
        {
            await LoadSamplesAsync(productId);
        }

        SetStatus(deleted ? "样本已删除。" : "没有找到要删除的样本。");
    }

    private static async Task<IReadOnlyList<EncodedImage>> EncodePendingAsync(
        OnnxClipImageEncoder encoder,
        IEnumerable<ImagePathItem> items)
    {
        var encoded = new List<EncodedImage>();
        foreach (var item in items)
        {
            encoded.Add(new EncodedImage(item.Path, await encoder.EncodeImageAsync(item.Path)));
        }

        return encoded;
    }

    private void AddPendingImages(ObservableCollection<ImagePathItem> target, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files (*.*)|*.*",
            InitialDirectory = TryGetPath(@"D:\CLIP\demoImage"),
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            if (!target.Any(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(new ImagePathItem(path));
            }
        }

        SetStatus($"{title}：已添加 {dialog.FileNames.Length} 张。");
    }

    private void RenderResult(InspectResult result)
    {
        ResultTextBlock.Text = result.Label.ToString();
        ResultTextBlock.Foreground = result.Label == InspectionLabel.OK
            ? new SolidColorBrush(Color.FromRgb(3, 128, 76))
            : new SolidColorBrush(Color.FromRgb(190, 18, 60));

        ScoreTextBlock.Text =
            $"Image: OK {Format(result.ImageOkScore)} · NG {Format(result.ImageNgScore)} · Margin {Format(result.ImageMargin)}\n" +
            $"Threshold {Format(result.Threshold)} · TopK {result.TopK}";

        TimingTextBlock.Text = $"Inference {result.Timing.InferenceMs:0.00} ms · Match {result.Timing.MatchMs:0.00} ms · Total {result.Timing.TotalMs:0.00} ms";

        var lines = new List<string>();
        AppendMatches(lines, "OK image", result.TopOk);
        AppendMatches(lines, "NG image", result.TopNg);
        TopKResultTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    private static void AppendMatches(List<string> lines, string title, IReadOnlyList<VectorMatch> matches)
    {
        if (matches.Count == 0)
        {
            return;
        }

        lines.Add(title);
        foreach (var item in matches)
        {
            var display = item.ImagePath ?? item.Prompt ?? "";
            lines.Add($"  {item.Rank}. {item.Similarity:0.0000}  {display}");
        }
    }

    private void ShowImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            PreviewImage.Source = null;
            ImagePlaceholderTextBlock.Visibility = Visibility.Visible;
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imagePath);
        bitmap.EndInit();
        bitmap.Freeze();

        PreviewImage.Source = bitmap;
        ImagePlaceholderTextBlock.Visibility = Visibility.Collapsed;
    }

    private string? ResolveProductId()
    {
        var productId = ProductIdTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(productId) ? null : productId;
    }

    private static int? ParseNullableInt(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static float? ParseNullableFloat(string text)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Format(float? value)
    {
        return value is null || float.IsNaN(value.Value) ? "--" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private static string? TryGetPath(string path)
    {
        return Directory.Exists(path) ? path : null;
    }

    private static string ResolveDefaultOnnxModelPath()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(current, "Models", "clip_vit_b32_image.onnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\CLIP\Models\clip_vit_b32_image.onnx";
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void ShowError(string message)
    {
        SetStatus(message);
        MessageBox.Show(this, message, "ClipInspect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed record ImagePathItem(string Path)
    {
        public string Display => System.IO.Path.GetFileName(Path);
    }

    private sealed record DatabaseSampleItem(long Id, string Label, string ImagePath, string Source)
    {
        public string Display => $"{System.IO.Path.GetFileName(ImagePath)}  [{Source}]";
    }

    private sealed record EncodedImage(string Path, float[] Feature);
}
