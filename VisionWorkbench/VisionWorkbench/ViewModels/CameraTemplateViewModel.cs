using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.ViewModels;

public sealed partial class CameraTemplateViewModel : ObservableObject
{
    public CameraTemplateViewModel(CameraViewModel camera, CameraAlignmentDefinition definition)
    {
        Camera = camera;
        Definition = definition;
        Camera.PropertyChanged += OnCameraPropertyChanged;
    }

    public CameraViewModel Camera { get; }

    public CameraAlignmentDefinition Definition { get; }

    public string CameraName => Camera.Name;

    public string CameraId => Camera.ConfigurationId;

    public string ReferenceImagePathText =>
        string.IsNullOrWhiteSpace(Definition.ReferenceImageRelativePath) ? "-" : Definition.ReferenceImageRelativePath;

    public string TemplatePathText =>
        IsTemplateCreated
            ? string.IsNullOrWhiteSpace(Definition.TemplateRelativePath) ? "SQLite" : Definition.TemplateRelativePath
            : "-";

    public string ImageSizeText =>
        Definition.ImageWidth > 0 && Definition.ImageHeight > 0
            ? $"{Definition.ImageWidth} x {Definition.ImageHeight}"
            : "-";

    public string KeyPointText => Definition.KeyPointCount > 0 ? Definition.KeyPointCount.ToString() : "-";

    public string DescriptorText =>
        Definition.DescriptorRows > 0 && Definition.DescriptorCols > 0
            ? $"{Definition.DescriptorRows} x {Definition.DescriptorCols}, type {Definition.DescriptorMatType}"
            : "-";

    public string RegisteredAtText =>
        Definition.RegisteredAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    public string EffectiveAlignmentRegionText => Definition.EffectiveAlignmentRegion is { } region
        ? $"左上 ({region.Left:0}, {region.Top:0})  右下 ({region.Right:0}, {region.Bottom:0})"
        : "整图";

    public bool HasEffectiveAlignmentRegion => Definition.EffectiveAlignmentRegion != null;

    public string AlignmentModeDisplay
    {
        get => Definition.AlignmentMode;
        set
        {
            if (string.Equals(Definition.AlignmentMode, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Definition.AlignmentMode = value;
            OnPropertyChanged();
        }
    }

    public bool IsTemplateCreated => Definition.DescriptorRows > 0 && Definition.DescriptorCols > 0;

    public bool HasTemplateParameterMismatch =>
        IsTemplateCreated &&
        (!string.Equals(
             NormalizeFeatureMethod(Definition.FeatureMethod),
             NormalizeFeatureMethod(Definition.RegisteredFeatureMethod),
             StringComparison.OrdinalIgnoreCase) ||
         MaxLongSide != (Definition.RegisteredMaxLongSide <= 0 ? MaxLongSide : Definition.RegisteredMaxLongSide) ||
         MaxFeatures != (Definition.RegisteredMaxFeatures <= 0 ? MaxFeatures : Definition.RegisteredMaxFeatures) ||
         !Definition.IsEffectiveAlignmentRegionCurrent);

    public string FeatureMethodDisplay
    {
        get => ToFeatureMethodDisplay(Definition.FeatureMethod);
        set
        {
            var normalized = NormalizeFeatureMethod(value);
            if (string.Equals(Definition.FeatureMethod, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Definition.FeatureMethod = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: true);
            OnPropertyChanged();
        }
    }

    public int MaxLongSide
    {
        get => Definition.MaxLongSide <= 0 ? 1600 : Definition.MaxLongSide;
        set
        {
            var normalized = value <= 0 ? 1600 : value;
            if (Definition.MaxLongSide == normalized)
            {
                return;
            }

            Definition.MaxLongSide = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: true);
            OnPropertyChanged();
        }
    }

    public int MaxFeatures
    {
        get => Definition.MaxFeatures <= 0 ? 5000 : Definition.MaxFeatures;
        set
        {
            var normalized = value <= 0 ? 5000 : value;
            if (Definition.MaxFeatures == normalized)
            {
                return;
            }

            Definition.MaxFeatures = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: true);
            OnPropertyChanged();
        }
    }

    public double LoweRatio
    {
        get => Definition.LoweRatio <= 0 ? 0.75 : Definition.LoweRatio;
        set
        {
            var normalized = value <= 0 ? 0.75 : value;
            if (Math.Abs(Definition.LoweRatio - normalized) < 0.000001)
            {
                return;
            }

            Definition.LoweRatio = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public int MinGoodMatches
    {
        get => Definition.MinGoodMatches <= 0 ? 12 : Definition.MinGoodMatches;
        set
        {
            var normalized = value <= 0 ? 12 : value;
            if (Definition.MinGoodMatches == normalized)
            {
                return;
            }

            Definition.MinGoodMatches = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public int MinInliers
    {
        get => Definition.MinInliers <= 0 ? 8 : Definition.MinInliers;
        set
        {
            var normalized = value <= 0 ? 8 : value;
            if (Definition.MinInliers == normalized)
            {
                return;
            }

            Definition.MinInliers = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public double MinInlierRatio
    {
        get => Definition.MinInlierRatio <= 0 ? 0.30 : Definition.MinInlierRatio;
        set
        {
            var normalized = value <= 0 ? 0.30 : value;
            if (Math.Abs(Definition.MinInlierRatio - normalized) < 0.000001)
            {
                return;
            }

            Definition.MinInlierRatio = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public double RansacReprojectionThreshold
    {
        get => Definition.RansacReprojectionThreshold <= 0 ? 3.0 : Definition.RansacReprojectionThreshold;
        set
        {
            var normalized = value <= 0 ? 3.0 : value;
            if (Math.Abs(Definition.RansacReprojectionThreshold - normalized) < 0.000001)
            {
                return;
            }

            Definition.RansacReprojectionThreshold = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public double MaxReprojectionRmse
    {
        get => Definition.MaxReprojectionRmse <= 0 ? 4.0 : Definition.MaxReprojectionRmse;
        set
        {
            var normalized = value <= 0 ? 4.0 : value;
            if (Math.Abs(Definition.MaxReprojectionRmse - normalized) < 0.000001)
            {
                return;
            }

            Definition.MaxReprojectionRmse = normalized;
            MarkAlignmentParameterChanged(requiresTemplateRebuild: false);
            OnPropertyChanged();
        }
    }

    public string ParameterStatusText
    {
        get
        {
            if (RequiresTemplateRebuild || HasTemplateParameterMismatch)
            {
                return "需要重新创建模板";
            }

            return IsTemplateCreated ? "参数已保存" : "等待创建模板";
        }
    }

    public string StatusText
    {
        get
        {
            if (!Camera.IsEnabled)
            {
                return "停用";
            }

            if (!Camera.IsAcquisitionConfigured)
            {
                return "未配置";
            }

            if (!string.IsNullOrWhiteSpace(LastError))
            {
                return "失败";
            }

            if (RequiresTemplateRebuild || HasTemplateParameterMismatch)
            {
                return "需重建";
            }

            if (IsTemplateCreated)
            {
                return "已创建";
            }

            return HasReferenceImage ? "已拍照" : "未注册";
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (!Camera.IsEnabled)
            {
                return UiBrushes.TextMuted;
            }

            if (!Camera.IsAcquisitionConfigured)
            {
                return UiBrushes.Warning;
            }

            if (!string.IsNullOrWhiteSpace(LastError))
            {
                return UiBrushes.Danger;
            }

            if (RequiresTemplateRebuild || HasTemplateParameterMismatch)
            {
                return UiBrushes.Warning;
            }

            if (IsTemplateCreated)
            {
                return UiBrushes.Success;
            }

            return HasReferenceImage ? UiBrushes.Warning : UiBrushes.TextMuted;
        }
    }

    public bool HasReferenceImage => ReferenceImage != null || !string.IsNullOrWhiteSpace(Definition.ReferenceImageRelativePath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceImage))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private ImageSource? referenceImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private string lastError = string.Empty;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ParameterStatusText))]
    private bool requiresTemplateRebuild;

    public event EventHandler<AlignmentParameterChangedEventArgs>? AlignmentParameterChanged;

    public void RefreshMetadata()
    {
        OnPropertyChanged(nameof(ReferenceImagePathText));
        OnPropertyChanged(nameof(TemplatePathText));
        OnPropertyChanged(nameof(ImageSizeText));
        OnPropertyChanged(nameof(KeyPointText));
        OnPropertyChanged(nameof(DescriptorText));
        OnPropertyChanged(nameof(RegisteredAtText));
        OnPropertyChanged(nameof(EffectiveAlignmentRegionText));
        OnPropertyChanged(nameof(HasEffectiveAlignmentRegion));
        OnPropertyChanged(nameof(IsTemplateCreated));
        OnPropertyChanged(nameof(HasTemplateParameterMismatch));
        OnPropertyChanged(nameof(FeatureMethodDisplay));
        OnPropertyChanged(nameof(MaxLongSide));
        OnPropertyChanged(nameof(MaxFeatures));
        OnPropertyChanged(nameof(LoweRatio));
        OnPropertyChanged(nameof(MinGoodMatches));
        OnPropertyChanged(nameof(MinInliers));
        OnPropertyChanged(nameof(MinInlierRatio));
        OnPropertyChanged(nameof(RansacReprojectionThreshold));
        OnPropertyChanged(nameof(MaxReprojectionRmse));
        OnPropertyChanged(nameof(ParameterStatusText));
        OnPropertyChanged(nameof(HasReferenceImage));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public void MarkTemplateCreated()
    {
        Definition.RegisteredFeatureMethod = NormalizeFeatureMethod(Definition.FeatureMethod);
        Definition.RegisteredMaxLongSide = MaxLongSide;
        Definition.RegisteredMaxFeatures = MaxFeatures;
        Definition.RegisteredEffectiveAlignmentRegion = Definition.EffectiveAlignmentRegion?.Clone();
        RequiresTemplateRebuild = false;
        RefreshMetadata();
    }

    public bool SetEffectiveAlignmentRegion(double left, double top, double right, double bottom)
    {
        var normalized = AlignmentEffectiveRegion.NormalizeOrNull(
            new AlignmentEffectiveRegion
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            },
            Definition.ImageWidth,
            Definition.ImageHeight);
        if (normalized == null)
        {
            return false;
        }

        if (AlignmentEffectiveRegion.AreEquivalent(Definition.EffectiveAlignmentRegion, normalized))
        {
            return true;
        }

        Definition.EffectiveAlignmentRegion = normalized;
        MarkAlignmentParameterChanged(requiresTemplateRebuild: true);
        RefreshMetadata();
        return true;
    }

    public bool ClearEffectiveAlignmentRegion()
    {
        if (Definition.EffectiveAlignmentRegion == null)
        {
            return false;
        }

        Definition.EffectiveAlignmentRegion = null;
        MarkAlignmentParameterChanged(requiresTemplateRebuild: true);
        RefreshMetadata();
        return true;
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.Name))
        {
            OnPropertyChanged(nameof(CameraName));
        }

        if (e.PropertyName == nameof(CameraViewModel.IsEnabled) ||
            e.PropertyName == nameof(CameraViewModel.IsAcquisitionConfigured))
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    private void MarkAlignmentParameterChanged(bool requiresTemplateRebuild)
    {
        if (requiresTemplateRebuild && IsTemplateCreated)
        {
            RequiresTemplateRebuild = true;
        }

        AlignmentParameterChanged?.Invoke(this, new AlignmentParameterChangedEventArgs(requiresTemplateRebuild));
        OnPropertyChanged(nameof(ParameterStatusText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    private static string NormalizeFeatureMethod(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "AKAZE" => "Akaze",
            "ORB" => "Orb",
            "NCC+AKAZE" => "NCC+AKAZE",
            _ => "Sift"
        };
    }

    private static string ToFeatureMethodDisplay(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "NCC+AKAZE" => "NCC+AKAZE",
            _ => NormalizeFeatureMethod(value).ToUpperInvariant()
        };
    }
}

public sealed record AlignmentParameterChangedEventArgs(bool RequiresTemplateRebuild);
