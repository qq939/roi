using System.Text;
using OpenCvSharp;
using RoiAlignment.Core;

namespace RoiAlignment.Core.Tests;

public sealed class TemplatePersistenceTests
{
    [Fact]
    public void AlignmentTemplate_CanSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.align.json");
        try
        {
            var template = new AlignmentTemplate
            {
                Name = "product-a",
                ImageWidth = 100,
                ImageHeight = 80,
                FeatureMethod = FeatureMethod.Sift,
                TransformModel = TransformModel.AffinePartial,
                KeyPoints =
                [
                    new KeyPointDto
                    {
                        X = 1,
                        Y = 2,
                        Size = 3,
                        Angle = 4,
                        Response = 5,
                        Octave = 6,
                        ClassId = 7
                    }
                ],
                Descriptors = new DescriptorData
                {
                    Rows = 1,
                    Cols = 4,
                    MatType = (int)MatType.CV_32FC1,
                    DataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("descriptor"))
                }
            };

            template.Save(path);
            var loaded = AlignmentTemplate.Load(path);

            Assert.Equal(template.Name, loaded.Name);
            Assert.Equal(template.ImageWidth, loaded.ImageWidth);
            Assert.Equal(template.KeyPoints.Count, loaded.KeyPoints.Count);
            Assert.False(loaded.Descriptors.IsEmpty);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
