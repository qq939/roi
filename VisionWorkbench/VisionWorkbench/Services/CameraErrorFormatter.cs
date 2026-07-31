namespace VisionWorkbench.Services;

public static class CameraErrorFormatter
{
    public static string ToUserMessage(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var innerMessages = aggregate
                .Flatten()
                .InnerExceptions
                .Select(ToUserMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            return innerMessages.Length == 0
                ? aggregate.Message
                : string.Join(Environment.NewLine, innerMessages);
        }

        var message = exception.Message;
        if (message.Contains("MV_E_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("0x80000203", StringComparison.OrdinalIgnoreCase))
        {
            return "相机访问被拒绝：设备可能正在被其他程序、上一次调试进程或另一个相机连接占用。请关闭占用相机的软件，确认上一次程序已退出，稍等几秒后重试。";
        }

        if (message.Contains("No Hik cameras were found", StringComparison.OrdinalIgnoreCase))
        {
            return "没有找到海康相机，请检查相机连接、网口/IP、驱动和供电。";
        }

        if (message.Contains("Camera capture returned no frame", StringComparison.OrdinalIgnoreCase))
        {
            return "相机取图失败：本次没有返回图像，请检查触发模式、曝光和相机连接。";
        }

        if (message.Contains("has no active camera session", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no active camera session", StringComparison.OrdinalIgnoreCase))
        {
            return "相机会话不存在，请先连接相机后再取图。";
        }

        return message;
    }
}
