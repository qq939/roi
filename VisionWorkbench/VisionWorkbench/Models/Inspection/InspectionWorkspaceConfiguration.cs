namespace VisionWorkbench.Models.Inspection;

public sealed class InspectionWorkspaceConfiguration
{
    public const int CurrentSchemaVersion = 6;

    public const string DefaultImageArchiveRootDirectory = @"D:\picture";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string SelectedProductModelId { get; set; } = "default-product";

    public string ImageArchiveRootDirectory { get; set; } = DefaultImageArchiveRootDirectory;

    public bool AutoStartInspection { get; set; } = true;

    public SecondaryBoardSettings SecondaryBoard { get; set; } = new();

    public RoiOverlaySettings RoiOverlay { get; set; } = new();

    public List<ProductModelDefinition> ProductModels { get; set; } =
    [
        new ProductModelDefinition()
    ];

    public List<CameraAlignmentDefinition> Alignments { get; set; } = [];

    public List<InspectionTaskDefinition> Tasks { get; set; } = [];

    /// <summary>
    /// 保存每个成品号的 OK 阈值，格式：成品号 -> 阈值
    /// </summary>
    public Dictionary<string, double> ProductModelOkThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InspectionTaskDefinition> GetTasks(string productModelId, string cameraId)
    {
        return Tasks
            .Where(task =>
                task.Enabled &&
                string.Equals(task.ProductModelId, productModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(task.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}

public sealed class SecondaryBoardSettings
{
    public const string LayoutThreeByTwo = "3x2";

    public const string LayoutTwoByThree = "2x3";

    public const string DefaultBackgroundColor = "#111827";

    public bool Enabled { get; set; } = true;

    public string Layout { get; set; } = LayoutThreeByTwo;

    public string BackgroundColor { get; set; } = DefaultBackgroundColor;

    public Dictionary<string, CameraViewportSettings> CameraViewports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, CameraViewportSettings>> ProductCameraViewports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Layout = IsSupportedLayout(Layout) ? Layout : LayoutThreeByTwo;
        BackgroundColor = TryNormalizeColor(BackgroundColor, out var normalizedColor)
            ? normalizedColor
            : DefaultBackgroundColor;
        var viewports = CameraViewports ?? new Dictionary<string, CameraViewportSettings>();
        CameraViewports = new Dictionary<string, CameraViewportSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cameraId, viewport) in viewports)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
            {
                continue;
            }

            var normalizedViewport = viewport ?? new CameraViewportSettings();
            normalizedViewport.Normalize();
            CameraViewports[cameraId.Trim()] = normalizedViewport;
        }

        var productViewports = ProductCameraViewports ?? new Dictionary<string, Dictionary<string, CameraViewportSettings>>();
        ProductCameraViewports = new Dictionary<string, Dictionary<string, CameraViewportSettings>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, cameraViewports) in productViewports)
        {
            if (string.IsNullOrWhiteSpace(productId) || cameraViewports == null)
            {
                continue;
            }

            var normalizedCameraViewports = new Dictionary<string, CameraViewportSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cameraId, viewport) in cameraViewports)
            {
                if (string.IsNullOrWhiteSpace(cameraId))
                {
                    continue;
                }

                var normalizedViewport = viewport ?? new CameraViewportSettings();
                normalizedViewport.Normalize();
                normalizedCameraViewports[cameraId.Trim()] = normalizedViewport;
            }

            ProductCameraViewports[productId.Trim()] = normalizedCameraViewports;
        }
    }

    public static bool IsSupportedLayout(string? layout)
    {
        return string.Equals(layout, LayoutThreeByTwo, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(layout, LayoutTwoByThree, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = DefaultBackgroundColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (!color.StartsWith('#'))
        {
            color = $"#{color}";
        }

        if (color.Length is not 7 and not 9)
        {
            return false;
        }

        for (var i = 1; i < color.Length; i++)
        {
            if (!Uri.IsHexDigit(color[i]))
            {
                return false;
            }
        }

        normalized = color.ToUpperInvariant();
        return true;
    }
}

public sealed class RoiOverlaySettings
{
    public const string DefaultLabelBackgroundColor = "#111827";

    public double FillOpacity { get; set; } = 0.24;

    public string LabelBackgroundColor { get; set; } = DefaultLabelBackgroundColor;

    public double LabelFontSize { get; set; } = 16;

    public void Normalize()
    {
        FillOpacity = double.IsFinite(FillOpacity)
            ? Math.Clamp(FillOpacity, 0, 1)
            : 0.24;
        LabelFontSize = double.IsFinite(LabelFontSize)
            ? Math.Clamp(LabelFontSize, 8, 48)
            : 16;
        LabelBackgroundColor = SecondaryBoardSettings.TryNormalizeColor(LabelBackgroundColor, out var normalized)
            ? normalized
            : DefaultLabelBackgroundColor;
    }
}

public sealed class CameraViewportSettings
{
    public double Scale { get; set; }

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public void Normalize()
    {
        if (!double.IsFinite(Scale) || Scale <= 0)
        {
            Scale = 0;
        }

        if (!double.IsFinite(OffsetX))
        {
            OffsetX = 0;
        }

        if (!double.IsFinite(OffsetY))
        {
            OffsetY = 0;
        }
    }
}
