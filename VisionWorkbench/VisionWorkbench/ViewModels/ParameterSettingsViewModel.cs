using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forms = System.Windows.Forms;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.ViewModels;

public sealed partial class ParameterSettingsViewModel : ObservableObject
{
    private readonly InspectionConfigurationStorage _storage;
    private readonly InspectionImageArchiveService _imageArchiveService;
    private readonly SecondaryBoardViewModel _secondaryBoard;
    private readonly Mt3aModbusTcpIoConfigurationStorage _ioConfigurationStorage;
    private readonly Mt3aModbusTcpIoClient _ioModule;

    public event EventHandler? OkThresholdApplied;

    public ParameterSettingsViewModel(
        InspectionWorkspaceConfiguration configuration,
        VisionRuntimePaths runtimePaths,
        ClipRuntimeOptions clipOptions,
        InspectionConfigurationStorage storage,
        InspectionImageArchiveService imageArchiveService,
        SecondaryBoardViewModel secondaryBoard,
        Mt3aModbusTcpIoConfigurationStorage ioConfigurationStorage,
        Mt3aModbusTcpIoClient ioModule)
    {
        Configuration = configuration;
        _storage = storage;
        _imageArchiveService = imageArchiveService;
        _secondaryBoard = secondaryBoard;
        _ioConfigurationStorage = ioConfigurationStorage;
        _ioModule = ioModule;

        var ioConfiguration = _ioConfigurationStorage.LoadOrCreateConfiguration();
        imageArchiveRootDirectory = configuration.ImageArchiveRootDirectory;
        autoStartInspection = configuration.AutoStartInspection;
        secondaryBoardEnabled = secondaryBoard.IsEnabled;
        selectedSecondaryBoardLayout = secondaryBoard.Layout;
        secondaryBoardBackgroundColor = secondaryBoard.BackgroundColor;
        roiFillOpacity = configuration.RoiOverlay.FillOpacity;
        roiLabelFontSize = configuration.RoiOverlay.LabelFontSize;
        ioModuleHost = ioConfiguration.Host;
        ioModulePort = ioConfiguration.Port;
        ioModuleUnitId = ioConfiguration.UnitId;
        Items =
        [
            new ParameterSettingItem("主参数文件", runtimePaths.InspectionConfigurationPath),
            new ParameterSettingItem("IO参数文件", runtimePaths.IoModuleConfigurationPath),
            new ParameterSettingItem("当前产品", configuration.SelectedProductModelId),
            new ParameterSettingItem("配置目录", runtimePaths.ConfigDirectory),
            new ParameterSettingItem("模型目录", runtimePaths.ModelsDirectory),
            new ParameterSettingItem("缓存目录", runtimePaths.CacheDirectory),
            new ParameterSettingItem("数据库目录", runtimePaths.DatabaseDirectory),
            new ParameterSettingItem("CLIP模型", clipOptions.ModelPath),
            new ParameterSettingItem("向量库", clipOptions.DatabasePath),
            new ParameterSettingItem("对齐模板库", runtimePaths.AlignmentTemplateDatabasePath),
            new ParameterSettingItem("检测记录库", runtimePaths.InspectionResultDatabasePath)
        ];
        statusText = "参数修改后点击右上角保存。";
        LoadSavedOkThreshold();
    }

    /// <summary>
    /// 当成品号切换时调用此方法刷新 OK 阈值
    /// </summary>
    public void RefreshOkThresholdForCurrentProduct()
    {
        LoadSavedOkThreshold();
    }

    public InspectionWorkspaceConfiguration Configuration { get; }

    public ObservableCollection<ParameterSettingItem> Items { get; }

    public IReadOnlyList<string> SecondaryBoardLayoutOptions => _secondaryBoard.LayoutOptions;

    public Brush SecondaryBoardBackgroundBrush => CreateBrush(SecondaryBoardBackgroundColor);

    [ObservableProperty]
    private string imageArchiveRootDirectory;

    [ObservableProperty]
    private bool autoStartInspection;

    [ObservableProperty]
    private bool secondaryBoardEnabled;

    [ObservableProperty]
    private string selectedSecondaryBoardLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryBoardBackgroundBrush))]
    private string secondaryBoardBackgroundColor;

    [ObservableProperty]
    private double roiFillOpacity;

    [ObservableProperty]
    private double roiLabelFontSize;

    [ObservableProperty]
    private string ioModuleHost;

    [ObservableProperty]
    private int ioModulePort;

    [ObservableProperty]
    private int ioModuleUnitId;

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private float globalOkThreshold = ClipVectorSetDefinition.DefaultThreshold;

    partial void OnSelectedSecondaryBoardLayoutChanged(string value)
    {
        if (!SecondaryBoardSettings.IsSupportedLayout(value))
        {
            SelectedSecondaryBoardLayout = SecondaryBoardSettings.LayoutThreeByTwo;
        }
    }

    [RelayCommand]
    private void BrowseImageRootDirectory()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择图片根目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        var selectedPath = GetExistingDirectory(ImageArchiveRootDirectory);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            dialog.SelectedPath = selectedPath;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK &&
            !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            ImageArchiveRootDirectory = dialog.SelectedPath;
            StatusText = $"已选择图片根目录：{dialog.SelectedPath}";
        }
    }

    [RelayCommand]
    private void ChooseSecondaryBoardBackgroundColor()
    {
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = false
        };

        if (TryToDrawingColor(SecondaryBoardBackgroundColor, out var color))
        {
            dialog.Color = color;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SecondaryBoardBackgroundColor = ToRgbHex(dialog.Color);
            StatusText = $"已选择看板背景色：{SecondaryBoardBackgroundColor}";
        }
    }

    [RelayCommand]
    private void ApplyGlobalOkThreshold()
    {
        var threshold = ClipVectorSetDefinition.NormalizeThreshold(GlobalOkThreshold);
        if (!threshold.Equals(GlobalOkThreshold))
        {
            GlobalOkThreshold = threshold;
            StatusText = $"OK阈值已调整为有效范围：{threshold:F3}";
            return;
        }

        var productModelId = Configuration.SelectedProductModelId;
        var updatedCount = 0;

        foreach (var task in Configuration.Tasks)
        {
            if (task.Kind == InspectionTaskKind.Classification &&
                string.Equals(task.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase))
            {
                task.EnsureClipVectorSet().Threshold = threshold;
                updatedCount++;
            }
        }

        // 保存该成品号的阈值到配置文件
        Configuration.ProductModelOkThresholds[productModelId] = threshold;
        _storage.Save(Configuration);

        // 通知其他页面刷新阈值显示
        OkThresholdApplied?.Invoke(this, EventArgs.Empty);

        StatusText = $"已更新 {updatedCount} 个分类任务的 OK 阈值为 {threshold:F3}，并保存到配置文件";
        AppDiagnostics.Info("parameter-settings", $"Global OK threshold applied and saved. ProductModel={productModelId}, Threshold={threshold:F3}, UpdatedTasks={updatedCount}");
    }

    private void LoadSavedOkThreshold()
    {
        var productModelId = Configuration.SelectedProductModelId;
        if (Configuration.ProductModelOkThresholds.TryGetValue(productModelId, out var savedThreshold))
        {
            GlobalOkThreshold = ClipVectorSetDefinition.NormalizeThreshold((float)savedThreshold);
        }
        else
        {
            GlobalOkThreshold = ClipVectorSetDefinition.DefaultThreshold;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var validation = ValidateSettings();
        if (!validation.Success)
        {
            StatusText = validation.Message;
            return;
        }

        try
        {
            ApplyMainConfiguration(validation.ImageRootDirectory!, validation.SecondaryBoardBackgroundColor!);
            _storage.Save(Configuration);

            var ioConfiguration = _ioConfigurationStorage.LoadOrCreateConfiguration();
            ioConfiguration.Host = validation.IoHost!;
            ioConfiguration.Port = IoModulePort;
            ioConfiguration.UnitId = (byte)IoModuleUnitId;
            ioConfiguration = _ioConfigurationStorage.SaveConfiguration(ioConfiguration);

            IoModuleHost = ioConfiguration.Host;
            IoModulePort = ioConfiguration.Port;
            IoModuleUnitId = ioConfiguration.UnitId;
            await _ioModule.ReconfigureAsync(ioConfiguration.ToOptions());

            StatusText = $"参数已保存。图片根目录：{validation.ImageRootDirectory}；IO：{ioConfiguration.Host}:{ioConfiguration.Port} Unit {ioConfiguration.UnitId}";
            AppDiagnostics.Info(
                "parameter-settings",
                $"Parameter settings saved. ImageRoot={validation.ImageRootDirectory}, AutoStartInspection={AutoStartInspection}, SecondaryBoardEnabled={SecondaryBoardEnabled}, Layout={SelectedSecondaryBoardLayout}, Background={validation.SecondaryBoardBackgroundColor}, RoiFillOpacity={Configuration.RoiOverlay.FillOpacity:0.###}, RoiLabelFontSize={Configuration.RoiOverlay.LabelFontSize:0.#}, IoEndpoint={ioConfiguration.Host}:{ioConfiguration.Port}, UnitId={ioConfiguration.UnitId}");
        }
        catch (Exception ex)
        {
            StatusText = $"参数保存失败：{ex.Message}";
            AppDiagnostics.Error("parameter-settings", "Parameter settings save failed.", ex);
        }
    }

    private void ApplyMainConfiguration(string imageRootDirectory, string secondaryBoardBackgroundColor)
    {
        _imageArchiveService.SetRootDirectory(imageRootDirectory);
        Configuration.ImageArchiveRootDirectory = imageRootDirectory;
        Configuration.AutoStartInspection = AutoStartInspection;

        Configuration.SecondaryBoard.Enabled = SecondaryBoardEnabled;
        Configuration.SecondaryBoard.Layout = SecondaryBoardSettings.IsSupportedLayout(SelectedSecondaryBoardLayout)
            ? SelectedSecondaryBoardLayout
            : SecondaryBoardSettings.LayoutThreeByTwo;
        Configuration.SecondaryBoard.BackgroundColor = secondaryBoardBackgroundColor;
        Configuration.SecondaryBoard.Normalize();

        Configuration.RoiOverlay.FillOpacity = Math.Clamp(RoiFillOpacity, 0, 1);
        Configuration.RoiOverlay.LabelFontSize = Math.Clamp(RoiLabelFontSize, 8, 48);
        Configuration.RoiOverlay.Normalize();

        ImageArchiveRootDirectory = imageRootDirectory;
        SelectedSecondaryBoardLayout = Configuration.SecondaryBoard.Layout;
        SecondaryBoardBackgroundColor = Configuration.SecondaryBoard.BackgroundColor;
        RoiFillOpacity = Configuration.RoiOverlay.FillOpacity;
        RoiLabelFontSize = Configuration.RoiOverlay.LabelFontSize;

        _secondaryBoard.IsEnabled = Configuration.SecondaryBoard.Enabled;
        _secondaryBoard.Layout = Configuration.SecondaryBoard.Layout;
        _secondaryBoard.BackgroundColor = Configuration.SecondaryBoard.BackgroundColor;
        _secondaryBoard.SaveNow();
    }

    private SettingsValidationResult ValidateSettings()
    {
        var imageRoot = string.IsNullOrWhiteSpace(ImageArchiveRootDirectory)
            ? InspectionWorkspaceConfiguration.DefaultImageArchiveRootDirectory
            : ImageArchiveRootDirectory.Trim();
        try
        {
            imageRoot = Path.GetFullPath(imageRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SettingsValidationResult.Fail($"图片根目录无效：{ex.Message}");
        }

        if (!SecondaryBoardSettings.TryNormalizeColor(SecondaryBoardBackgroundColor, out var normalizedBackground))
        {
            return SettingsValidationResult.Fail("看板背景色格式错误，请输入 #RRGGBB 或 #AARRGGBB。");
        }

        if (!SecondaryBoardSettings.IsSupportedLayout(SelectedSecondaryBoardLayout))
        {
            return SettingsValidationResult.Fail("看板布局无效。");
        }

        if (IoModulePort is < 1 or > 65535)
        {
            return SettingsValidationResult.Fail("IO模块端口范围应为 1-65535。");
        }

        if (IoModuleUnitId is < 0 or > 255)
        {
            return SettingsValidationResult.Fail("IO模块站号范围应为 0-255。");
        }

        var host = string.IsNullOrWhiteSpace(IoModuleHost)
            ? "192.168.1.12"
            : IoModuleHost.Trim();

        return SettingsValidationResult.Ok(imageRoot, normalizedBackground, host);
    }

    private static string? GetExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            var parent = Directory.GetParent(fullPath);
            return parent != null && Directory.Exists(parent.FullName)
                ? parent.FullName
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Brush CreateBrush(string color)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return UiBrushes.TextMuted;
        }
    }

    private static bool TryToDrawingColor(string value, out System.Drawing.Color color)
    {
        color = System.Drawing.Color.FromArgb(17, 24, 39);
        try
        {
            var mediaColor = (Color)ColorConverter.ConvertFromString(value);
            color = System.Drawing.Color.FromArgb(mediaColor.A, mediaColor.R, mediaColor.G, mediaColor.B);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToRgbHex(System.Drawing.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private sealed class SettingsValidationResult
    {
        private SettingsValidationResult(
            bool success,
            string message,
            string? imageRootDirectory = null,
            string? secondaryBoardBackgroundColor = null,
            string? ioHost = null)
        {
            Success = success;
            Message = message;
            ImageRootDirectory = imageRootDirectory;
            SecondaryBoardBackgroundColor = secondaryBoardBackgroundColor;
            IoHost = ioHost;
        }

        public bool Success { get; }

        public string Message { get; }

        public string? ImageRootDirectory { get; }

        public string? SecondaryBoardBackgroundColor { get; }

        public string? IoHost { get; }

        public static SettingsValidationResult Ok(
            string imageRootDirectory,
            string secondaryBoardBackgroundColor,
            string ioHost)
        {
            return new SettingsValidationResult(true, string.Empty, imageRootDirectory, secondaryBoardBackgroundColor, ioHost);
        }

        public static SettingsValidationResult Fail(string message)
        {
            return new SettingsValidationResult(false, message);
        }
    }
}

public sealed record ParameterSettingItem(string Name, string Value);
