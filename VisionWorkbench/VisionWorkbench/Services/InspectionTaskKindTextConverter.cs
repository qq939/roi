using System.Globalization;
using System.Windows.Data;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class InspectionTaskKindTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            InspectionTaskKind.Classification => "分类",
            InspectionTaskKind.Measurement => "测量",
            InspectionTaskKind.Color => "颜色",
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
