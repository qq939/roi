# ROI 图像对齐 NuGet 包设计计划

## 1. 目标

设计并实现一个小而稳定、易于使用的 .NET NuGet 包，用于基于图像特征点的图像对齐和 ROI 变换。

这个包要覆盖下面这条完整流程：

1. 注册一张参考图像。
2. 从参考图中提取并保存稳定特征点。
3. 在参考图坐标系中定义 ROI。
4. 对实际运行图像进行特征匹配和图像对齐。
5. 将参考图上的 ROI 变换到实际图像上。
6. 返回对齐质量指标，让调用方知道本次对齐结果是否可信。

第一版应该优先做好纯算法核心包。WPF、画布控件、模板编辑器等 UI 能力后面再作为独立包补充。

## 2. 包拆分

### RoiAlignment.Core

核心算法包。

职责：

- 创建参考模板。
- 提取图像特征。
- 进行特征匹配。
- 估计图像变换矩阵。
- 变换 ROI。
- 校验对齐质量。
- 保存和加载模板。

这个包不应该依赖 WPF、WinForms、QuarkCanvas 或任何 UI 框架。

建议依赖：

- OpenCvSharp4。
- Windows 优先时可使用 OpenCvSharp4.Windows。
- 跨平台时使用 OpenCvSharp4 加对应 runtime 包。
- System.Text.Json。

### RoiAlignment.Wpf

可选的 WPF 适配包。

职责：

- 将 WPF 或画布中的 ROI 对象转换成 Core 包中的 ROI 模型。
- 将对齐后的 ROI 模型转换回 UI 对象。
- 根据需要提供可视化叠加层或查看器辅助能力。

这个包应该依赖 `RoiAlignment.Core`，Core 不反向依赖 WPF。

### RoiAlignment.Demo.Wpf

演示和验证程序。

职责：

- 注册参考图。
- 绘制注册区域 mask。
- 绘制 ROI。
- 保存和加载模板。
- 对实际图像进行对齐。
- 显示变换后的 ROI 和对齐质量指标。

Demo 项目不作为 NuGet 包的核心 API 暴露。

## 3. 核心设计原则

### ROI 始终保存在参考图坐标系

ROI 应该始终以参考图坐标系为准进行绘制、保存和管理。

运行时对齐完成后，再通过变换矩阵把这些 ROI 映射到当前实际图像坐标系。

### ROI 内部优先使用点集表达

内部不要只保存 `x, y, width, height, angle`。

推荐统一使用点集表达 ROI 几何：

- 水平矩形：四个角点。
- 旋转矩形：四个角点。
- 多边形：全部多边形点。

这样仿射变换和单应变换都能稳定处理，避免只变换中心点、宽高和角度时产生误差。

### 默认不要使用 Homography

包应该支持多种变换模型，但默认不建议使用 Homography。

推荐默认值：

- 默认使用部分仿射变换。
- 只有存在明显透视变化时，再使用 Homography。

支持的变换模型：

- `AffinePartial`：平移、旋转、等比例缩放。
- `Affine`：平移、旋转、缩放、剪切。
- `Homography`：透视变换。

### 对齐质量是一等输出

这个包不能只返回变换后的 ROI。它还必须告诉调用方本次对齐是否可靠。

每次对齐结果都应该包含：

- 是否成功。
- 失败原因。
- 原始匹配数量。
- 通过筛选的匹配数量。
- RANSAC 内点数量。
- 内点比例。
- 重投影 RMSE。
- 估计出的变换矩阵。
- 变换后的 ROI。

如果质量不达标，包应该返回失败结果，而不是静默输出一组可能错误的 ROI。

## 4. 公共 API 草案

### 推荐 API 形态

核心 API 应该同时支持两种使用方式：

- 解耦模式：对齐模板只负责特征和变换，ROI 列表由调用方传入。
- 托管模式：提供一个项目对象，把对齐模板和 ROI 一起保存、加载和使用。

这样既可以接入用户已有的 ROI 管理系统，也可以让简单项目直接使用我们提供的一站式模板。

### 解耦用法

适合调用方已经有自己的 ROI 管理、数据库、画布控件或配置系统。

```csharp
using RoiAlignment.Core;

// referenceMat 是注册时使用的参考图。
// AlignmentTemplate 只保存对齐相关内容，不强制管理 ROI。
var template = AlignmentTemplateBuilder
    .FromImage(referenceMat)
    // 使用 SIFT 提取参考图特征点和描述子。
    // 第一版建议默认用 SIFT，优先保证稳定性。
    .UseSift()
    // 使用部分仿射模型估计变换。
    // 适合平移、旋转、等比例缩放，不容易过拟合。
    .UseAffinePartial()
    // 可选：只在稳定区域提取特征。
    // maskMat 可以避开反光、文字变化、产品状态变化等不稳定区域。
    .WithRegistrationMask(maskMat)
    // Build 会提取参考图特征，并生成可持久化的对齐模板。
    .Build();

// 保存对齐模板。模板中包含参考图尺寸、特征点、描述子和算法配置。
// 不包含 ROI，ROI 可以由业务系统单独管理。
template.Save("product-a.align.json");

// 实际运行时加载对齐模板。
var loadedTemplate = AlignmentTemplate.Load("product-a.align.json");

// runtimeMat 是现场采集到的实际图。
// Align 只负责提取实际图特征、匹配参考特征、估计变换矩阵。
var result = RoiAligner.Align(loadedTemplate, runtimeMat);

if (!result.Success)
{
    // 对齐失败时，不应该继续使用 ROI。
    // FailureReason 会说明失败原因，例如匹配点不足、内点率过低、重投影误差过大等。
    Console.WriteLine(result.FailureReason);
    return;
}

// referenceRois 是调用方自己管理的 ROI 列表。
// 这些 ROI 必须在参考图坐标系中定义。
var alignedRois = result.TransformRois(referenceRois);

foreach (var roi in alignedRois)
{
    // alignedRois 是已经从参考图坐标系变换到实际图坐标系的 ROI。
    Console.WriteLine($"{roi.Name}: {roi.Bounds}");
}
```

### 便捷重载

适合调用方希望一次完成“对齐 + ROI 变换”，但仍然由调用方传入 ROI 列表。

```csharp
// 这个重载不会要求 template 内部持有 ROI。
// 它只是把 Align 和 TransformRois 两步合并为一步。
var result = RoiAligner.Align(loadedTemplate, runtimeMat, referenceRois);

if (result.Success)
{
    foreach (var roi in result.AlignedRois)
    {
        Console.WriteLine($"{roi.Name}: {roi.Bounds}");
    }
}
```

### 托管用法

适合 Demo、轻量项目或用户希望把参考图对齐数据和 ROI 一起保存的场景。

```csharp
var project = RoiAlignmentProjectBuilder
    .FromImage(referenceMat)
    .UseSift()
    .UseAffinePartial()
    .WithRegistrationMask(maskMat)
    // rois 是用户在参考图上绘制的 ROI。
    // 项目对象会一起管理和保存这些 ROI。
    .WithRois(referenceRois)
    .Build();

project.Save("product-a.align-project.json");

var loadedProject = RoiAlignmentProject.Load("product-a.align-project.json");
var result = RoiAligner.Align(loadedProject, runtimeMat);
```

### 可配置用法

```csharp
var options = new AlignmentOptions
{
    // 特征算法。SIFT 稳定性较好，AKAZE/ORB 可作为速度优先选项。
    FeatureMethod = FeatureMethod.Sift,

    // 变换模型。默认建议 AffinePartial。
    // 如果存在明显透视变化，可改为 Homography。
    TransformModel = TransformModel.AffinePartial,

    // Lowe ratio test 阈值。
    // 值越小，匹配越严格；值越大，保留的匹配更多但误匹配风险也更高。
    LoweRatio = 0.75,

    // 通过 ratio test 后的最少匹配数。
    // 低于该值时直接判定对齐失败。
    MinGoodMatches = 20,

    // RANSAC 估计变换后要求的最少内点数。
    MinInliers = 12,

    // 内点数量 / good matches。
    // 用于判断匹配是否具有稳定几何一致性。
    MinInlierRatio = 0.35,

    // 重投影均方根误差，单位是像素。
    // 超过该值说明估计出的变换矩阵质量较差。
    MaxReprojectionRmse = 4.0
};

// 使用自定义参数创建对齐器。
var aligner = new RoiAligner(options);

// 不传 ROI：只返回对齐质量和变换矩阵。
var result = aligner.Align(template, runtimeMat);

// 传 ROI：同时返回变换后的 ROI。
var resultWithRois = aligner.Align(template, runtimeMat, referenceRois);
```

### 四点和 XYWHA 互转

UI、模板存储和算法内部经常会在“四点表达”和 `x, y, width, height, angle` 之间转换，因此 Core 包应该提供标准转换方法。

建议约定：

- `X`、`Y` 表示旋转矩形中心点。
- `Width`、`Height` 表示矩形局部坐标系下的宽高。
- `AngleDegrees` 表示角度，单位为度。
- 角度方向需要在文档中明确，建议遵循 OpenCV 图像坐标系下的角度约定，或在 API 中明确使用顺时针角度。
- 四点顺序固定为：左上、右上、右下、左下。

```csharp
// 从 xywha 创建旋转矩形 ROI。
var xywha = new Xywha(
    x: 120.0,
    y: 80.0,
    width: 60.0,
    height: 30.0,
    angleDegrees: 15.0);

Point2fDto[] points = RoiGeometry.FromXywha(xywha);

// 从四点反算 xywha。
// 如果四点不是严格矩形，可以用最小面积外接矩形近似。
Xywha fitted = RoiGeometry.ToXywha(points);

var roi = RoiShape.FromXywha("检测区域1", xywha);
Xywha roiXywha = roi.ToXywha();
```

## 5. 数据模型草案

```csharp
/// <summary>
/// 对齐模板。
/// 保存参考图的特征数据和对齐配置，不强制保存 ROI。
/// 该对象可以序列化为 .align.json 文件。
/// </summary>
public sealed class AlignmentTemplate
{
    /// <summary>
    /// 模板名称，例如产品型号、工位名称或用户自定义名称。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 注册参考图宽度，单位为像素。
    /// 用于校验 ROI 和特征点是否属于同一参考图坐标系。
    /// </summary>
    public int ImageWidth { get; init; }

    /// <summary>
    /// 注册参考图高度，单位为像素。
    /// </summary>
    public int ImageHeight { get; init; }

    /// <summary>
    /// 模板使用的特征算法。
    /// 运行时对齐应使用兼容的特征算法和匹配器。
    /// </summary>
    public FeatureMethod FeatureMethod { get; init; }

    /// <summary>
    /// 默认使用的变换模型。
    /// </summary>
    public TransformModel TransformModel { get; init; }

    /// <summary>
    /// 参考图中的特征点。
    /// </summary>
    public IReadOnlyList<KeyPointDto> KeyPoints { get; init; } = [];

    /// <summary>
    /// 参考图特征描述子。
    /// SIFT 通常是 float 描述子，ORB/AKAZE 通常是 byte 描述子。
    /// </summary>
    public DescriptorData Descriptors { get; init; } = DescriptorData.Empty;

    /// <summary>
    /// 模板元数据，例如创建时间、备注、软件版本、相机信息等。
    /// </summary>
    public TemplateMetadata Metadata { get; init; } = new();
}

/// <summary>
/// 对齐项目。
/// 用于一站式保存对齐模板和 ROI，适合简单项目或 Demo。
/// 核心对齐算法不依赖该对象。
/// </summary>
public sealed class RoiAlignmentProject
{
    /// <summary>
    /// 对齐模板，负责特征匹配和变换矩阵估计。
    /// </summary>
    public AlignmentTemplate Template { get; init; } = new();

    /// <summary>
    /// 在参考图坐标系中定义的 ROI。
    /// </summary>
    public IReadOnlyList<RoiShape> Rois { get; init; } = [];
}

/// <summary>
/// 单次图像对齐结果。
/// 包含是否成功、质量指标、变换矩阵和对齐后的 ROI。
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>
    /// 对齐是否成功。
    /// false 时调用方不应继续使用 AlignedRois。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 对齐失败原因。
    /// Success 为 true 时应为 None。
    /// </summary>
    public AlignmentFailureReason FailureReason { get; init; }

    /// <summary>
    /// 本次对齐实际使用的变换模型。
    /// </summary>
    public TransformModel TransformModel { get; init; }

    /// <summary>
    /// 估计出的变换矩阵。
    /// AffinePartial/Affine 通常为 2x3，Homography 为 3x3。
    /// </summary>
    public TransformData? Transform { get; init; }

    /// <summary>
    /// 匹配器返回的原始匹配数量。
    /// </summary>
    public int RawMatches { get; init; }

    /// <summary>
    /// 通过 Lowe ratio test 等过滤后的匹配数量。
    /// </summary>
    public int GoodMatches { get; init; }

    /// <summary>
    /// RANSAC 判断为几何一致的内点数量。
    /// </summary>
    public int Inliers { get; init; }

    /// <summary>
    /// 内点比例，通常等于 Inliers / GoodMatches。
    /// </summary>
    public double InlierRatio { get; init; }

    /// <summary>
    /// 重投影 RMSE，单位为像素。
    /// 数值越小，说明变换矩阵越能解释匹配点关系。
    /// </summary>
    public double ReprojectionRmse { get; init; }

    /// <summary>
    /// 综合置信度，可由内点数、内点率、RMSE 等指标计算得到。
    /// 第一版可以先保留字段，后续再调优计算公式。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 已经变换到实际图坐标系的 ROI。
    /// 只有调用 Align(template, image, rois) 或 Align(project, image) 时才会自动填充。
    /// 只有 Success 为 true 时才应该使用。
    /// </summary>
    public IReadOnlyList<RoiShape> AlignedRois { get; init; } = [];

    /// <summary>
    /// 将参考图坐标系中的 ROI 变换到实际图坐标系。
    /// 适合 ROI 由调用方自行管理的解耦场景。
    /// </summary>
    public IReadOnlyList<RoiShape> TransformRois(
        IReadOnlyList<RoiShape> referenceRois);
}

/// <summary>
/// ROI 几何定义。
/// 内部统一使用点集表达，便于支持仿射和透视变换。
/// </summary>
public sealed class RoiShape
{
    /// <summary>
    /// ROI 名称，例如 "定位孔"、"检测区域1"。
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// ROI 类型，例如矩形、旋转矩形、多边形等。
    /// </summary>
    public RoiKind Kind { get; init; }

    /// <summary>
    /// ROI 点集。
    /// 矩形和旋转矩形建议保存四个角点，多边形保存全部顶点。
    /// </summary>
    public IReadOnlyList<Point2fDto> Points { get; init; } = [];

    /// <summary>
    /// 业务标签，可用于保存检测类型、工位信息、阈值配置索引等扩展信息。
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// 从 xywha 创建旋转矩形 ROI。
    /// </summary>
    public static RoiShape FromXywha(string name, Xywha xywha);

    /// <summary>
    /// 将当前 ROI 转换为 xywha。
    /// 对于非矩形 ROI，可以返回最小面积外接矩形。
    /// </summary>
    public Xywha ToXywha();
}

/// <summary>
/// 旋转矩形参数表达。
/// X/Y 表示中心点，Width/Height 表示宽高，AngleDegrees 表示角度。
/// </summary>
public readonly record struct Xywha(
    double X,
    double Y,
    double Width,
    double Height,
    double AngleDegrees);

/// <summary>
/// ROI 几何工具。
/// 负责四点、xywha、OpenCV RotatedRect 等几何表达之间的转换。
/// </summary>
public static class RoiGeometry
{
    /// <summary>
    /// 将 xywha 转换为四个角点。
    /// 默认点顺序为左上、右上、右下、左下。
    /// </summary>
    public static Point2fDto[] FromXywha(Xywha xywha);

    /// <summary>
    /// 将四个角点转换为 xywha。
    /// 如果点不是严格矩形，可以使用最小面积外接矩形进行拟合。
    /// </summary>
    public static Xywha ToXywha(IReadOnlyList<Point2fDto> points);
}
```

## 6. 特征算法选择

### SIFT

推荐作为默认算法，优先保证精度和鲁棒性。

优点：

- 匹配质量较好。
- 对尺度变化和旋转变化比较稳定。
- 在很多工业图像中表现可靠。

缺点：

- 速度比二进制描述子慢。
- 描述子数据体积更大。

推荐匹配器：

- BFMatcher + L2。
- 特征数量较多时可以考虑 FLANN。

### AKAZE

适合作为速度和稳定性之间的折中选择。

优点：

- 通常比 SIFT 快。
- 二进制描述子更紧凑。
- 在一些工业场景中效果不错。

缺点：

- 在复杂纹理、光照变化、尺度变化明显的情况下通常不如 SIFT 稳定。

推荐匹配器：

- BFMatcher + Hamming。

### ORB

适合作为速度优先选项。

优点：

- 快。
- 描述子体积小。

缺点：

- 对尺度、光照和透视变化的鲁棒性相对较弱。

推荐匹配器：

- BFMatcher + Hamming。

## 7. 匹配流程

第一版推荐实现流程：

1. 从实际运行图中提取特征点和描述子。
2. 使用 KNN 进行匹配，`k = 2`。
3. 使用 Lowe ratio test 过滤弱匹配。
4. 可选增加双向匹配校验。
5. 使用 RANSAC 估计变换矩阵。
6. 只保留 RANSAC 内点。
7. 计算重投影 RMSE。
8. 根据阈值校验对齐结果。
9. 变换 ROI。

建议默认阈值：

```csharp
MinGoodMatches = 20
MinInliers = 12
MinInlierRatio = 0.35
MaxReprojectionRmse = 4.0
LoweRatio = 0.75
RansacReprojectionThreshold = 3.0
```

这些阈值必须允许调用方配置。

## 8. 变换矩阵估计

### AffinePartial

使用 `Cv2.EstimateAffinePartial2D`。

适合场景：

- 平移。
- 旋转。
- 等比例缩放。
- 相机和工件变化较小。

建议作为默认变换模型。

### Affine

使用 `Cv2.EstimateAffine2D`。

适合场景：

- 更灵活的二维变形。
- 轻微剪切。
- 非等比例缩放。

当 `AffinePartial` 约束太强时可以使用。

### Homography

使用 `Cv2.FindHomography`。

适合场景：

- 平面物体存在明显透视变化。
- 相机视角变化较明显。

需要谨慎使用，因为它自由度更高。在匹配点质量较差时，Homography 可能给出看似合理但实际错误的结果。

## 9. ROI 变换

变换方式：

- `AffinePartial` 和 `Affine`：使用 `Cv2.Transform`。
- `Homography`：使用 `Cv2.PerspectiveTransform`。

ROI 变换后需要做校验：

- ROI 点不能出现 NaN 或 Infinity。
- ROI 面积不能塌缩到接近 0。
- ROI 不能大面积超出实际图像范围，除非调用方显式允许。
- 多边形点顺序应该保持一致。

## 10. 模板持久化

模板和项目文件第一版建议使用 JSON。

推荐文件扩展名：

```text
.align.json
.align-project.json
```

`.align.json` 表示纯对齐模板，应包含：

- 包 schema 版本。
- 参考图尺寸。
- 特征算法。
- 变换模型。
- 特征点。
- 描述子。
- 可选元数据。

`.align-project.json` 表示托管项目，应包含：

- 包 schema 版本。
- 一个 `AlignmentTemplate`。
- ROI 定义。
- 可选项目元数据。

第一版可以把描述子数组用 Base64 存到 JSON 中。如果后续文件体积成为问题，再增加二进制模板格式。

从第一版开始就应该加入 schema version：

```json
{
  "schemaVersion": 1,
  "name": "product-a",
  "kind": "alignment-template",
  "imageWidth": 2448,
  "imageHeight": 2048
}
```

## 11. 错误处理

建议使用明确的失败原因，而不是只返回 `false`。

建议失败原因：

```csharp
public enum AlignmentFailureReason
{
    None,
    EmptyTemplate,
    NoRuntimeFeatures,
    NotEnoughMatches,
    TransformEstimationFailed,
    NotEnoughInliers,
    InlierRatioTooLow,
    ReprojectionErrorTooHigh,
    TransformOutOfRange,
    RoiTransformInvalid
}
```

## 12. 建议项目结构

```text
RoiAlignment.Core/
  src/
    RoiAlignment.Core/
      Alignment/
      Features/
      Matching/
      Models/
      Persistence/
      Roi/
      Validation/
    RoiAlignment.Wpf/
    RoiAlignment.Demo.Wpf/
  tests/
    RoiAlignment.Core.Tests/
  docs/
    roi-alignment-nuget-plan.md
  README.md
```

## 13. 第一阶段里程碑

第一阶段目标是做出一个可用的核心 NuGet 包。

范围：

- 创建 `RoiAlignment.Core` 项目。
- 添加 OpenCvSharp 依赖。
- 实现 `AlignmentTemplateBuilder`。
- 实现 SIFT 特征提取。
- 实现 KNN 匹配和 Lowe ratio test。
- 实现部分仿射变换估计。
- 实现四点矩形 ROI 变换。
- 实现 `Align(template, image)` 和 `Align(template, image, rois)` 重载。
- 实现 `RoiGeometry.FromXywha` 和 `RoiGeometry.ToXywha`。
- 实现对齐质量指标。
- 实现 JSON 保存和加载。
- 为 ROI 变换、xywha 互转和结果校验增加单元测试。
- 增加一个控制台示例或测试示例，用两张图完成对齐。

第一阶段暂不做：

- WPF UI。
- 模板编辑器。
- Homography 参数调试界面。
- ECC 精修。
- 深度学习匹配。
- 多参考模板选择。

## 14. 第二阶段里程碑

范围：

- 增加 AKAZE 和 ORB。
- 增加 `Affine` 和 `Homography`。
- 增加多边形 ROI 支持。
- 增加注册区域 mask 支持。
- 增加变换范围校验。
- 增加更多诊断信息。
- 增加 Demo 程序。

## 15. 第三阶段里程碑

范围：

- 增加 `RoiAlignment.Wpf`。
- 增加 QuarkCanvas 或自定义画布适配。
- 增加可视化调试叠加层：
  - 匹配点，
  - RANSAC 内点，
  - 变换后的 ROI，
  - 对齐置信度。
- 增加模板编辑工作流。

## 16. 未来可选增强

- 特征对齐后增加 ECC 精修。
- 多参考模板和最佳模板选择。
- 支持 ArUco 或 AprilTag 标记辅助定位。
- 增加二进制模板格式。
- 增加图像预处理配置：
  - 灰度化，
  - CLAHE，
  - 模糊，
  - 阈值化，
  - 边缘增强匹配。
- 增加批量对齐 API。
- 增加调试图像导出。

## 17. 推荐产品定位

这个包可以定位为：

> 一个基于 .NET 和 OpenCvSharp 的轻量级 ROI 图像对齐工具包，用于在参考图上注册 ROI，并在实际图像中可靠地变换 ROI，同时提供可解释的对齐质量诊断。

它的核心价值不只是封装特征匹配，而是把下面这条完整链路做简单、做稳定：

- 参考图。
- 稳定注册特征。
- ROI 保存。
- 运行时图像对齐。
- ROI 变换。
- 可解释的成功或失败判断。
