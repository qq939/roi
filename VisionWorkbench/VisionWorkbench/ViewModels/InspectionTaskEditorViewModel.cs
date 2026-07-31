using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.ViewModels;

public sealed partial class InspectionTaskEditorViewModel : ObservableObject
{
    public InspectionTaskEditorViewModel(InspectionTaskDefinition definition)
    {
        Definition = definition;
        name = definition.Name;
        kind = definition.Kind;
        enabled = definition.Enabled;
        roiX = definition.Roi.X;
        roiY = definition.Roi.Y;
        roiWidth = definition.Roi.Width;
        roiHeight = definition.Roi.Height;
        roiAngleDegrees = definition.Roi.AngleDegrees;
        classificationThreshold = definition.Kind == InspectionTaskKind.Classification
            ? definition.EnsureClipVectorSet().Threshold
            : ClipVectorSetDefinition.DefaultThreshold;
        var measurement = definition.EnsureMeasurementOptions();
        firstEdgePolarity = measurement.FirstEdgePolarity;
        secondEdgePolarity = measurement.SecondEdgePolarity;
        pixelToMillimeterScale = measurement.PixelToMillimeterScale;
        minDistanceMm = measurement.MinDistanceMm;
        maxDistanceMm = measurement.MaxDistanceMm;
        edgeThreshold = measurement.EdgeThreshold;
        smoothWindow = measurement.SmoothWindow;
        minEdgeGapPx = measurement.MinEdgeGapPx;
    }

    public InspectionTaskDefinition Definition { get; }

    public string Id => Definition.Id;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeasurementTask))]
    [NotifyPropertyChangedFor(nameof(IsClassificationTask))]
    private InspectionTaskKind kind;

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private double roiX;

    [ObservableProperty]
    private double roiY;

    [ObservableProperty]
    private double roiWidth;

    [ObservableProperty]
    private double roiHeight;

    [ObservableProperty]
    private double roiAngleDegrees;

    [ObservableProperty]
    private float classificationThreshold;

    public void RefreshThreshold()
    {
        if (Definition.Kind == InspectionTaskKind.Classification)
        {
            ClassificationThreshold = Definition.EnsureClipVectorSet().Threshold;
        }
    }

    [ObservableProperty]
    private MeasurementEdgePolarity firstEdgePolarity;

    [ObservableProperty]
    private MeasurementEdgePolarity secondEdgePolarity;

    [ObservableProperty]
    private double pixelToMillimeterScale;

    [ObservableProperty]
    private double minDistanceMm;

    [ObservableProperty]
    private double maxDistanceMm;

    [ObservableProperty]
    private double edgeThreshold;

    [ObservableProperty]
    private int smoothWindow;

    [ObservableProperty]
    private double minEdgeGapPx;

    public bool IsMeasurementTask => Kind == InspectionTaskKind.Measurement;

    public bool IsClassificationTask => Kind == InspectionTaskKind.Classification;

    [ObservableProperty]
    private InspectionJudgment previewResult = InspectionJudgment.Unknown;

    [ObservableProperty]
    private float? previewOkScore;

    [ObservableProperty]
    private float? previewNgScore;

    public void ClearPreviewResult()
    {
        PreviewResult = InspectionJudgment.Unknown;
        PreviewOkScore = null;
        PreviewNgScore = null;
    }

    partial void OnNameChanged(string value)
    {
        Definition.Name = value;
    }

    partial void OnKindChanged(InspectionTaskKind value)
    {
        Definition.Kind = value;
        if (value == InspectionTaskKind.Classification)
        {
            ClassificationThreshold = Definition.EnsureClipVectorSet().Threshold;
        }

        if (value == InspectionTaskKind.Measurement)
        {
            Definition.EnsureMeasurementOptions();
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        Definition.Enabled = value;
    }

    partial void OnRoiXChanged(double value)
    {
        Definition.Roi.X = value;
    }

    partial void OnRoiYChanged(double value)
    {
        Definition.Roi.Y = value;
    }

    partial void OnRoiWidthChanged(double value)
    {
        Definition.Roi.Width = value;
    }

    partial void OnRoiHeightChanged(double value)
    {
        Definition.Roi.Height = value;
    }

    partial void OnRoiAngleDegreesChanged(double value)
    {
        Definition.Roi.AngleDegrees = value;
    }

    partial void OnClassificationThresholdChanged(float value)
    {
        var normalized = ClipVectorSetDefinition.NormalizeThreshold(value);
        if (!normalized.Equals(value))
        {
            ClassificationThreshold = normalized;
            return;
        }

        Definition.EnsureClipVectorSet().Threshold = normalized;
    }

    partial void OnFirstEdgePolarityChanged(MeasurementEdgePolarity value)
    {
        Definition.EnsureMeasurementOptions().FirstEdgePolarity = value;
    }

    partial void OnSecondEdgePolarityChanged(MeasurementEdgePolarity value)
    {
        Definition.EnsureMeasurementOptions().SecondEdgePolarity = value;
    }

    partial void OnPixelToMillimeterScaleChanged(double value)
    {
        Definition.EnsureMeasurementOptions().PixelToMillimeterScale = value;
    }

    partial void OnMinDistanceMmChanged(double value)
    {
        Definition.EnsureMeasurementOptions().MinDistanceMm = value;
    }

    partial void OnMaxDistanceMmChanged(double value)
    {
        Definition.EnsureMeasurementOptions().MaxDistanceMm = value;
    }

    partial void OnEdgeThresholdChanged(double value)
    {
        Definition.EnsureMeasurementOptions().EdgeThreshold = value;
    }

    partial void OnSmoothWindowChanged(int value)
    {
        Definition.EnsureMeasurementOptions().SmoothWindow = value;
    }

    partial void OnMinEdgeGapPxChanged(double value)
    {
        Definition.EnsureMeasurementOptions().MinEdgeGapPx = value;
    }

    public void SetRoi(double x, double y, double width, double height, double angleDegrees = 0)
    {
        RoiX = x;
        RoiY = y;
        RoiWidth = width;
        RoiHeight = height;
        RoiAngleDegrees = angleDegrees;
    }

    public void ApplyMeasurementOptions(MeasurementOptions options)
    {
        options.Normalize();
        FirstEdgePolarity = options.FirstEdgePolarity;
        SecondEdgePolarity = options.SecondEdgePolarity;
        PixelToMillimeterScale = options.PixelToMillimeterScale;
        MinDistanceMm = options.MinDistanceMm;
        MaxDistanceMm = options.MaxDistanceMm;
        EdgeThreshold = options.EdgeThreshold;
        SmoothWindow = options.SmoothWindow;
        MinEdgeGapPx = options.MinEdgeGapPx;
    }
}
