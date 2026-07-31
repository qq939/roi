# CLIP + OK Cache 工业结构异常检测 Demo 计划

## 1. 背景与目标

工业现场存在频繁换型、NG样本少的问题。本Demo针对明显结构级异常，例如：

- 装错件
- 漏装件
- 方向装反
- 型号混料
- 外形结构明显不一致
- 孔位、接口、组件形态差异

这类问题更适合使用CLIP图像特征做整体或ROI级相似度判断，而不是依赖大量NG样本训练分类器。

Demo目标：

1. 使用少量OK图片为每个产品型号建立特征cache。
2. 新图像输入后，与当前型号OK cache计算相似度。
3. 输出相似度分数、OK/NG结果、Top-K最相似OK样本。
4. 验证该方案在频繁换型场景下是否具备快速上线能力。

目标技术栈：

- WPF
- C#
- ONNXRuntime
- CLIP Image Encoder ONNX

## 2. 总体方案

第一版不做完整Tip-Adapter训练，只实现最核心的工程化逻辑：

```text
CLIP Image Encoder
+
OK样本特征cache
+
Top-K cosine similarity
+
阈值判定
```

推理公式：

```text
query_feature = CLIP(image)
score = mean(topK(cosine(query_feature, ok_cache_features)))

if score >= threshold:
    result = OK
else:
    result = NG
```

其中所有特征在计算相似度前都做L2 normalize。

## 3. Demo范围

### 第一阶段：单型号、整图检测

功能：

- 导入一个OK图片文件夹。
- 批量提取CLIP图像特征。
- 建立并保存当前型号OK cache。
- 导入一张待测图片。
- 计算待测图与OK cache的Top-K相似度。
- 显示：
  - 当前型号
  - 待测图片
  - 相似度分数
  - 阈值
  - OK/NG结果
  - Top-K最相似OK样本

### 第二阶段：多型号管理

功能：

- 支持多个产品型号。
- 每个型号独立保存cache、阈值、topK配置。
- 检测前选择当前型号。

### 第三阶段：多ROI检测

对于固定结构位置明显的零件，加入ROI级检测：

```text
full_image_score
roi_1_score
roi_2_score
roi_3_score
```

推荐融合方式：

```text
final_score = min(full_image_score, roi_1_score, roi_2_score, ...)
```

这样可以避免整图embedding把局部结构错误平均掉。

## 4. 系统架构

建议目录结构：

```text
ClipAnomalyDemo/
  Models/
    clip_image_encoder.onnx

  Cache/
    part_A.cache.json
    part_B.cache.json

  Samples/
    part_A/
      ok/
      test/

  ClipAnomaly.Core/
    ClipFeatureExtractor.cs
    ImagePreprocessor.cs
    FeatureCache.cs
    SimilarityScorer.cs
    ProductProfile.cs
    DetectionResult.cs

  ClipAnomaly.Wpf/
    MainWindow.xaml
    MainWindow.xaml.cs
```

## 5. 核心模块设计

### 5.1 ClipFeatureExtractor

职责：

- 加载CLIP Image Encoder ONNX模型。
- 接收图片路径或Bitmap。
- 完成预处理。
- 调用ONNXRuntime推理。
- 输出归一化后的图像embedding。

典型接口：

```csharp
public interface IFeatureExtractor
{
    float[] ExtractFeature(string imagePath);
}
```

### 5.2 ImagePreprocessor

职责：

- 读取图像。
- Resize到CLIP模型输入尺寸。
- 转RGB。
- 归一化。
- 转换为ONNX输入tensor。

注意事项：

- 不同CLIP ONNX模型的输入尺寸和normalize参数可能不同。
- 必须记录模型对应的预处理配置。
- C#侧预处理要和导出ONNX时的Python预处理保持一致。

常见CLIP参数示例：

```text
input_size: 224 x 224
mean: [0.48145466, 0.4578275, 0.40821073]
std:  [0.26862954, 0.26130258, 0.27577711]
```

### 5.3 FeatureCache

职责：

- 保存某个产品型号的OK样本特征。
- 保存样本图片路径、特征向量、阈值、topK等配置。
- 支持加载、保存、追加、重建。

建议cache结构：

```json
{
  "productId": "part_A",
  "featureDim": 512,
  "topK": 3,
  "threshold": 0.82,
  "preprocess": {
    "inputWidth": 224,
    "inputHeight": 224
  },
  "items": [
    {
      "imagePath": "Samples/part_A/ok/001.jpg",
      "feature": [0.01, -0.03, 0.12]
    }
  ]
}
```

第一版可以用JSON，便于调试。后续如果cache变大，再改为二进制格式。

### 5.4 SimilarityScorer

职责：

- 计算query feature与cache features的cosine similarity。
- 排序取Top-K。
- 计算最终score。
- 输出Top-K样本和相似度。

由于特征已L2 normalize，cosine similarity可简化为点积：

```text
cosine(a, b) = dot(a, b)
```

典型接口：

```csharp
public DetectionResult Detect(float[] queryFeature, FeatureCache cache);
```

### 5.5 ProductProfile

职责：

- 表示一个产品型号的检测配置。
- 绑定该型号的cache、阈值、topK、ROI配置。

字段建议：

```text
productId
displayName
cachePath
threshold
topK
roiConfigs
```

## 6. WPF界面设计

第一版界面保持简单，重点服务验证。

主要区域：

1. 型号与建库区
   - 产品型号输入框
   - 选择OK图片文件夹
   - 建立/重建cache按钮
   - cache样本数量显示

2. 检测区
   - 选择待测图片按钮
   - 图片预览
   - 检测按钮

3. 结果区
   - OK/NG结果
   - 相似度score
   - threshold
   - Top-K相似样本列表

推荐第一版显示逻辑：

```text
score >= threshold: 绿色 OK
score < threshold: 红色 NG
```

## 7. 阈值策略

### 第一版：手动阈值

先用固定阈值或界面输入阈值，例如：

```text
threshold = 0.82
```

实际阈值需要根据模型、图像、工位稳定性调整。

### 第二版：基于OK样本自动估计

将OK样本两两计算相似度，得到OK内部相似度分布。

可选策略：

```text
threshold = P5(ok_internal_scores) - margin
```

例如：

```text
threshold = 5%分位数 - 0.02
```

### 第三版：使用少量NG样本校准

如果后续有少量NG样本，不建议立即训练分类器，优先用于阈值校准：

- 观察OK score分布
- 观察NG score分布
- 选择兼顾漏检和误报的阈值

## 8. 多ROI增强方案

如果异常集中在固定关键结构区域，建议加入ROI。

每个ROI单独建立OK cache：

```text
product_A
  full_image_cache
  roi_1_cache
  roi_2_cache
```

检测时：

```text
full_score = score(full_image)
roi_1_score = score(roi_1)
roi_2_score = score(roi_2)

final_score = min(full_score, roi_1_score, roi_2_score)
```

判定时可以同时输出最异常的区域：

```text
abnormal_region = argmin(score_list)
```

## 9. 与PatchCore的关系

本Demo优先服务结构级错误：

- 漏装
- 错装
- 装反
- 混料
- 明显结构差异

因此主线选择CLIP + OK cache。

PatchCore更适合：

- 划痕
- 脏污
- 压伤
- 毛刺
- 局部表面缺陷

如果后续现场同时存在表面缺陷，可以再增加PatchCore作为补充模块。

## 10. 开发路线

### Step 1：控制台最小验证

目标：

```text
加载ONNX
读取图片
提取feature
计算两张图cosine similarity
```

输出：

```text
image_a vs image_b similarity = 0.91
```

### Step 2：OK文件夹建库

目标：

```text
输入OK文件夹
批量提取feature
保存cache
```

输出：

```text
part_A.cache.json
```

### Step 3：单图检测

目标：

```text
输入待测图片
加载cache
计算Top-K score
输出OK/NG
```

输出：

```text
score = 0.87
threshold = 0.82
result = OK
```

### Step 4：WPF界面

目标：

实现可交互Demo：

- 建库
- 选择型号
- 检测图片
- 显示结果
- 显示Top-K参考图

### Step 5：多ROI

目标：

- 支持配置ROI。
- ROI级cache。
- 输出每个ROI分数。
- 使用min score做最终判定。

## 11. 风险与注意事项

1. CLIP对光照、角度、背景变化仍然敏感，采集OK图时要覆盖正常波动。
2. 如果结构异常只占很小区域，整图embedding可能不敏感，需要ROI。
3. ONNX模型的预处理必须严格一致，否则相似度会不稳定。
4. 阈值不能跨产品型号通用，建议每个型号独立阈值。
5. OK cache样本不是越多越好，质量和覆盖范围更重要。
6. Top-K过大可能引入不相关样本，第一版建议topK=3或5。

## 12. 第一版验收标准

Demo第一版完成后，至少满足：

1. 可以导入OK图片文件夹并生成cache。
2. 可以加载cache并检测单张图片。
3. 可以输出score、threshold、OK/NG。
4. 可以显示Top-K相似OK样本。
5. 对明显结构错误样本，score低于OK样本。
6. 新型号只需要重新导入OK图片建cache，不需要训练模型。

## 13. 推荐优先级

当前需求下，推荐优先级：

```text
P0: CLIP Image Encoder ONNX推理打通
P0: OK cache建库
P0: Top-K cosine检测
P1: WPF界面
P1: Top-K参考图显示
P2: 多型号管理
P2: 自动阈值估计
P3: 多ROI检测
P3: 少量NG样本阈值校准
```

## 14. 最小可行版本定义

MVP只包含：

```text
单型号
整图检测
手动阈值
OK cache
Top-K cosine
WPF基本界面
```

该版本足够用于验证CLIP + OK cache是否适合当前工业结构异常检测场景。
