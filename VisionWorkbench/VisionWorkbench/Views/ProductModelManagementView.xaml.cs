using System.Windows.Controls;
using ImageBox;
using VisionWorkbench.ViewModels;

namespace VisionWorkbench.Views;

public partial class ProductModelManagementView : UserControl
{
    public ProductModelManagementView()
    {
        InitializeComponent();
    }

    private void TemplateImageBox_RoiDrawCompleted(object? sender, RoiDrawCompletedEventArgs e)
    {
        if (DataContext is ProductModelManagementViewModel viewModel &&
            e.Mode == ImageBoxInteractionMode.DrawRectangle)
        {
            viewModel.CompleteEffectiveAlignmentRegionDraw(e.Points);
        }
    }

    private void TemplateImageBox_RoiDrawRejected(object? sender, RoiDrawRejectedEventArgs e)
    {
        if (DataContext is ProductModelManagementViewModel viewModel)
        {
            viewModel.RejectEffectiveAlignmentRegionDraw(e.Message);
        }
    }

    private void TemplateImageBox_OverlayItemSelected(object? sender, OverlayItemSelectedEventArgs e)
    {
        if (DataContext is ProductModelManagementViewModel viewModel)
        {
            viewModel.SelectEffectiveAlignmentRegion(e.Id);
        }
    }

    private void TemplateImageBox_OverlayItemEditCompleted(object? sender, OverlayItemEditCompletedEventArgs e)
    {
        if (DataContext is ProductModelManagementViewModel viewModel)
        {
            viewModel.CompleteEffectiveAlignmentRegionEdit(e.Id, e.X, e.Y, e.Width, e.Height);
        }
    }
}
