# RoiAlignment.Core

RoiAlignment.Core is a .NET/OpenCvSharp toolkit for feature-based image alignment and ROI transformation.

First milestone scope:

- Build an alignment template from a reference image.
- Extract SIFT features.
- Match runtime images against the reference template.
- Estimate a partial affine transform with RANSAC.
- Transform ROIs from reference image coordinates to runtime image coordinates.
- Convert ROI geometry between four corner points and `xywha`.
- Save and load alignment templates as JSON.

## Basic Usage

```csharp
using OpenCvSharp;
using RoiAlignment.Core;

using var reference = Cv2.ImRead("reference.png");
using var runtime = Cv2.ImRead("runtime.png");

var template = AlignmentTemplateBuilder
    .FromImage(reference)
    .UseSift()
    .UseAffinePartial()
    .Build();

template.Save("product-a.align.json");

var rois = new[]
{
    RoiShape.FromXywha("target", new Xywha(120, 80, 60, 30, 0))
};

var result = RoiAligner.Align(template, runtime, rois);
if (!result.Success)
{
    Console.WriteLine(result.FailureReason);
    return;
}

foreach (var roi in result.AlignedRois)
{
    Console.WriteLine(roi.ToXywha());
}
```

## Notes

The current implementation focuses on `SIFT + AffinePartial`. More feature methods and transform models can be added behind the existing enums and result model.

## WPF Demo

Run the demo:

```powershell
dotnet run --project .\samples\RoiAlignment.Demo.Wpf\RoiAlignment.Demo.Wpf.csproj
```

Demo workflow:

1. Load a reference image.
2. Drag on the left image to draw one or more ROIs.
3. Load a runtime image.
4. Click align.
5. View transformed ROIs on the right image and check match metrics at the bottom.
