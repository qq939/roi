using System.Windows.Controls;
using System.Windows.Input;
using VisionWorkbench.ViewModels;

namespace VisionWorkbench.Views;

public partial class ClipTrainingLibraryView : UserControl
{
    public ClipTrainingLibraryView()
    {
        InitializeComponent();
    }

    private void SampleListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void TrainingImagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ClipTrainingLibraryViewModel viewModel ||
            sender is not ListBox listBox)
        {
            return;
        }

        viewModel.SetSelectedTrainingImages(
            listBox.SelectedItems.OfType<TrainingImageRecordViewModel>());
    }
}
