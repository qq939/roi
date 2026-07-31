using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionWorkbench.Services;

public sealed record ProductionLoopOptions
{
    public int CycleSleepMs { get; init; } = 50;

    public int TriggerInputIndex { get; init; }
}

public sealed partial class ProductionLoopService : ObservableObject, IDisposable
{
    private readonly Mt3aModbusTcpIoClient _ioModule;
    private readonly Func<CancellationToken, Task> _runInspectionAsync;
    private readonly Action<string, string> _log;
    private readonly Func<bool> _isInspectionEnabled;
    private readonly ProductionLoopOptions _options;
    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private bool _lastTriggerInput;
    private bool _isExecutingInspection;
    private bool _disposed;

    public ProductionLoopService(
        Mt3aModbusTcpIoClient ioModule,
        Func<CancellationToken, Task> runInspectionAsync,
        Action<string, string> log,
        Func<bool>? isInspectionEnabled = null,
        ProductionLoopOptions? options = null)
    {
        _ioModule = ioModule;
        _runInspectionAsync = runInspectionAsync;
        _log = log;
        _isInspectionEnabled = isInspectionEnabled ?? (() => true);
        _options = options ?? new ProductionLoopOptions();
        _synchronizationContext = SynchronizationContext.Current;
        statusText = "Stopped";
    }

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private long cycleCount;

    [ObservableProperty]
    private string statusText;

    public void Start()
    {
        ThrowIfDisposed();
        if (_loopTask != null)
        {
            AppDiagnostics.Info("production-loop", "Start ignored because loop task already exists.");
            return;
        }

        _cancellation = new CancellationTokenSource();
        PublishState(isRunning: true, statusText: "Running");
        AppDiagnostics.Info("production-loop", "Starting production loop task.");
        _loopTask = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var task = _loopTask;
        _cancellation = null;
        _loopTask = null;
        AppDiagnostics.Info("production-loop", "Stopping production loop task.");

        if (cancellation == null || task == null)
        {
            PublishState(isRunning: false, statusText: "Stopped");
            return;
        }

        await cancellation.CancelAsync();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            PublishState(isRunning: false, statusText: "Stopped");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cancellation = _cancellation;
        _cancellation = null;
        _loopTask = null;
        if (cancellation != null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        PublishLog("INFO", $"生产循环已启动，周期 {_options.CycleSleepMs} ms");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PublishCycleCount(CycleCount + 1);
                await ExecuteCycleAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_options.CycleSleepMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishStatus(ex.Message);
                AppDiagnostics.Error("production-loop", $"Production loop error: {ex.Message}", ex);
                PublishLog("WARN", $"生产循环异常：{ex.Message}");
                await Task.Delay(_options.CycleSleepMs, cancellationToken).ConfigureAwait(false);
            }
        }

        PublishLog("INFO", "生产循环已停止");
    }

    private async Task ExecuteCycleAsync(CancellationToken cancellationToken)
    {
        var triggerInput = ReadInput(_options.TriggerInputIndex);
        var triggerRisingEdge = triggerInput && !_lastTriggerInput;
        _lastTriggerInput = triggerInput;

        if (!_isInspectionEnabled())
        {
            return;
        }

        if (!triggerRisingEdge || _isExecutingInspection)
        {
            return;
        }

        _isExecutingInspection = true;
        AppDiagnostics.Info("production-loop", $"DI trigger rising edge. InputIndex={_options.TriggerInputIndex}");
        PublishState(isBusy: true, statusText: $"Triggered by DI{_options.TriggerInputIndex + 1:00}");
        try
        {
            await RunInspectionOnCapturedContextAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isExecutingInspection = false;
            PublishState(isBusy: false, statusText: _isInspectionEnabled() ? "Running" : "Stopped");
        }
    }

    private bool ReadInput(int index)
    {
        return index >= 0 && index < _ioModule.DI.Length && _ioModule.DI[index];
    }

    private Task RunInspectionOnCapturedContextAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationContext == null || SynchronizationContext.Current == _synchronizationContext)
        {
            return _runInspectionAsync(cancellationToken);
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizationContext.Post(
            async _ =>
            {
                try
                {
                    await _runInspectionAsync(cancellationToken);
                    completion.SetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.SetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            null);
        return completion.Task;
    }

    private void PublishState(bool? isRunning = null, bool? isBusy = null, string? statusText = null)
    {
        RunOnCapturedContext(() =>
        {
            if (isRunning.HasValue)
            {
                IsRunning = isRunning.Value;
            }

            if (isBusy.HasValue)
            {
                IsBusy = isBusy.Value;
            }

            if (statusText != null)
            {
                StatusText = statusText;
            }
        });
    }

    private void PublishStatus(string statusText)
    {
        RunOnCapturedContext(() => StatusText = statusText);
    }

    private void PublishCycleCount(long cycleCount)
    {
        RunOnCapturedContext(() => CycleCount = cycleCount);
    }

    private void PublishLog(string level, string message)
    {
        RunOnCapturedContext(() => _log(level, message));
    }

    private void RunOnCapturedContext(Action action)
    {
        if (_synchronizationContext == null || SynchronizationContext.Current == _synchronizationContext)
        {
            action();
            return;
        }

        _synchronizationContext.Post(_ => action(), null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
