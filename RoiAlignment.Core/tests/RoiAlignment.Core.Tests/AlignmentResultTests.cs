using RoiAlignment.Core;

namespace RoiAlignment.Core.Tests;

public sealed class AlignmentResultTests
{
    [Fact]
    public void TransformRois_WithAffineTransform_TransformsAllPoints()
    {
        var result = new AlignmentResult
        {
            Success = true,
            TransformModel = TransformModel.AffinePartial,
            Transform = new TransformData
            {
                Model = TransformModel.AffinePartial,
                Rows = 2,
                Cols = 3,
                Values = [1, 0, 10, 0, 1, 20]
            }
        };
        var roi = RoiShape.FromXywha("roi", new Xywha(10, 10, 10, 10, 0));

        var transformed = result.TransformRois([roi]);

        Assert.Single(transformed);
        Assert.All(transformed[0].Points, point =>
        {
            Assert.True(point.X >= 15 && point.X <= 25);
            Assert.True(point.Y >= 25 && point.Y <= 35);
        });
    }

    [Fact]
    public void TransformRois_WhenFailed_Throws()
    {
        var result = new AlignmentResult
        {
            Success = false,
            FailureReason = AlignmentFailureReason.NotEnoughMatches
        };

        Assert.Throws<InvalidOperationException>(() => result.TransformRois([]));
    }
}
