using System.IO;
using System.IO.Ports;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionWorkbench.Services;

public sealed record BarcodeScannerSerialPortOptions
{
    public string PortName { get; init; } = "COM7";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    public Parity Parity { get; init; } = Parity.None;

    public StopBits StopBits { get; init; } = StopBits.One;

    public Handshake Handshake { get; init; } = Handshake.None;
}

public sealed class BarcodeScannedEventArgs : EventArgs
{
    public BarcodeScannedEventArgs(string barcode, string portName, DateTime receivedAt)
    {
        Barcode = barcode;
        PortName = portName;
        ReceivedAt = receivedAt;
    }

    public string Barcode { get; }

    public string PortName { get; }

    public DateTime ReceivedAt { get; }
}

public sealed partial class BarcodeScannerSerialPortService : ObservableObject, IDisposable
{
    private readonly object _gate = new();
    private readonly BarcodeScannerSerialPortOptions _options;
    private readonly StringBuilder _lineBuffer = new();
    private readonly SynchronizationContext? _synchronizationContext;
    private SerialPort? _serialPort;
    private bool _disposed;

    public BarcodeScannerSerialPortService(BarcodeScannerSerialPortOptions? options = null)
    {
        _options = options ?? new BarcodeScannerSerialPortOptions();
        _synchronizationContext = SynchronizationContext.Current;
        portName = _options.PortName;
        statusText = $"{_options.PortName} stopped";
        lastBarcode = string.Empty;
        lastBarcodeTimeText = "-";
        lastProductCode = string.Empty;
        lastProductCodeTimeText = "-";
    }

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    [ObservableProperty]
    private string portName;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private string lastBarcode;

    [ObservableProperty]
    private string lastBarcodeTimeText;

    [ObservableProperty]
    private string lastProductCode;

    [ObservableProperty]
    private string lastProductCodeTimeText;

    public static bool IsProductCodeBarcode(string? barcode)
    {
        return !string.IsNullOrWhiteSpace(barcode) && barcode.Trim().Length >= 15;
    }

    public void Start()
    {
        ThrowIfDisposed();

        AppDiagnostics.Debug("barcode", $"[START] Attempting to open port. CurrentState: IsOpen={_serialPort?.IsOpen}, Port={_options.PortName}");

        lock (_gate)
        {
            if (_serialPort?.IsOpen == true)
            {
                AppDiagnostics.Debug("barcode", $"[START] Port already open, skipping. Port={_options.PortName}");
                return;
            }
        }

        SerialPort? serialPort = null;
        try
        {
            serialPort = CreateSerialPort();
            AppDiagnostics.Debug("barcode", $"[START] SerialPort created. Port={_options.PortName}, BaudRate={_options.BaudRate}");
            serialPort.DataReceived += OnDataReceived;
            serialPort.Open();
            AppDiagnostics.Debug("barcode", $"[START] Port opened successfully. Port={_options.PortName}");

            lock (_gate)
            {
                _lineBuffer.Clear();
                _serialPort = serialPort;
            }

            PublishConnectionState(true, $"{_options.PortName} connected");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AppDiagnostics.Debug("barcode", $"[START] Port open failed. Port={_options.PortName}, Error={ex.GetType().Name}: {ex.Message}");
            if (serialPort != null)
            {
                serialPort.DataReceived -= OnDataReceived;
                serialPort.Dispose();
            }

            PublishConnectionState(false, $"{_options.PortName} unavailable: {ex.Message}");
        }
    }

    public void Stop()
    {
        SerialPort? serialPort;
        lock (_gate)
        {
            serialPort = _serialPort;
            _serialPort = null;
            _lineBuffer.Clear();
        }

        if (serialPort != null)
        {
            serialPort.DataReceived -= OnDataReceived;
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }

            serialPort.Dispose();
        }

        PublishConnectionState(false, $"{_options.PortName} stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private SerialPort CreateSerialPort()
    {
        return new SerialPort(_options.PortName, _options.BaudRate, _options.Parity, _options.DataBits, _options.StopBits)
        {
            Handshake = _options.Handshake,
            DtrEnable = true,
            RtsEnable = true,
            Encoding = Encoding.ASCII,
            NewLine = "\r\n",
            ReadTimeout = 500,
            WriteTimeout = 500
        };
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        AppDiagnostics.Debug("barcode", $"[DATA_RECEIVED] Thread={Environment.CurrentManagedThreadId}, Port={_options.PortName}");

        string receivedText;
        try
        {
            receivedText = ((SerialPort)sender).ReadExisting();
            AppDiagnostics.Debug("barcode", $"[DATA_READ] Length={receivedText?.Length ?? 0}, Data='{receivedText?.Replace("\r", "\\r").Replace("\n", "\\n")}'");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppDiagnostics.Debug("barcode", $"[DATA_ERROR] Port={_options.PortName}, Error={ex.GetType().Name}: {ex.Message}");
            PublishConnectionState(false, $"{_options.PortName} read failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(receivedText))
        {
            AppDiagnostics.Debug("barcode", $"[DATA_EMPTY] No data received, Port={_options.PortName}");
            return;
        }

        List<string> completedBarcodes = [];
        lock (_gate)
        {
            foreach (var character in receivedText)
            {
                if (character is '\r' or '\n')
                {
                    var barcode = _lineBuffer.ToString().Trim();
                    _lineBuffer.Clear();
                    if (!string.IsNullOrEmpty(barcode))
                    {
                        completedBarcodes.Add(barcode);
                        AppDiagnostics.Debug("barcode", $"[BARCODE_COMPLETE] Buffer='{barcode}', Length={barcode.Length}");
                    }

                    continue;
                }

                _lineBuffer.Append(character);
            }
            AppDiagnostics.Debug("barcode", $"[BUFFER_STATE] CompletedCount={completedBarcodes.Count}, BufferLength={_lineBuffer.Length}");
        }

        foreach (var barcode in completedBarcodes)
        {
            PublishBarcode(barcode);
        }
    }

    private void PublishBarcode(string barcode)
    {
        var receivedAt = DateTime.Now;
        AppDiagnostics.Debug("barcode", $"[PUBLISH_START] Barcode='{barcode}', IsProductCode={IsProductCodeBarcode(barcode)}, Port={_options.PortName}");

        RunOnCapturedContext(() =>
        {
            AppDiagnostics.Debug("barcode", $"[PUBLISH_CONTEXT] Setting LastBarcode='{barcode}', Thread={Environment.CurrentManagedThreadId}");
            LastBarcode = barcode;
            LastBarcodeTimeText = receivedAt.ToString("HH:mm:ss");
            if (IsProductCodeBarcode(barcode))
            {
                AppDiagnostics.Debug("barcode", $"[PUBLISH_PRODUCT_CODE] Setting LastProductCode='{barcode.Trim()}'");
                LastProductCode = barcode.Trim();
                LastProductCodeTimeText = LastBarcodeTimeText;
            }

            AppDiagnostics.Debug("barcode", $"[PUBLISH_INVOKE] Raising BarcodeScanned event. Barcode='{barcode}', Subscribers={BarcodeScanned?.GetInvocationList().Length ?? 0}");
            BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(barcode, _options.PortName, receivedAt));
            AppDiagnostics.Debug("barcode", $"[PUBLISH_END] Barcode='{barcode}' published successfully");
        });
    }

    private void PublishConnectionState(bool connected, string statusText)
    {
        RunOnCapturedContext(() =>
        {
            IsConnected = connected;
            StatusText = statusText;
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
