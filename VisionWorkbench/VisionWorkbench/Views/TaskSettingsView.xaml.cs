using System.Windows.Controls;
using ImageBox;
using VisionWorkbench.ViewModels;

namespace VisionWorkbench.Views;

public partial class TaskSettingsView : UserControl
{
    public TaskSettingsView()
    {
        InitializeComponent();
    }

    private void ImageBox_RoiDrawCompleted(object? sender, RoiDrawCompletedEventArgs e)
    {
        if (DataContext is TaskSettingsViewModel viewModel)
        {
            viewModel.CompleteRoiDraw(e.Mode, e.Points);
        }
    }

    private void ImageBox_RoiDrawRejected(object? sender, RoiDrawRejectedEventArgs e)
    {
        if (DataContext is TaskSettingsViewModel viewModel)
        {
            viewModel.RejectRoiDraw(e.Message);
        }
    }

    private void ImageBox_OverlayItemSelected(object? sender, OverlayItemSelectedEventArgs e)
    {
        if (DataContext is TaskSettingsViewModel viewModel)
        {
            viewModel.SelectTaskById(e.Id);
        }
    }

    private void ImageBox_OverlayItemEditCompleted(object? sender, OverlayItemEditCompletedEventArgs e)
    {
        if (DataContext is TaskSettingsViewModel viewModel)
        {
            viewModel.CompleteRoiEdit(e.Id, e.X, e.Y, e.Width, e.Height, e.AngleDegrees);
        }
    }
}
