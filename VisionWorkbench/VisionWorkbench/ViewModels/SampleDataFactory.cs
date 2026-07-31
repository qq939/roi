using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionWorkbench.Models;

namespace VisionWorkbench.ViewModels;

internal static class SampleDataFactory
{
    public static ObservableCollection<CameraViewModel> CreateCameras()
    {
        var cameras = new ObservableCollection<CameraViewModel>();

        for (var i = 1; i <= 6; i++)
        {
            cameras.Add(new CameraViewModel(
                i,
                $"CAM {i:00}",
                i <= 3 ? "上料侧" : "出料侧",
                string.Empty,
                string.Empty,
                CreateEmptyFrame(),
                []));
        }

        return cameras;
    }

    public static ObservableCollection<InspectionResultRow> CreateResults(IEnumerable<CameraViewModel> cameras)
    {
        return [];
    }

    public static ObservableCollection<LogEntry> CreateLogs(IEnumerable<CameraViewModel> cameras)
    {
        return [];
    }

    private static ImageSource CreateEmptyFrame()
    {
        const int width = 1280;
        const int height = 900;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
