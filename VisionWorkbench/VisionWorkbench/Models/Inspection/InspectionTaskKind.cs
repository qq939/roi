namespace VisionWorkbench.Models.Inspection;

public enum InspectionTaskKind
{
    Classification,
    Color,
    Measurement
}

public enum MeasurementEdgePolarity
{
    BlackToWhite,
    WhiteToBlack
}

public enum InspectionJudgment
{
    Unknown,
    OK,
    NG
}
