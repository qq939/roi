using System.Globalization;
using System.Windows.Data;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class MeasurementEdgePolarityTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MeasurementEdgePolarity.BlackToWhite => "黑到白",
            MeasurementEdgePolarity.WhiteToBlack => "白到黑",
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
