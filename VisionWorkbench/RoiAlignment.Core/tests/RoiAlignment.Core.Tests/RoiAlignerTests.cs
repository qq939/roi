using OpenCvSharp;
using RoiAlignment.Core;

namespace RoiAlignment.Core.Tests;

public sealed class RoiAlignerTests
{
    [Fact]
    public void Align_WithTranslatedSyntheticImage_ReturnsTransformedRoi()
    {
        using var reference = CreateFeatureRichImage();
        using var runtime = new Mat(reference.Size(), reference.Type(), Scalar.Black);
        var transform = Mat.FromArray(new double[,]
        {
            { 1, 0, 12 },
            { 0, 1, 8 }
        });
        Cv2.WarpAffine(reference, runtime, transform, reference.Size());

        var template = AlignmentTemplateBuilder
            .FromImage(reference)
            .UseSift()
            .UseAffinePartial()
            .Build();
        var roi = RoiShape.FromXywha("target", new Xywha(120, 110, 40, 30, 0));
        var options = new AlignmentOptions
        {
            MinGoodMatches = 8,
            MinInliers = 6,
            MinInlierRatio = 0.25,
            MaxReprojectionRmse = 5
        };

        var result = new RoiAligner(options).AlignImage(template, runtime, [roi]);

        Assert.True(result.Success, result.FailureReason.ToString());
        Assert.NotEmpty(result.AlignedRois);
        var bounds = result.AlignedRois[0].ToXywha();
        Assert.InRange(bounds.X, 128, 136);
        Assert.InRange(bounds.Y, 114, 122);
    }

    [Theory]
    [InlineData(FeatureMethod.Sift)]
    [InlineData(FeatureMethod.Akaze)]
    [InlineData(FeatureMethod.Orb)]
    public void TemplateBuilder_CanSelectFeatureMethod(FeatureMethod method)
    {
        using var reference = CreateFeatureRichImage();

        var template = AlignmentTemplateBuilder
            .FromImage(reference)
            .UseFeatureMethod(method)
            .Build();

        Assert.Equal(method, template.FeatureMethod);
        Assert.False(template.IsEmpty);
    }

    [Fact]
    public void TemplateBuilder_RegistrationMask_OnlyExtractsFeaturesInsideMask()
    {
        using var reference = CreateFeatureRichImage();
        using var mask = new Mat(reference.Size(), MatType.CV_8UC1, Scalar.Black);
        var effectiveRegion = new Rect(75, 75, 115, 70);
        Cv2.Rectangle(mask, effectiveRegion, Scalar.White, -1);

        var template = AlignmentTemplateBuilder
            .FromImage(reference)
            .UseSift()
            .WithRegistrationMask(mask)
            .Build();

        Assert.NotEmpty(template.KeyPoints);
        Assert.All(template.KeyPoints, keyPoint =>
        {
            Assert.InRange(keyPoint.X, effectiveRegion.X, effectiveRegion.Right);
            Assert.InRange(keyPoint.Y, effectiveRegion.Y, effectiveRegion.Bottom);
        });
    }

    private static Mat CreateFeatureRichImage()
    {
        var image = new Mat(new Size(260, 220), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(30, 25, 70, 45), Scalar.White, 2);
        Cv2.Circle(image, new Point(170, 60), 28, Scalar.White, 2);
        Cv2.Line(image, new Point(40, 160), new Point(220, 150), Scalar.White, 3);
        Cv2.PutText(image, "ROI", new Point(90, 120), HersheyFonts.HersheySimplex, 1.2, Scalar.White, 2);
        Cv2.Circle(image, new Point(120, 110), 5, Scalar.White, -1);
        return image;
    }
}
