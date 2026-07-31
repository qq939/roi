using RoiAlignment.Core;

namespace RoiAlignment.Core.Tests;

public sealed class RoiGeometryTests
{
    [Fact]
    public void XywhaRoundTrip_KeepsCenterSizeAndAngle()
    {
        var original = new Xywha(120, 80, 60, 30, 15);

        var points = RoiGeometry.FromXywha(original);
        var actual = RoiGeometry.ToXywha(points);

        Assert.Equal(original.X, actual.X, 6);
        Assert.Equal(original.Y, actual.Y, 6);
        Assert.Equal(original.Width, actual.Width, 6);
        Assert.Equal(original.Height, actual.Height, 6);
        Assert.Equal(original.AngleDegrees, actual.AngleDegrees, 6);
    }

    [Fact]
    public void RoiShapeFromXywha_CreatesFourPointRoi()
    {
        var roi = RoiShape.FromXywha("检测区域1", new Xywha(10, 20, 30, 40, 0));

        Assert.Equal("检测区域1", roi.Name);
        Assert.Equal(RoiKind.RotatedRectangle, roi.Kind);
        Assert.Equal(4, roi.Points.Count);
    }
}
