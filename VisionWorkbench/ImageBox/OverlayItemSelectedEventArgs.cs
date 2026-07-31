namespace ImageBox;

public sealed class OverlayItemSelectedEventArgs(string id) : EventArgs
{
    public string Id { get; } = id;
}
