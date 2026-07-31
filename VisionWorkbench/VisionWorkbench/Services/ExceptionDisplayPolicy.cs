using System.IO;
using System.Net.Sockets;

namespace VisionWorkbench.Services;

public static class ExceptionDisplayPolicy
{
    public static bool IsBackgroundConnectionNoise(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsBackgroundConnectionNoise);
        }

        if (exception is IOException or SocketException or ObjectDisposedException)
        {
            return IsTransportAbortMessage(exception.Message) ||
                   exception is SocketException or ObjectDisposedException;
        }

        return IsTransportAbortMessage(exception.Message);
    }

    private static bool IsTransportAbortMessage(string message)
    {
        return message.Contains("transport connection", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("I/O operation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("thread exit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("application request", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("远程主机", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("线程退出", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("应用程序请求", StringComparison.OrdinalIgnoreCase);
    }
}
