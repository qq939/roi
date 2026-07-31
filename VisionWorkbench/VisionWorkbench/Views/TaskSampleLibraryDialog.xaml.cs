using System.Windows;
using VisionWorkbench.ViewModels;

namespace VisionWorkbench.Views;

public partial class TaskSampleLibraryDialog : Window
{
    public TaskSampleLibraryDialog(TaskSampleLibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskSampleLibraryViewModel viewModel &&
            viewModel.RefreshCommand.CanExecute(null))
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
