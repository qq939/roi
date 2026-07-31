using System.Windows.Media;

namespace VisionWorkbench.Models;

public static class UiBrushes
{
    public static readonly Brush Success = Frozen("#16A34A");
    public static readonly Brush SuccessSoft = Frozen("#DCFCE7");
    public static readonly Brush Danger = Frozen("#DC2626");
    public static readonly Brush DangerSoft = Frozen("#FEE2E2");
    public static readonly Brush Warning = Frozen("#F59E0B");
    public static readonly Brush TextMuted = Frozen("#64748B");

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
