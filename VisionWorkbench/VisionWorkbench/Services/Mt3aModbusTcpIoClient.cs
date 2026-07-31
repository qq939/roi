using System.IO;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using NModbus;

namespace VisionWorkbench.Services;

public enum Mt3aModbusDiReadMode
{
    Coils = 0x01,
    DiscreteInputs = 0x02
}

public sealed record Mt3aModbusTcpIoOptions
{
    public string Host { get; init; } = "192.168.1.12";
    public int Port { get; init; } = 502;

    public byte UnitId { get; init; } = 1;

    public ushort DiStartAddress { get; init; }

    public ushort DoStartAddress { get; init; }

    public int ChannelCount { get; init; } = 16;

    public Mt3aModbusDiReadMode DiReadMode { get; init; } = Mt3aModbusDiReadMode.DiscreteInputs;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    public bool WriteOutputsOnFirstConnect { get; init; }
}

public sealed class IoInputsChangedEventArgs : EventArgs
{
    public IoInputsChangedEventArgs(bool[] inputs)
    {
        Inputs = inputs;
    }

    public bool[] Inputs { get; }
}

public sealed class IoOutputsWrittenEventArgs : EventArgs
{
    public IoOutputsWrittenEventArgs(bool[] outputs)
    {
        Outputs = outputs;
    }

    public bool[] Outputs { get; }
}

public sealed class IoConnectionStateChangedEventArgs : EventArgs
{
    public IoConnectionStateChangedEventArgs(bool isConnected, string statusText)
    {
        IsConnected = isConnected;
        StatusText = statusText;
    }

    public bool IsConnected { get; }

    public string StatusText { get; }
}

public sealed partial class Mt3aModbusTcpIoClient : ObservableObject, IDisposable
{
    private readonly IModbusFactory _modbusFactory = new ModbusFactory();
    private Mt3aModbusTcpIoOptions _options;
    private readonly bool[] _lastInputs;
    private readonly bool[] _lastWrittenOutputs;
    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _disposed;
    private bool _forceWriteOutputs;
    private bool _hasWrittenOutputs;

    public Mt3aModbusTcpIoClient(Mt3aModbusTcpIoOptions? options = null)
    {
        _options = options ?? new Mt3aModbusTcpIoOptions();
        DI = new bool[_options.ChannelCount];
        DO = new bool[_options.ChannelCount];
        _lastInputs = new bool[_options.ChannelCount];
        _lastWrittenOutputs = new bool[_options.ChannelCount];
        _synchronizationContext = SynchronizationContext.Current;
        endpointText = $"{_options.Host}:{_options.Port}";
        statusText = $"{EndpointText} stopped";
    }

    public event EventHandler<IoInputsChangedEventArgs>? InputsChanged;

    public event EventHandler<IoOutputsWrittenEventArgs>? OutputsWritten;

    public event EventHandler<IoConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public bool[] DI { get; }

    public bool[] DO { get; }

    public string SettingsText =>
        $"UnitId={_options.UnitId}, DI={_options.DiReadMode}(0x{GetDiReadFunctionCode():X2}), DI Start={_options.DiStartAddress}, DO Start={_options.DoStartAddress}, Channels={_options.ChannelCount}, Timeout={_options.RequestTimeout.TotalMilliseconds:0}ms";

    [ObservableProperty]
    private string endpointText;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText;

    public void Start()
    {
        ThrowIfDisposed();

        if (_runTask != null)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCancellation.Token));
        _ = _runTask.ContinueWith(
            task =>
            {
                if (task.Exception == null)
                {
                    return;
                }

                AppDiagnostics.Error("io-module", "IO polling task faulted.", task.Exception);
                PublishConnectionState(false, $"{EndpointText} stopped after IO error");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task StopAsync()
    {
        var cancellation = _runCancellation;
        var task = _runTask;
        _runCancellation = null;
        _runTask = null;

        if (cancellation == null || task == null)
        {
            PublishConnectionState(false, $"{EndpointText} stopped");
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
            PublishConnectionState(false, $"{EndpointText} stopped");
        }
    }

    public void RequestWriteOutputs()
    {
        _forceWriteOutputs = true;
    }

    public async Task ReconfigureAsync(Mt3aModbusTcpIoOptions options)
    {
        ThrowIfDisposed();

        var wasRunning = _runTask != null;
        await StopAsync();
        _options = options;
        EndpointText = $"{_options.Host}:{_options.Port}";
        StatusText = $"{EndpointText} stopped";
        _forceWriteOutputs = true;
        OnPropertyChanged(nameof(SettingsText));

        if (wasRunning)
        {
            Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cancellation = _runCancellation;
        _runCancellation = null;
        _runTask = null;

        if (cancellation != null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connect = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!connect.Success)
            {
                PublishConnectionFault(connect.Message);
                await DelayBeforeReconnectAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var connection = connect.Value!;
            var connectedPublished = false;

            if (_options.WriteOutputsOnFirstConnect || _hasWrittenOutputs)
            {
                _forceWriteOutputs = true;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = TryReadInputs(connection.Master, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!read.Success)
                {
                    PublishConnectionFault(read.Message);
                    break;
                }

                PublishInputsIfChanged(read.Value!);

                var write = TryWriteOutputsIfChanged(connection.Master, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!write.Success)
                {
                    PublishConnectionFault(write.Message);
                    break;
                }

                if (!connectedPublished)
                {
                    PublishConnectionState(true, $"{EndpointText} connected");
                    connectedPublished = true;
                }

                if (!await TryDelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }

            await DelayBeforeReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IoOperationResult<ModbusIoConnection>> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return IoOperationResult<ModbusIoConnection>.Ok(await ConnectAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IoOperationResult<ModbusIoConnection>.Canceled();
        }
        catch (Exception ex)
        {
            return IoOperationResult<ModbusIoConnection>.Fail(FormatConnectionStatus(ex));
        }
    }

    private IoOperationResult<bool[]> TryReadInputs(
        IModbusMaster master,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IoOperationResult<bool[]>.Ok(ReadInputs(master));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IoOperationResult<bool[]>.Canceled();
        }
        catch (Exception ex)
        {
            return IoOperationResult<bool[]>.Fail(FormatConnectionStatus(ex));
        }
    }

    private IoOperationResult<bool> TryWriteOutputsIfChanged(
        IModbusMaster master,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteOutputsIfChanged(master);
            return IoOperationResult<bool>.Ok(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IoOperationResult<bool>.Canceled();
        }
        catch (Exception ex)
        {
            return IoOperationResult<bool>.Fail(FormatConnectionStatus(ex));
        }
    }

    private async Task DelayBeforeReconnectAsync(CancellationToken cancellationToken)
    {
        await TryDelayAsync(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void PublishConnectionFault(string status)
    {
        AppDiagnostics.Warn("io-module", $"IO module connection state. Endpoint={EndpointText}, Status={status}");
        PublishConnectionState(false, $"{EndpointText} {status}");
    }

    private static string FormatConnectionStatus(Exception exception)
    {
        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private async Task<ModbusIoConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = ToTimeoutMilliseconds(_options.RequestTimeout),
            SendTimeout = ToTimeoutMilliseconds(_options.RequestTimeout)
        };

        try
        {
            try
            {
                await client.ConnectAsync(_options.Host, _options.Port, cancellationToken)
                    .AsTask()
                    .WaitAsync(_options.ConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"连接超时：{EndpointText}，Timeout={_options.ConnectTimeout.TotalMilliseconds:0}ms", ex);
            }

            var master = _modbusFactory.CreateMaster(client);
            ConfigureTransport(master);
            return new ModbusIoConnection(client, master);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void ConfigureTransport(IModbusMaster master)
    {
        var timeout = ToTimeoutMilliseconds(_options.RequestTimeout);
        master.Transport.ReadTimeout = timeout;
        master.Transport.WriteTimeout = timeout;
        master.Transport.Retries = 0;
        master.Transport.WaitToRetryMilliseconds = 0;
        master.Transport.SlaveBusyUsesRetryCount = true;
    }

    private bool[] ReadInputs(IModbusMaster master)
    {
        var count = checked((ushort)_options.ChannelCount);
        return _options.DiReadMode == Mt3aModbusDiReadMode.Coils
            ? master.ReadCoils(_options.UnitId, _options.DiStartAddress, count)
            : master.ReadInputs(_options.UnitId, _options.DiStartAddress, count);
    }

    private void WriteOutputsIfChanged(IModbusMaster master)
    {
        var outputs = Snapshot(DO);
        if (!_forceWriteOutputs && outputs.SequenceEqual(_lastWrittenOutputs))
        {
            return;
        }

        master.WriteMultipleCoils(_options.UnitId, _options.DoStartAddress, outputs);
        Array.Copy(outputs, _lastWrittenOutputs, outputs.Length);
        _hasWrittenOutputs = true;
        _forceWriteOutputs = false;

        RunOnCapturedContext(() => OutputsWritten?.Invoke(this, new IoOutputsWrittenEventArgs(Snapshot(outputs))));
    }

    private byte GetDiReadFunctionCode()
    {
        return _options.DiReadMode == Mt3aModbusDiReadMode.Coils
            ? (byte)Mt3aModbusDiReadMode.Coils
            : (byte)Mt3aModbusDiReadMode.DiscreteInputs;
    }

    private void PublishInputsIfChanged(bool[] inputs)
    {
        if (inputs.SequenceEqual(_lastInputs))
        {
            return;
        }

        Array.Copy(inputs, DI, inputs.Length);
        Array.Copy(inputs, _lastInputs, inputs.Length);

        RunOnCapturedContext(() => InputsChanged?.Invoke(this, new IoInputsChangedEventArgs(Snapshot(inputs))));
    }

    private void PublishConnectionState(bool connected, string status)
    {
        RunOnCapturedContext(() =>
        {
            if (IsConnected == connected && string.Equals(StatusText, status, StringComparison.Ordinal))
            {
                return;
            }

            IsConnected = connected;
            StatusText = status;
            ConnectionStateChanged?.Invoke(this, new IoConnectionStateChangedEventArgs(connected, status));
        });
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

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        return (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
    }

    private static bool[] Snapshot(bool[] values)
    {
        var snapshot = new bool[values.Length];
        Array.Copy(values, snapshot, values.Length);
        return snapshot;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ModbusIoConnection : IDisposable
    {
        public ModbusIoConnection(TcpClient client, IModbusMaster master)
        {
            Client = client;
            Master = master;
        }

        private TcpClient Client { get; }

        public IModbusMaster Master { get; }

        public void Dispose()
        {
            if (Master is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Client.Dispose();
        }
    }

    private sealed class IoOperationResult<T>
    {
        private IoOperationResult(bool success, T? value, string message)
        {
            Success = success;
            Value = value;
            Message = message;
        }

        public bool Success { get; }

        public T? Value { get; }

        public string Message { get; }

        public static IoOperationResult<T> Ok(T value)
        {
            return new IoOperationResult<T>(true, value, string.Empty);
        }

        public static IoOperationResult<T> Fail(string message)
        {
            return new IoOperationResult<T>(false, default, message);
        }

        public static IoOperationResult<T> Canceled()
        {
            return new IoOperationResult<T>(false, default, "canceled");
        }
    }
}
