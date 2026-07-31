# 任务配置页面方案

## 1. 目标

任务配置页用于维护“产品型号 + 相机 + 任务”的检测配置。型号管理页用于维护产品型号以及该型号下每个相机的对齐模板资产。两者分工是：型号管理注册模板，任务配置消费模板并维护 ROI 和任务。

任务配置页保持三栏结构：

- 左栏：产品型号选择 + 相机卡片列表。
- 中栏：当前选中相机的图像预览和 ROI 编辑区域。
- 右栏：该产品、该相机下的图像对齐配置和任务列表。

核心原则：

- 所有任务配置绑定到产品型号。
- 创建产品型号时，应注册该型号下各相机的对齐模板。
- 相机只负责取图和展示当前配置上下文，不直接拥有产品任务。
- ROI 存储在参考图坐标系中；运行时如启用对齐，先做图像对齐，再把 ROI 变换到运行图坐标系。
- 页面不做复杂向导，不堆过多状态文本，只保留必要的选择、按钮、列表和参数。

## 2. 页面布局

建议布局：

```text
┌──────────────────┬──────────────────────────────┬──────────────────────────┐
│ 产品型号 ComboBox │                              │ 图像对齐                 │
│                  │                              │ - 启用                   │
│ 相机卡片列表      │                              │ - 模板/注册入口           │
│ ┌──────────────┐ │                              │ - 匹配参数               │
│ │ 相机1         │ │                              │                          │
│ │ 拍照  读图    │ │        ImageBox              │ 任务列表                 │
│ └──────────────┘ │  当前相机图片 + ROI Overlay   │ - ROI + 类型             │
│ ┌──────────────┐ │                              │ - 新增/删除/复制          │
│ │ 相机2         │ │                              │                          │
│ │ 拍照  读图    │ │                              │ 任务参数                 │
│ └──────────────┘ │                              │ - 分类/颜色/测量          │
└──────────────────┴──────────────────────────────┴──────────────────────────┘
```

推荐宽度：

- 左栏：240-260。
- 中栏：自适应，占主要空间。
- 右栏：360-400。

### 左栏

左栏顶部放产品型号下拉框：

- `SelectedProductModel` 改变后，中栏仍显示当前相机图片。
- 右侧任务列表切换为该产品 + 当前相机的配置。
- 对齐模板也按产品 + 相机读取。

相机列表每项是卡片：

- 显示相机名称，例如 `相机1`、`上料左侧`。
- 显示一个很轻的连接状态点即可，不额外加大段文字。
- 按钮：`拍照`、`读图`。
- 只有当前选中卡片的按钮可操作；未选中卡片按钮禁用或不显示。
- 选中卡片使用左侧色条或浅蓝背景突出。

操作建议：

- 点击卡片：选中相机，中栏切换到该相机当前图像。
- 拍照：调用相机服务取图，更新该相机当前帧。
- 读图：从本地文件读取一张图片，作为该相机当前帧，便于离线配置。

### 中栏

中栏只放一个主要 `ImageBox`：

- 绑定 `SelectedTaskCamera.Frame`。
- 没有图片时保持全黑。
- 显示当前产品 + 当前相机的 ROI overlay。
- 支持选择、绘制、编辑 ROI。

不建议在中栏放太多工具条。必要工具可以放在 ImageBox 上方一行：

- 选择。
- 新建矩形 ROI。
- 适配窗口。
- 删除选中 ROI。

ROI 表现：

- 普通任务 ROI：按任务类型区分描边颜色。
- 选中任务 ROI：加粗或高亮。
- 对齐后的 ROI 预览：使用实线。
- 参考 ROI 或未对齐 ROI：使用较淡颜色。

### 右栏

右栏分两块。

第一块：图像对齐。

字段建议：

- 启用对齐：`bool Enabled`。
- 模板：显示当前模板名称或文件。
- 注册/维护：一个按钮，先保留入口。
- 特征方法：默认 `SIFT`。
- 最少匹配点：默认 20。
- 最少内点：默认 12。
- 最大 RMSE：默认 4.0。

模板图像注册和维护位置：

- 第一阶段先放在右栏“图像对齐”区域里，一个按钮打开后续弹窗或独立页面。
- 不建议现在就在任务配置页里做完整模板管理，否则这个页面会过重。
- 模板维护本身应按产品 + 相机保存，不按任务保存。

第二块：任务配置。

任务列表字段：

- 名称。
- 类型：分类、颜色、测量。
- ROI 名称或编号。
- 启用。

任务参数区随任务类型切换：

- 分类：绑定 CLIP 向量集。
- 颜色：HSV 阈值和判定规则。
- 测量：1D 测量方向、滤波、极值筛选规则。

## 3. 产品绑定关系

推荐配置层级：

```text
InspectionWorkspaceConfiguration
  ProductModels[]
  ProductConfigurations[]
    ProductModelId
    CameraConfigurations[]
      CameraId
      Alignment
      Tasks[]
```

也可以继续沿用当前扁平 `Tasks` 列表，但需要保证查询统一走：

```csharp
GetTasks(productModelId, cameraId)
```

推荐逐步演进：

第一阶段保留当前 `InspectionWorkspaceConfiguration.Tasks` 扁平结构，新增对齐配置列表：

```csharp
List<CameraAlignmentDefinition> Alignments
```

第二阶段如果配置明显变多，再整理为产品下挂相机配置。

## 4. ROI 坐标模型

当前 `RoiRegion` 只有 `X/Y/Width/Height`，不足以支持自由矩形和对齐。建议扩展为旋转矩形：

```csharp
public sealed class RoiRegion
{
    public string Id { get; set; }
    public string Name { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double AngleDegrees { get; set; }
}
```

存储约定：

- 任务 ROI 永远存参考图坐标。
- 对齐结果产生运行图坐标 ROI，但不回写配置。
- UI 可以同时显示参考 ROI 和对齐后的 ROI，但保存时只保存参考 ROI。

与 `ImageBox` 的关系：

- `RoiRegion` 转 `ImageOverlayItem` 用于显示。
- `ImageBox` 现有 `RotatedRectangle` overlay 可用于显示旋转矩形。
- 当前 `ImageBox` 只有画普通矩形和多边形事件；自由旋转矩形编辑建议单独做一个轻量交互层，不要放进任务业务模型。

## 5. 图像对齐方案

`RoiAlignment.Core` 当前比较适合作为独立算法库直接引用：

- 它是 `net8.0` class library。
- 无 WPF UI 依赖。
- API 边界是模板、特征、变换、ROI 坐标转换。
- WPF demo 只作为参考，不应该被主工程引用。

建议引用方式：

```xml
<ProjectReference Include="..\RoiAlignment.Core\src\RoiAlignment.Core\RoiAlignment.Core.csproj" />
```

但在引用前建议做一次依赖版本整理：

- `VisionWorkbench` / `VideoInference.Camera` 当前使用 `OpenCvSharp4 4.11.0.20250507`。
- `RoiAlignment.Core` 当前使用 `OpenCvSharp4 4.10.0.20241108`。
- 建议把 `RoiAlignment.Core` 也升级到 `4.11.0.20250507`。
- 更推荐 core 只引用 `OpenCvSharp4`，runtime 包由最终 WPF 程序统一引用，避免算法库携带平台 runtime。

对齐执行流程：

```text
运行图 Mat
  -> 读取产品+相机的 AlignmentTemplate
  -> RoiAligner.Align(template, runtimeMat, referenceRois)
  -> 得到 Transform + AlignedRois
  -> 每个任务使用 AlignedRoi 裁图/测量/分类
```

对齐配置建议：

```csharp
public sealed class CameraAlignmentDefinition
{
    public string ProductModelId { get; set; }
    public string CameraId { get; set; }
    public bool Enabled { get; set; }
    public string TemplatePath { get; set; }
    public string FeatureMethod { get; set; } = "Sift";
    public int MinGoodMatches { get; set; } = 20;
    public int MinInliers { get; set; } = 12;
    public double MinInlierRatio { get; set; } = 0.35;
    public double MaxReprojectionRmse { get; set; } = 4.0;
}
```

模板入口位置：

- 任务配置页只保留 `打开型号管理` 入口。
- 型号管理页负责加载/捕获参考图、提取描述符、生成模板，保存到产品 + 相机路径。
- 后续再增加注册 mask、模板预览、历史版本。

### 5.1 型号管理

模板图像不是任务本身，也不是相机硬件参数。它是“产品型号 + 相机”的对齐基准资产。

因此这块建议命名为“型号管理”，而不是“模板维护”。创建产品型号时，就应该为该型号注册每个相机的对齐模板：

```text
ProductModel
  -> Camera 1 ReferenceImage + AlignmentTemplate
  -> Camera 2 ReferenceImage + AlignmentTemplate
  -> ...
  -> Camera N ReferenceImage + AlignmentTemplate
```

任务配置页只消费型号管理里注册好的模板，不负责完整维护模板资产。

推荐 UI 放置方式：

1. 左侧主导航增加 `型号管理`。
2. 任务配置页右栏的“图像对齐”区域可以放一个 `打开型号管理` 的小入口。
3. 创建或编辑产品型号时，在型号管理页完成每个相机的模板注册。
4. 任务配置页只显示“当前型号 + 当前相机”的模板状态，并基于模板图像维护 ROI。

型号管理页建议三栏或两栏：

```text
┌────────────────────┬──────────────────────────────┬──────────────────────────┐
│ 型号列表            │ 模板图像 ImageBox             │ 模板/描述符数据           │
│ - 新建型号          │ - 当前相机参考图              │ - 产品型号                │
│ - 复制型号          │ - 可显示关键点/注册区域        │ - 当前相机                │
│ - 删除型号          │                              │ - 图片尺寸                │
│                    │                              │ - 特征方法                │
│ 快速操作            │                              │ - 关键点数量              │
│ - 拍照              │                              │ - 描述符维度              │
│ - 创建              │                              │ - 描述符行数              │
│ - 全部清除          │                              │ - 模板路径                │
│                    │                              │                          │
│ 相机模板卡片列表     │                              │ 当前相机操作              │
│ - 相机1             │                              │ - 拍照注册                │
│ - 相机2             │                              │ - 读图注册                │
│ - 相机N             │                              │ - 重新提取                │
└────────────────────┴──────────────────────────────┴──────────────────────────┘
```

第一版为了不做重，可以采用：

- 左栏：型号下拉或型号列表 + 快速操作 Panel + 相机模板卡片。
- 中栏：选中相机的注册模板图像。
- 右栏：选中相机的模板信息和描述符数据。

### 5.2 型号创建流程

创建产品型号时建议进入一个明确流程：

```text
新建型号
  -> 输入型号名称/编码
  -> 为所有启用相机准备模板图像
  -> 逐个或批量提取描述符
  -> 保存型号配置
  -> 进入任务配置页维护 ROI 和任务
```

是否强制所有相机都注册模板，可以做成配置：

- 严格模式：创建型号必须注册全部启用相机模板。
- 宽松模式：允许部分相机未注册，但任务配置和运行时提示该相机不可用。

第一版建议用宽松模式，界面上清晰标记“已注册/未注册”。这样现场调试时不被流程卡死。

### 5.3 快速操作 Panel

型号管理页需要一个快速操作 Panel，用于批量处理当前型号下的所有启用相机。

按钮建议：

- `拍照`：依次触发所有启用相机拍照，更新各相机的待注册图像。
- `创建`：对所有已有待注册图像的相机提取描述符，生成模板文件。
- `全部清除`：清除当前型号下所有相机的待注册图像和模板引用。

按钮行为：

```text
拍照
  -> 遍历启用相机
  -> CaptureAsync
  -> 保存 reference.png
  -> 更新相机卡片缩略图和待创建状态

创建
  -> 遍历有 reference.png 的相机
  -> AlignmentTemplateBuilder.FromImage
  -> 提取 SIFT 描述符
  -> 保存 template.align.json
  -> 更新关键点数量、描述符行数、注册时间

全部清除
  -> 清除当前型号的所有 camera alignment 配置
  -> 可选择是否删除磁盘图片和模板文件
```

`全部清除` 建议第一版只清配置引用和状态，磁盘文件移动到 `Trash` 或保留，避免误删现场数据。后面再补“清理无引用资产”工具。

快速操作需要有整体进度，但不需要复杂日志。保留：

- 当前处理相机。
- 成功数量。
- 失败数量。

### 5.4 相机模板卡片

型号管理页左侧相机模板列表每个项是卡片：

- 相机名称。
- 注册状态。
- 缩略图。
- 关键点数量。
- 注册时间。

选中某个相机后：

- 中间 ImageBox 显示该相机的注册模板图像。
- 右侧显示模板和描述符数据。
- 右侧按钮只操作当前相机。

右侧模板和描述符数据建议显示：

- 参考图路径。
- 模板路径。
- 图像宽高。
- 特征方法。
- TransformModel。
- KeyPointCount。
- Descriptor Rows。
- Descriptor Cols。
- Descriptor MatType。
- DataBase64 长度，不直接显示完整 Base64。
- RegisteredAt。

完整描述符不建议在 UI 中展开，数据很长且没有人工编辑价值。需要时提供“打开文件位置”或“复制摘要”。

### 5.5 模板注册能力

- 从当前相机拍照注册。
- 从本地图片注册。
- 提取 SIFT 描述符。
- 保存参考图和 `.align.json`。
- 显示关键点数量、图像尺寸、创建时间。
- 支持删除/覆盖当前模板。

暂时不做：

- 多模板版本管理。
- 模板质量评分的复杂页面。
- 复杂 mask 编辑。
- 复杂批量注册策略。

### 5.6 存储 Root 和相对路径

图像和数据的存储应该设置一个统一 Root，然后配置里只存相对目录，方便后期整体迁移、备份和管理。

全局参数建议：

```csharp
public sealed class VisionStorageOptions
{
    public string RootDirectory { get; set; } = "RuntimeData";
}
```

型号配置里保存相对路径：

```text
{RootDirectory}/
  Products/
    {ProductModelId}/
      {CameraId}/
        alignment/
          reference.png
          template.align.json
          preview.png
```

主配置 JSON 中不要保存绝对路径，只保存相对路径和参数：

```csharp
public sealed class CameraAlignmentDefinition
{
    public string ProductModelId { get; set; }
    public string CameraId { get; set; }
    public bool Enabled { get; set; }
    public string ReferenceImageRelativePath { get; set; }
    public string TemplateRelativePath { get; set; }
    public string PreviewRelativePath { get; set; }
    public FeatureMethod FeatureMethod { get; set; } = FeatureMethod.Sift;
    public TransformModel TransformModel { get; set; } = TransformModel.AffinePartial;
    public int KeyPointCount { get; set; }
    public int DescriptorRows { get; set; }
    public int DescriptorCols { get; set; }
    public int DescriptorMatType { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}
```

路径解析统一放在服务中：

```csharp
public interface IVisionAssetPathService
{
    string GetFullPath(string relativePath);
    string GetProductCameraDirectory(string productModelId, string cameraId);
    string GetRelativePath(string fullPath);
}
```

运行时不重新从参考图提取描述符，直接加载 `.align.json`：

```text
加载产品型号
  -> 加载当前相机 CameraAlignmentDefinition
  -> TemplatePath = RootDirectory + TemplateRelativePath
  -> AlignmentTemplate.Load(TemplatePath)
  -> RoiAligner.Align(template, runtimeImage, referenceRois)
```

### 5.7 型号管理边界

- `ProductModelManagementViewModel`：负责型号列表、相机模板卡片、快速操作命令。
- `AlignmentTemplateService`：负责拍照/读图后生成模板、保存参考图、保存 `.align.json`。
- `VisionAssetPathService`：负责 Root 和相对路径转换。
- `RoiAlignment.Core`：只负责提取特征和序列化模板。
- `TaskSettingsViewModel`：只读取当前产品 + 相机的模板状态，并消费对齐结果。

这样型号管理不会污染任务列表，也不会让每个任务都背一份对齐模板。

## 6. 三类任务

### 6.1 分类任务：CLIP

职责：

- 使用 ROI 裁剪后的图像做 CLIP 推理。
- 向量集绑定产品 + 相机 + 任务。

配置：

```csharp
public sealed class ClipTaskParameters
{
    public string VectorSetId { get; set; }
    public int TopK { get; set; } = 5;
    public double Threshold { get; set; } = 0.15;
}
```

运行：

```text
AlignedRoi -> 裁图 -> 保存临时图 -> ClipClassificationService.ClassifyAsync
```

### 6.2 颜色任务：HSV 阈值

职责：

- 在 ROI 内转 HSV。
- 使用阈值生成 mask。
- 根据像素比例、面积或均值判断 OK/NG。

配置：

```csharp
public sealed class HsvColorTaskParameters
{
    public int HueMin { get; set; }
    public int HueMax { get; set; } = 179;
    public int SaturationMin { get; set; }
    public int SaturationMax { get; set; } = 255;
    public int ValueMin { get; set; }
    public int ValueMax { get; set; } = 255;
    public double MinRatio { get; set; }
    public double MaxRatio { get; set; } = 1.0;
}
```

运行：

```text
AlignedRoi -> 旋转裁剪成 patch -> BGR2HSV -> InRange -> ratio -> OK/NG
```

第一阶段不必做复杂形态学，只保留可选开闭运算参数位。

### 6.3 测量任务：1D 极值坐标

用户描述的“根据 ROI 的方向像素平均后找出的极值坐标”可以落成 1D 测量：

1. 按旋转 ROI 裁剪为标准 patch。
2. 沿 ROI 的短边方向做像素平均，得到沿长边的一维灰度曲线。
3. 对一维曲线做平滑。
4. 计算差分或梯度。
5. 按极性、阈值、最小距离筛选峰值。
6. 选择第 N 个、最强峰、最左/最右峰。
7. 把一维坐标映射回原图坐标。

配置：

```csharp
public enum MeasurementDirection
{
    AlongRoiWidth,
    AlongRoiHeight
}

public enum EdgePolarity
{
    BrightToDark,
    DarkToBright,
    Both
}

public sealed class Measurement1DTaskParameters
{
    public MeasurementDirection Direction { get; set; } = MeasurementDirection.AlongRoiWidth;
    public EdgePolarity Polarity { get; set; } = EdgePolarity.Both;
    public int SmoothWindow { get; set; } = 5;
    public double MinGradient { get; set; } = 10;
    public int MinDistancePixels { get; set; } = 5;
    public int PeakIndex { get; set; } = 0;
}
```

输出：

- 测量坐标：`Point2d`。
- 一维位置：像素坐标。
- 峰值强度。
- OK/NG 可后续加上下限。

第一阶段只做“找极值坐标”，不要马上做复杂几何尺寸链。

## 7. 服务边界

建议新增这些服务，保持 UI、算法、配置分开：

```text
TaskSettingsView / TaskSettingsViewModel
  -> IInspectionConfigurationStorage
  -> ITaskImageService
  -> IRoiOverlayService
  -> IAlignmentService
  -> ITaskExecutionService
```

### UI/ViewModel

负责：

- 当前产品。
- 当前相机。
- 当前图片。
- 当前任务。
- 按钮状态。
- 调用服务。

不负责：

- OpenCV 细节。
- CLIP 推理。
- ROI 坐标变换细节。

### IAlignmentService

包装 `RoiAlignment.Core`：

```csharp
public interface IAlignmentService
{
    AlignmentExecutionResult Align(
        CameraAlignmentDefinition definition,
        Mat runtimeImage,
        IReadOnlyList<RoiRegion> referenceRois);
}
```

主工程只依赖自己的 `CameraAlignmentDefinition` / `RoiRegion`，转换到 `RoiAlignment.Core.RoiShape` 放在适配层。

### ITaskExecutionService

负责按任务类型分发：

- Classification -> CLIP。
- Color -> HSV。
- Measurement -> 1D 测量。

不要让 CLIP、HSV、Measurement 互相知道彼此。

## 8. 数据持久化

建议以一个可配置 Root 作为所有运行资产的根目录。配置中保存相对路径，不保存绝对路径：

```text
{RootDirectory}/
  inspection_config.json
  app_settings.json
  Products/
    default-product/
      camera-01/
        alignment/
          reference.png
          template.align.json
          preview.png
      camera-02/
        alignment/
          reference.png
          template.align.json
```

Root 默认可以先使用：

```text
{AppContext.BaseDirectory}/RuntimeData
```

后续在参数设置里允许用户改成统一数据盘目录，例如：

```text
D:\VisionData\KingCold
```

配置 JSON 里保存：

- 产品型号。
- 每个产品 + 相机的对齐配置。
- 每个产品 + 相机的任务列表。
- 每个任务的 ROI 和类型参数。
- RootDirectory。

不要把 CLIP 向量样本、模板描述符 Base64、图片大文件塞进主配置 JSON，只保存相对路径和索引摘要。

## 9. 第一版实现顺序

### 阶段 1：型号管理 UI 骨架

- 新增 `型号管理` 导航入口。
- 产品型号列表。
- 全相机快速操作 Panel：拍照、创建、全部清除。
- 相机模板卡片列表。
- 选中相机后显示注册图、模板路径、描述符摘要。
- 支持 RootDirectory + 相对路径。

### 阶段 2：任务配置页 UI 骨架

- 左栏产品下拉 + 相机卡片。
- 中栏 ImageBox 显示选中相机图片；无图黑底。
- 右栏图像对齐区域 + 任务列表区域。
- 拍照和读图只对选中相机可用。

### 阶段 3：ROI 与任务配置

- 扩展 ROI 为旋转矩形。
- ImageBox 显示任务 ROI overlay。
- 新增/删除/选择任务。
- 任务类型增加：分类、颜色、测量。
- 配置保存和加载。

### 阶段 4：接入 RoiAlignment.Core

- 将 `RoiAlignment.Core` 加入 solution 和 ProjectReference。
- 统一 OpenCvSharp 版本。
- 新增 `AlignmentService` 适配层。
- 支持加载模板并把任务 ROI 转换到运行图坐标。
- 型号管理中调用 `AlignmentTemplateBuilder` 创建模板。

### 阶段 5：实现颜色和 1D 测量

- `HsvColorInspectionService`。
- `Measurement1DService`。
- 先做最小可用参数，不做复杂模板管理。

### 阶段 6：完善 CLIP 样本维护和模板增强

- CLIP OK/NG 样本维护页面。
- 模板版本管理。
- 模板质量评分。
- 任务运行结果回显。

## 10. 当前建议结论

`RoiAlignment.Core` 建议直接引用，但只引用 `src/RoiAlignment.Core`，不要引用 demo。

接入前先做两件小事：

1. 把 `RoiAlignment.Core` 的 OpenCvSharp 版本统一到 `4.11.0.20250507`。
2. 在 `VisionWorkbench` 新增 `AlignmentService` 适配层，不让页面和任务模型直接依赖 `RoiAlignment.Core` 的所有类型。

这样边界会比较清楚：

- `RoiAlignment.Core`：算法能力。
- `VisionWorkbench.Services.Alignment`：算法适配。
- `VisionWorkbench.Services.Assets`：Root 和相对路径解析。
- `VisionWorkbench.Models.Inspection`：产品、相机、模板、任务、ROI 配置。
- `ProductModelManagementViewModel`：型号与模板资产维护。
- `TaskSettingsViewModel`：页面状态和命令。
- `ImageBox`：图像显示和 ROI 交互。
