using System;
using System.Collections.Generic;

namespace VideoInferenceDemo;

public sealed class CameraSopProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Strategy { get; set; } = "sop_rules";
    public string FingerprintModuleId { get; set; } = string.Empty;
    public List<CameraSopStep> Steps { get; set; } = new();
}

public sealed class CameraSopStep
{
    public int Step { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ActionCode { get; set; }
    public string? TcnLabel { get; set; }
    public string? ExpectedStateCode { get; set; }
}
