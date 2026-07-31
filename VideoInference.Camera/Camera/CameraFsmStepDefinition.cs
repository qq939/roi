namespace VideoInferenceDemo;

public sealed class CameraFsmStepDefinition
{
    public int Step { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ActionCode { get; set; }
    public string? TcnLabel { get; set; }
}
