using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionWorkbench.Services;

public sealed class Mt3aModbusTcpIoConfiguration
{
    public int SchemaVersion { get; set; }

    public string Host { get; set; } = "192.168.1.12";

    public int Port { get; set; } = 502;

    public byte UnitId { get; set; } = 1;

    public ushort DiStartAddress { get; set; }

    public ushort DoStartAddress { get; set; }

    public int ChannelCount { get; set; } = 16;

    public Mt3aModbusDiReadMode DiReadMode { get; set; } = Mt3aModbusDiReadMode.DiscreteInputs;

    public int ConnectTimeoutMs { get; set; } = 2000;

    public int RequestTimeoutMs { get; set; } = 2000;

    public int PollIntervalMs { get; set; } = 100;

    public int ReconnectDelayMs { get; set; } = 2000;

    public bool WriteOutputsOnFirstConnect { get; set; }

    public Mt3aModbusTcpIoOptions ToOptions()
    {
        return new Mt3aModbusTcpIoOptions
        {
            Host = Host,
            Port = Port,
            UnitId = UnitId,
            DiStartAddress = DiStartAddress,
            DoStartAddress = DoStartAddress,
            ChannelCount = ChannelCount,
            DiReadMode = DiReadMode,
            ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs),
            RequestTimeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
            PollInterval = TimeSpan.FromMilliseconds(PollIntervalMs),
            ReconnectDelay = TimeSpan.FromMilliseconds(ReconnectDelayMs),
            WriteOutputsOnFirstConnect = WriteOutputsOnFirstConnect
        };
    }
}

public sealed class Mt3aModbusTcpIoConfigurationStorage
{
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Mt3aModbusTcpIoConfigurationStorage(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public Mt3aModbusTcpIoOptions LoadOrCreate()
    {
        return LoadOrCreateConfiguration().ToOptions();
    }

    public Mt3aModbusTcpIoConfiguration LoadOrCreateConfiguration()
    {
        var configuration = LoadConfiguration();
        var normalized = Normalize(configuration);
        Save(normalized);
        return normalized;
    }

    public Mt3aModbusTcpIoConfiguration SaveConfiguration(Mt3aModbusTcpIoConfiguration configuration)
    {
        var normalized = Normalize(configuration);
        Save(normalized);
        return normalized;
    }

    private Mt3aModbusTcpIoConfiguration LoadConfiguration()
    {
        if (!File.Exists(FilePath))
        {
            return new Mt3aModbusTcpIoConfiguration();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Mt3aModbusTcpIoConfiguration>(json, SerializerOptions)
                ?? new Mt3aModbusTcpIoConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Warn("io-config", $"IO configuration load failed. File={FilePath}, Error={ex.Message}");
            return new Mt3aModbusTcpIoConfiguration();
        }
    }

    private void Save(Mt3aModbusTcpIoConfiguration configuration)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(configuration, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Warn("io-config", $"IO configuration save failed. File={FilePath}, Error={ex.Message}");
        }
    }

    private static Mt3aModbusTcpIoConfiguration Normalize(Mt3aModbusTcpIoConfiguration configuration)
    {
        if (configuration.SchemaVersion <= 0 && configuration.DiReadMode == Mt3aModbusDiReadMode.Coils)
        {
            configuration.DiReadMode = Mt3aModbusDiReadMode.DiscreteInputs;
        }

        configuration.SchemaVersion = CurrentSchemaVersion;
        configuration.Host = string.IsNullOrWhiteSpace(configuration.Host)
            ? "192.168.1.12"
            : configuration.Host.Trim();
        configuration.Port = Math.Clamp(configuration.Port, 1, 65535);
        configuration.DiReadMode = Mt3aModbusDiReadMode.DiscreteInputs;
        configuration.DiStartAddress = 0;
        configuration.DoStartAddress = 0;
        configuration.ChannelCount = 16;

        configuration.ConnectTimeoutMs = Math.Clamp(configuration.ConnectTimeoutMs, 200, 30000);
        configuration.RequestTimeoutMs = Math.Clamp(configuration.RequestTimeoutMs, 200, 30000);
        configuration.PollIntervalMs = Math.Clamp(configuration.PollIntervalMs, 20, 60000);
        configuration.ReconnectDelayMs = Math.Clamp(configuration.ReconnectDelayMs, 200, 60000);
        return configuration;
    }
}
