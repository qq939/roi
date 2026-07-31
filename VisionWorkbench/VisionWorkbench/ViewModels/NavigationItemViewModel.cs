using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models;

namespace VisionWorkbench.ViewModels;

public sealed partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(
        NavigationItemKind kind,
        string title,
        string iconGlyph,
        CameraViewModel? camera = null,
        bool hasTopDivider = false)
    {
        Kind = kind;
        this.title = title;
        IconGlyph = iconGlyph;
        Camera = camera;
        HasTopDivider = hasTopDivider;
        BadgeText = camera?.Index.ToString() ?? string.Empty;
        if (camera != null)
        {
            camera.PropertyChanged += OnCameraPropertyChanged;
        }
    }

    public NavigationItemKind Kind { get; }

    public string IconGlyph { get; }

    public CameraViewModel? Camera { get; }

    public bool HasTopDivider { get; }

    public string BadgeText { get; }

    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string title = string.Empty;

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.Name) && Camera != null)
        {
            Title = Camera.Name;
        }
    }
}
