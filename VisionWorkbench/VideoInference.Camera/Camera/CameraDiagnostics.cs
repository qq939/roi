using NewLife.Log;

namespace VideoInferenceDemo;

public static class CameraDiagnostics
{
    public static string LogPath => XTrace.LogPath ?? string.Empty;

    public static void Info(string source, string message)
    {
        XTrace.WriteLine("[{0}] {1}", source, message);
    }

    public static void Warn(string source, string message)
    {
        XTrace.WriteLine("[{0}] [WARN] {1}", source, message);
    }

    public static void Error(string source, string message, Exception? exception = null)
    {
        XTrace.WriteLine("[{0}] {1}", source, message);
        if (exception != null)
            XTrace.WriteException(exception);
    }
}
