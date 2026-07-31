using VisionWorkbench.Services;

namespace VisionWorkbench.ViewModels;

public sealed class InspectionResultRecordViewModel
{
    public InspectionResultRecordViewModel(InspectionResultRecord record)
    {
        Record = record;
    }

    public InspectionResultRecord Record { get; }

    public string DisplayTitle => $"{Record.OccurredAt.ToLocalTime():HH:mm:ss}  {Record.Result}  {Record.OkScore?.ToString("0.0000") ?? "--"}";

    public string DetailText =>
        $"NG {Record.NgScore?.ToString("0.0000") ?? "--"}  Margin {Record.Margin?.ToString("0.0000") ?? "--"}";

    public string ImagePath => Record.CropImagePath ?? string.Empty;
}
