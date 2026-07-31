namespace VisionWorkbench.Models.Inspection;

public sealed class InspectionTaskDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "检测任务";

    public string ProductModelId { get; set; } = string.Empty;

    public string CameraId { get; set; } = string.Empty;

    public InspectionTaskKind Kind { get; set; } = InspectionTaskKind.Classification;

    public bool Enabled { get; set; } = true;

    public RoiRegion Roi { get; set; } = new();

    public ClipVectorSetDefinition? Clip { get; set; }

    public MeasurementOptions Measurement { get; set; } = new();

    public ClipVectorSetDefinition EnsureClipVectorSet()
    {
        Clip ??= new ClipVectorSetDefinition();
        Clip.ProductModelId = ProductModelId;
        Clip.CameraId = CameraId;
        Clip.TaskId = Id;
        Clip.DisplayName = string.IsNullOrWhiteSpace(Clip.DisplayName) ? Name : Clip.DisplayName;
        Clip.EnsureId();
        return Clip;
    }

    public MeasurementOptions EnsureMeasurementOptions()
    {
        Measurement ??= new MeasurementOptions();
        Measurement.Normalize();
        return Measurement;
    }
}

public sealed class MeasurementOptions
{
    public MeasurementEdgePolarity FirstEdgePolarity { get; set; } = MeasurementEdgePolarity.BlackToWhite;

    public MeasurementEdgePolarity SecondEdgePolarity { get; set; } = MeasurementEdgePolarity.WhiteToBlack;

    public double MinDistancePx { get; set; }

    public double MaxDistancePx { get; set; } = 100_000;

    public double PixelToMillimeterScale { get; set; } = 1;

    public double MinDistanceMm { get; set; }

    public double MaxDistanceMm { get; set; } = 100_000;

    public double EdgeThreshold { get; set; } = 5;

    public int SmoothWindow { get; set; } = 5;

    public double MinEdgeGapPx { get; set; } = 5;

    public void Normalize()
    {
        if (MaxDistancePx <= 0)
        {
            MaxDistancePx = 100_000;
        }

        if (MinDistancePx < 0)
        {
            MinDistancePx = 0;
        }

        if (MaxDistancePx < MinDistancePx)
        {
            MaxDistancePx = MinDistancePx;
        }

        if (PixelToMillimeterScale <= 0)
        {
            PixelToMillimeterScale = 1;
        }

        if (MaxDistanceMm <= 0)
        {
            MaxDistanceMm = 100_000;
        }

        if (MinDistanceMm < 0)
        {
            MinDistanceMm = 0;
        }

        if (MaxDistanceMm < MinDistanceMm)
        {
            MaxDistanceMm = MinDistanceMm;
        }

        MinDistancePx = MinDistanceMm / PixelToMillimeterScale;
        MaxDistancePx = MaxDistanceMm / PixelToMillimeterScale;

        if (EdgeThreshold <= 0)
        {
            EdgeThreshold = 5;
        }

        if (SmoothWindow < 1)
        {
            SmoothWindow = 1;
        }

        if (SmoothWindow % 2 == 0)
        {
            SmoothWindow++;
        }

        if (MinEdgeGapPx < 0)
        {
            MinEdgeGapPx = 0;
        }
    }
}
