using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using VisionWorkbench.ViewModels;

namespace VisionWorkbench;

public partial class MainWindow : Window
{
    private readonly bool _ownsDataContext;

    public MainWindow()
    {
        InitializeComponent();
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            DataContext = new MainWindowViewModel();
            _ownsDataContext = true;
        }
        Loaded += OnLoaded;
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SerialNumberInput.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
        {
            SerialNumberInput.Focus();
            Keyboard.Focus(SerialNumberInput);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_ownsDataContext && DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
