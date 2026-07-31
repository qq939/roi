using System.Windows.Media;
using VisionWorkbench.Models;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.ViewModels;

public sealed class ClipTrainingSampleViewModel
{
    public ClipTrainingSampleViewModel(ClipTrainingSampleInfo info)
    {
        Info = info;
    }

    public ClipTrainingSampleInfo Info { get; }

    public string FileName => Info.FileName;

    public string Source => Info.Source;

    public string CreatedAtText => Info.CreatedAtText;

    public string ImagePath => Info.ImagePath;

    public bool Enabled => Info.Enabled;

    public string StateText => Enabled ? Info.Label.ToString() : "忽略";

    public Brush StateBrush => !Enabled
        ? UiBrushes.TextMuted
        : Info.Label == InspectionJudgment.OK
            ? UiBrushes.Success
            : UiBrushes.Danger;

    public Brush StateBackground => !Enabled
        ? Frozen("#E2E8F0")
        : Info.Label == InspectionJudgment.OK
            ? UiBrushes.SuccessSoft
            : UiBrushes.DangerSoft;

    public string DetailText => string.IsNullOrWhiteSpace(CreatedAtText)
        ? $"{StateText}  {Source}"
        : $"{StateText}  {CreatedAtText}  {Source}";

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
