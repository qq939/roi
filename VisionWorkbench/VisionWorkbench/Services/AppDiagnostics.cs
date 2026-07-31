using NewLife.Log;

namespace VisionWorkbench.Services;

public static class AppDiagnostics
{
    public static string LogPath => XTrace.LogPath ?? string.Empty;

    public static void Debug(string source, string message)
    {
        XTrace.WriteLine("[{0}] [DEBUG] {1}", source, message);
    }

    public static void Info(string source, string message)
    {
        XTrace.WriteLine("[{0}] {1}", source, message);
    }

    public static void Warn(string source, string message, Exception? exception = null)
    {
        XTrace.WriteLine("[{0}] [WARN] {1}", source, message);
        if (exception != null)
        {
            XTrace.WriteException(exception);
        }
    }

    public static void Error(string source, string message, Exception? exception = 
    null)
    {
        XTrace.WriteLine("[{0}] [ERROR] {1}", source, message);
        if (exception != null)
        {
            XTrace.WriteException(exception);
        }
    }
}
