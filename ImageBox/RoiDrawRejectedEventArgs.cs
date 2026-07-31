namespace ImageBox;

public sealed class RoiDrawRejectedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
