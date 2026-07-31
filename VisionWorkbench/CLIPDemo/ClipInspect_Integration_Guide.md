# ClipInspect 集成说明

本文档说明当前 C# 版 ClipInspect 的边界、部署文件、SQLite 向量库使用方式，以及如何在其他 C# 项目中快速接入。

## 1. 项目边界

当前工程分为三层：

```text
ClipInspect
  可复用核心库，给其他 C# 程序引用。

ClipInspect.WpfDemo
  WPF 演示程序，只负责建库、样本维护、推理测试。

python_demo
  Python 原型和 ONNX 导出工具，用于验证和模型导出。
```

核心能力应优先放在 `ClipInspect` 中，WPF 只作为调用示例。后续移植到其他工业软件时，主要引用 `ClipInspect.dll`。

## 2. 核心库职责

`ClipInspect` 内部结构：

```text
Core
  检测流程、建库请求、检测请求、检测结果。

Onnx
  ONNX Runtime 图像编码器，负责把图片转成 CLIP 512 维特征。

Storage
  JSON cache 兼容读写，以及 SQLite 向量数据库。

Matching
  向量点积、TopK 排序、相似度结果。
```

当前主流程已经切到 SQLite。JSON cache 相关代码保留，用于兼容旧 Python 原型或做导入导出，不建议作为新流程主存储。

## 3. 运行依赖

目标框架：

```text
.NET 8
```

NuGet 依赖：

```text
Microsoft.ML.OnnxRuntime 1.20.1
Microsoft.Data.Sqlite 8.0.11
SixLabors.ImageSharp 3.1.5
```

运行时需要额外部署：

```text
Models/clip_vit_b32_image.onnx
Cache/clip_vectors.db
```

`Models/` 和模型权重文件已经加入 `.gitignore`，不会进入 git。

## 4. ONNX 模型

当前模型：

```text
Models/clip_vit_b32_image.onnx
```

它由 Python OpenCLIP 导出：

```powershell
C:\Users\ljia\miniconda3\Scripts\conda.exe run -n yolo python D:\CLIP\python_demo\export_clip_onnx.py --out D:\CLIP\Models\clip_vit_b32_image.onnx
```

模型不会打包到 git，需要现场部署时单独复制。

## 5. SQLite 向量库

默认数据库：

```text
Cache/clip_vectors.db
```

主要表：

```text
products
  product_id
  name
  model_name
  pretrained
  feature_dim
  top_k
  threshold
  text_weight
  created_at
  updated_at

samples
  id
  product_id
  label       OK / NG
  kind        Image / Text
  image_path
  prompt
  feature     float32[] BLOB
  enabled
  source
  note
  created_at
  updated_at
```

当前 TopN 查询方式：

```text
SQLite 存样本和向量 BLOB
C# 按 productId + label + kind + enabled 读取候选
内存中做 dot product
排序取 TopN
```

这个方案适合当前 demo 和中小规模本地样本库。后续如果样本量很大，可以在 `SqliteVectorStore.SearchAsync` 内部替换为 `sqlite-vec`，外部 API 可以保持不变。

## 6. 建库与样本维护

典型建库流程：

```csharp
using ClipInspect.Onnx;
using ClipInspect.Storage.Sqlite;

var dbPath = @"D:\CLIP\Cache\clip_vectors.db";
var modelPath = @"D:\CLIP\Models\clip_vit_b32_image.onnx";
var productId = "part_A";

var store = new SqliteVectorStore(dbPath);

using var encoder = new OnnxClipImageEncoder(modelPath);
var feature = await encoder.EncodeImageAsync(okImagePath);

await store.CreateOrUpdateProductAsync(new SqliteProductConfig
{
    ProductId = productId,
    Name = productId,
    ModelName = "ViT-B-32",
    Pretrained = "laion2b_s34b_b79k",
    FeatureDim = feature.Length,
    TopK = 3,
    Threshold = 0.94f,
    TextWeight = 0
});

await store.AddImageSampleAsync(productId, "OK", okImagePath, feature);
```

添加 NG 样本：

```csharp
var ngFeature = await encoder.EncodeImageAsync(ngImagePath);
await store.AddImageSampleAsync(productId, "NG", ngImagePath, ngFeature);
```

删除样本：

```csharp
await store.DeleteImageSampleAsync(productId, imagePath);
```

禁用样本：

```csharp
await store.SetSampleEnabledAsync(sampleId, false);
```

查询样本：

```csharp
var samples = await store.ListSamplesAsync(productId);
```

查询 TopN：

```csharp
var matches = await store.SearchAsync(
    productId,
    label: "OK",
    kind: "Image",
    queryFeature,
    topK: 3);
```

## 7. 推理调用

推荐直接使用 SQLite 推理接口：

```csharp
using ClipInspect.Core;
using ClipInspect.Onnx;

using var encoder = new OnnxClipImageEncoder(@"D:\CLIP\Models\clip_vit_b32_image.onnx");
var engine = new ClipInspectionEngine(imageEncoder: encoder);

var result = await engine.InspectImageFromSqliteAsync(new InspectSqliteImageRequest
{
    DatabasePath = @"D:\CLIP\Cache\clip_vectors.db",
    ProductId = "part_A",
    ImagePath = imagePath,
    TopK = 3,
    Threshold = 0.94f
});

Console.WriteLine(result.Label);
Console.WriteLine(result.ImageOkScore);
Console.WriteLine(result.ImageNgScore);
Console.WriteLine(result.ImageMargin);
Console.WriteLine(result.Timing.TotalMs);
```

结果字段：

```text
Label          OK / NG
ImageOkScore   OK TopK 均值相似度
ImageNgScore   NG TopK 均值相似度，可为空
ImageMargin    OK - NG
TopOk          OK TopK 命中样本
TopNg          NG TopK 命中样本
Timing         推理、匹配、总耗时
```

判定逻辑：

```text
如果没有 NG 样本：
  OK score >= threshold => OK

如果有 NG 样本：
  OK score >= threshold 且 OK score - NG score >= 0 => OK
```

## 8. WPF Demo

运行：

```powershell
dotnet run --project D:\CLIP\ClipInspect.WpfDemo\ClipInspect.WpfDemo.csproj
```

WPF 分为两个页面。

### Cache 构建

功能：

```text
选择/创建 SQLite 数据库
查询产品
加载 OK 图片
加载 NG 图片
构建/追加到 SQLite
查看数据库 OK/NG 样本
删除数据库样本
```

### 推理测试

功能：

```text
选择产品
选择待测图片
执行推理
显示 OK/NG 分数、margin、TopK 命中、耗时
```

## 9. 快速移植清单

移植到其他 C# 项目时需要：

```text
1. 引用 ClipInspect 项目或 ClipInspect.dll
2. 带上 NuGet 依赖
3. 部署 ONNX 模型
4. 准备 SQLite 数据库
5. 用 OnnxClipImageEncoder + ClipInspectionEngine 调用推理
```

最小调用路径：

```text
OnnxClipImageEncoder
  -> EncodeImageAsync
  -> ClipInspectionEngine.InspectImageFromSqliteAsync
  -> SqliteVectorStore.SearchAsync
```

## 10. 当前边界和后续建议

当前可以快速移植验证，但还有几个生产化建议：

```text
1. 增加统一门面类 ClipInspectService，减少调用方组合类的成本。
2. 增加 schema_version 表，做正式数据库版本迁移。
3. 增加检测历史表，保存每次推理结果和 TopK 命中。
4. 增加误判一键加入 OK/NG 的 SDK API。
5. 样本量变大后，把 SearchAsync 内部替换为 sqlite-vec。
```

当前最重要的边界原则：

```text
业务软件只调用 ClipInspect。
WPF Demo 不承载核心业务逻辑。
模型文件和数据库文件作为外部部署资源管理。
```
