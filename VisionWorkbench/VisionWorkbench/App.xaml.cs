using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VisionWorkbench.Services;
using VisionWorkbench.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;

namespace VisionWorkbench;

public partial class App : Application
{
    private MainWindowViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private SecondaryBoardWindow? _secondaryBoardWindow;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDiagnostics.Info("app", "Application starting.");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        NativeCameraRuntimeInitializer.Initialize();
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _mainViewModel = new MainWindowViewModel();
        _mainViewModel.SecondaryBoardWorkspace.PropertyChanged += SecondaryBoardWorkspace_PropertyChanged;

        _mainWindow = new MainWindow(_mainViewModel);
        MainWindow = _mainWindow;
        _mainWindow.Closed += MainWindow_Closed;
        _mainWindow.Show();

        TouchInputManager.Initialize(_mainWindow);

        if (_mainViewModel.SecondaryBoardWorkspace.IsEnabled)
        {
            ShowSecondaryBoardWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppDiagnostics.Error("app", "Dispatcher unhandled exception.", e.Exception);
        if (ExceptionDisplayPolicy.IsBackgroundConnectionNoise(e.Exception))
        {
            e.Handled = true;
            return;
        }
        ReportExceptionToUi("界面异常", e.Exception);
        if (IsRecoverableCameraException(e.Exception))
        {
            e.Handled = true;
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppDiagnostics.Error("app", "Unobserved task exception.", e.Exception);
        ReportExceptionToUi("后台任务异常", e.Exception);
        e.SetObserved();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
                        ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal exception.");
        AppDiagnostics.Error("app", "AppDomain unhandled exception.", exception);
        ReportExceptionToUi("未处理异常", exception);
    }

    private void ReportExceptionToUi(string source, Exception exception)
    {
        if (ExceptionDisplayPolicy.IsBackgroundConnectionNoise(exception))
        {
            return;
        }

        void Report()
        {
            _mainViewModel?.ReportApplicationException(source, exception);
        }

        var dispatcher = Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Report();
            return;
        }

        _ = dispatcher.BeginInvoke(Report, DispatcherPriority.Send);
    }

    private static bool IsRecoverableCameraException(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsRecoverableCameraException);
        }

        var text = $"{exception.Source} {exception.Message} {exception.StackTrace}";
        return text.Contains("VideoInference.Camera", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CameraAcquisitionService", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("WindowsHikCameraProvider", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MV_E_", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Hik camera", StringComparison.OrdinalIgnoreCase);
    }

    private void SecondaryBoardWorkspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isShuttingDown || e.PropertyName != nameof(SecondaryBoardViewModel.IsEnabled))
        {
            return;
        }

        if (_mainViewModel?.SecondaryBoardWorkspace.IsEnabled == true)
        {
            ShowSecondaryBoardWindow();
        }
        else
        {
            CloseSecondaryBoardWindow();
        }
    }

    private void ShowSecondaryBoardWindow()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        if (_secondaryBoardWindow is { IsLoaded: true })
        {
            _secondaryBoardWindow.Activate();
            return;
        }

        _secondaryBoardWindow = new SecondaryBoardWindow
        {
            DataContext = _mainViewModel.SecondaryBoardWorkspace
        };
        _secondaryBoardWindow.Closed += SecondaryBoardWindow_Closed;
        PlaceSecondaryBoardWindow(_secondaryBoardWindow);
        _secondaryBoardWindow.Show();
    }

    private void PlaceSecondaryBoardWindow(SecondaryBoardWindow window)
    {
        var secondaryScreen = FormsScreen.AllScreens.FirstOrDefault(screen => !screen.Primary);
        if (secondaryScreen != null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.Left = secondaryScreen.Bounds.Left;
            window.Top = secondaryScreen.Bounds.Top;
            window.Width = secondaryScreen.Bounds.Width;
            window.Height = secondaryScreen.Bounds.Height;
            window.Topmost = false;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.ResizeMode = ResizeMode.CanResize;
        window.Width = 1280;
        window.Height = 720;
    }

    private void SecondaryBoardWindow_Closed(object? sender, EventArgs e)
    {
        if (_secondaryBoardWindow != null)
        {
            _secondaryBoardWindow.Closed -= SecondaryBoardWindow_Closed;
            _secondaryBoardWindow = null;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isShuttingDown = true;
        if (_mainWindow != null)
        {
            _mainWindow.Closed -= MainWindow_Closed;
            _mainWindow = null;
        }

        CloseSecondaryBoardWindow();
        if (_mainViewModel != null)
        {
            _mainViewModel.SecondaryBoardWorkspace.PropertyChanged -= SecondaryBoardWorkspace_PropertyChanged;
            _mainViewModel.Dispose();
            _mainViewModel = null;
        }

        Shutdown();
    }

    private void CloseSecondaryBoardWindow()
    {
        if (_secondaryBoardWindow == null)
        {
            return;
        }

        var window = _secondaryBoardWindow;
        _secondaryBoardWindow = null;
        window.Closed -= SecondaryBoardWindow_Closed;
        window.Close();
    }
}
