# 特征匹配与轻量学习路线整理

## 1. 核心目标

当前项目目标不是先训练一个大模型，而是利用成熟 backbone 提取特征，再通过向量库、PCA、局部匹配和轻量分类器构建可持续优化的工业检测系统。

推荐总体架构：

```text
YOLO / ROI 定位
-> CLIP / ResNet / DINO 特征提取
-> 向量库维护 OK/NG 分布
-> 全局分布判断 + 局部结构判断
-> 人工纠错后持续入库
```

核心思想：

```text
训练模型 -> 变成维护向量记忆库
重新训练 -> 变成追加/禁用/压缩样本
黑盒判定 -> 变成相似度、PCA、热图、近邻解释
```

## 2. 直接点积 Baseline

最基础方法是对图片编码得到一个归一化向量，然后直接计算点积：

```text
similarity = dot(query_feature, cache_feature)
```

如果向量已经 L2 normalize，点积就是 cosine similarity。

当前 CLIP 流程就是：

```text
image -> CLIP image encoder -> 512 维向量
query 与 OK/NG cache 做 TopK 点积
TopK 相似度平均作为分数
```

优点：

```text
实现简单
推理快
适合全局粗分类、样本聚类、库体检
可以直接接 SQLite / sqlite-vec / Faiss
```

限制：

```text
CLIP 全局向量没有局部热图
小区域缺陷可能被整体语义稀释
对裁切构图、黑边、目标比例仍然敏感
只靠 OK TopK 不一定能区分局部结构异常
```

适合用途：

```text
OK/NG 粗筛
裁切质量检测
重复样本去重
样本聚类
向量库入库门控
```

## 3. TopK 聚合

TopK 有两种含义，必须区分。

样本级 TopK：

```text
query 全局向量和所有 cache 图片向量匹配
取最相似的 K 张图片
平均相似度作为分数
```

这是当前 CLIP cache 的做法。

Patch 级 TopK：

```text
每个 patch 都有一个异常距离
取距离最大的前 1% / 5% / 10%
平均后作为局部异常分数
```

这是 DINO patch anomaly 更适合的做法。

结论：

```text
全局 CLIP 用样本级 TopK
DINO patch 用 patch 级 TopK
两者不是一回事
```

## 4. PCA 的作用

PCA 不是取原始特征的某几列，而是计算新的主方向。

对于 512 维 CLIP 特征：

```text
PC1 = 512 维权重向量
pc1_score = dot(feature - mean, PC1)
```

PCA 的几个用途：

```text
二维/三维可视化
特征压缩
去掉部分低方差噪声
观察 OK 分布是否稳定
发现离群样本
```

需要注意：

```text
PCA 是无监督方法
PC1 只是最大变化方向，不一定是 OK/NG 方向
如果最大变化来自光照/裁切，PC1 就会表示光照/裁切
不同批次重新 fit PCA，坐标系可能旋转或正负翻转
```

工程建议：

```text
用稳定 OK 样本 fit PCA
保存 mean、components、explained_variance、version
后续样本只 transform，不随意重新 fit
PCA 更新时重新计算库内 PCA 坐标和阈值
```

## 5. OK-only PCA 异常分数

如果 NG 样本少、不稳定、不可枚举，优先使用 OK-only 建模。

推荐分数 1：PCA Mahalanobis Score

```text
z = PCA(feature - mean)
score = sum((z_i / std_i)^2)
```

含义：

```text
OK 中稳定的方向发生偏移，会被放大
OK 中本来波动大的方向，权重会降低
```

推荐分数 2：PCA Reconstruction Error

```text
z = PCA.transform(x)
x_hat = PCA.inverse_transform(z)
residual = ||x - x_hat||
```

含义：

```text
样本有多少特征无法被 OK 主空间解释
```

边界可以从 OK 样本自动估计：

```text
threshold = OK score 的 99% 分位数
```

最终可以只暴露一个分数：

```text
PCA Anomaly Score
```

### 5.1 Mahalanobis / PCA Residual 实验结论

实验脚本：

[python_demo/feature_distribution_scoring.py](D:/CLIP/python_demo/feature_distribution_scoring.py)

实验数据：

```text
D:\CLIP\allImage
ok: 13 张
ng: 5 张
```

实验方法：

```text
1. 只使用 allImage/ok 拟合 OK 分布
2. 对 OK 和 NG 全部图片打分
3. 对比：
   - OK TopK similarity
   - PCA Mahalanobis
   - PCA residual
   - LedoitWolf 正则化 Mahalanobis
```

输出目录：

```text
python_demo/outputs/distribution_clip_allImage
python_demo/outputs/distribution_clip_allImage_pca3
python_demo/outputs/distribution_resnet18_avg_allImage
python_demo/outputs/distribution_resnet18_avg_allImage_pca3
```

关键结果：ResNet18 avg pooling + PCA=3

| 指标 | OK 均值 | NG 均值 | 间隔 |
|---|---:|---:|---:|
| OK TopK similarity | 0.9699 | 0.8514 | 0.1185 |
| PCA Mahalanobis | 2.7692 | 5.7321 | 2.9629 |
| PCA residual | 0.1130 | 0.4917 | 0.3787 |
| LedoitWolf Mahalanobis | 13.6942 | 6416.3059 | 6402.6117 |

分布图：

[ResNet18 avg PCA=3 分布图](D:/CLIP/python_demo/outputs/distribution_resnet18_avg_allImage_pca3/resnet18_avg_distribution_histograms.png)

观察：

```text
1. OK TopK similarity 已经能分开
   OK 大约 0.93~0.98
   NG 大约 0.84~0.86

2. PCA residual 非常干净
   OK 大约 0.04~0.16
   NG 大约 0.47~0.51
   中间间隔明显

3. LedoitWolf 正则化 Mahalanobis 很强
   它处理了高维小样本下协方差矩阵不稳定的问题
   OK 和 NG 被拉得非常开

4. 普通 PCA Mahalanobis 要谨慎
   PCA 组件太多、OK 样本太少时，小方差方向会导致 NG 分数爆炸
   这可能有用，但阈值必须版本化，并用更多 OK 样本验证
```

结论：

```text
Mahalanobis 方向非常有价值。

但在小样本高维特征中，不建议直接裸算完整协方差 Mahalanobis。
应优先使用：
  - PCA 空间分数
  - PCA residual
  - LedoitWolf / shrinkage covariance

当前这组样本里，最稳的是：
  1. PCA residual
  2. LedoitWolf 正则化 Mahalanobis
  3. OK TopK similarity
```

建议的单一分数：

```text
Distribution Anomaly Score =
  0.5 * normalized_pca_residual
  + 0.3 * normalized_ledoit_mahalanobis
  + 0.2 * (1 - ok_topk_similarity)
```

阈值建议：

```text
threshold = OK 样本 Distribution Anomaly Score 的 99% 分位数
```

现场不需要暴露多个参数，可以只显示：

```text
Distribution Anomaly Score
OK Threshold
OK / NG
```

工程注意点：

```text
1. PCA / Mahalanobis 的参数必须跟向量库一起版本化
   包括 backbone、pooling、PCA components、均值向量、PCA 权重、协方差或 LedoitWolf 参数。

2. OK 样本很少时，Full PCA residual 容易对训练 OK 过拟合
   所以阈值不能只看训练 OK，需要至少留出一部分 OK 做验证，或者用滚动生产 OK 数据持续校准。

3. 如果没有稳定 NG 样本，PCA residual / LedoitWolf Mahalanobis 比 LDA、SVM、Logistic 更适合
   因为它们主要建模 OK 分布，不依赖 NG 类型完整。

4. 如果后续 NG 样本逐渐稳定，可以把 Distribution Anomaly Score 作为一个基础分数
   再叠加 LDA / Logistic / Linear SVM 做二级校准。
```

## 6. PCA + LDA / Logistic / Linear SVM

如果有稳定 OK/NG 标签，可以训练轻量线性分类器。

这些方法最终形式都类似：

```text
score = dot(feature, w) + b
```

区别：

```text
LDA: 统计解析解，找类间远、类内紧的方向
Logistic Regression: 优化交叉熵，输出 NG 概率
Linear SVM: 最大化分类间隔，适合小样本高维特征
```

推荐用法：

```text
backbone feature
-> PCA 8/16/32/64
-> LDA / Logistic / Linear SVM
-> 留一法或 K 折交叉验证
```

适合场景：

```text
NG 类型稳定
OK/NG 都有代表样本
想快速得到轻量分类器
```

不适合场景：

```text
NG 类型很少
NG 类型未来不可预测
只想发现未知异常
```

### 6.1 One-Class SVM

One-Class SVM 只使用 OK 样本学习正常区域，适合 NG 类型未知、但 OK 样本可以持续积累的场景。

核心形式：

```text
OK features -> OneClassSVM -> normal boundary
Query feature -> inside / outside boundary
```

它和 LDA / Logistic / Linear SVM 的区别：

```text
LDA / Logistic / Linear SVM:
  需要 OK 和 NG 两类样本
  学的是 OK / NG 分界面

One-Class SVM:
  只需要 OK 样本
  学的是 OK 分布边界
```

推荐输入流程：

```text
backbone feature
-> L2 normalize 或 StandardScaler
-> PCA 8/16/32/64
-> OneClassSVM(kernel='rbf', nu=0.05~0.1)
```

注意：

```text
nu 不是完全免阈值
它控制训练 OK 中允许被排除在边界外的比例，也影响边界松紧。

RBF kernel 对特征尺度很敏感
直接把 512 维原始特征输入通常不如先 PCA/标准化稳定。

OK 样本很多时，RBF One-Class SVM 的推理成本可能上升
需要关注支持向量数量。
```

适合场景：

```text
OK 分布不是单峰
NG 类型不可预测
想比单高斯 Mahalanobis 更灵活
样本量中等，能接受一定调参
```

不适合场景：

```text
OK 样本极少
特征尺度没有校准
需要非常轻量、可解释、易版本化的部署
```

当前定位：

```text
One-Class SVM 可以作为全局异常分数候选项
但不应直接替代 PCA residual / LedoitWolf Mahalanobis
需要和 OK TopK、PCA residual 一起做实验对比
```

## 7. CLIP 的角色

CLIP 全局特征在本项目中适合：

```text
样本聚类
裁切质量检测
OK/NG 粗筛
文本辅助
向量库体检
入库门控
```

实验观察：

```text
D:\CLIP\crop 中，有螺丝和缺螺丝可以被 CLIP PCA/聚类明显分开
D:\CLIP\crop2 中，CLIP 能发现裁切偏移的离群样本
D:\CLIP\allImage 中，NG 独立成 cluster，OK 又按任务/裁切分成两个子分布
```

局限：

```text
CLIP 全局向量没有局部位置热图
小缺陷可能不稳定
对构图和裁切变化敏感
```

推荐定位：

```text
CLIP 做全局分布和库质量
DINO 做局部结构异常
YOLO 做 ROI 定位
```

## 8. DINO Patch 匹配

DINOv2 输出 patch tokens，适合局部结构异常检测。

当前路线：

```text
OK 图片 -> DINO patch memory
Query 图片 -> DINO patch tokens
每个 query patch 匹配 OK patch
得到 patch distance map
TopK + 空间过滤得到图像级异常分数
```

匹配模式：

```text
same-position:
  只和相同网格位置匹配
  结构约束强，但对位移敏感

unrestricted:
  每个 patch 可和所有 OK patch 匹配
  平移鲁棒，但可能把真实缺陷解释成背景相似

local-window:
  只在相同位置附近窗口匹配
  推荐默认，兼顾位置约束和小范围平移
```

推荐默认：

```text
match_mode = local-window
window_radius = 1
```

## 9. Patch TopK 与空间过滤

对于局部异常，不建议使用所有 patch 距离均值，因为异常会被正常区域稀释。

推荐：

```text
patch_top5_distance = 前 5% 最大 patch distance 的平均
```

进一步增加空间过滤：

```text
先取响应最高的 Top 5% patch
只保留至少有 min_neighbors 个高响应邻居的 patch
孤立高响应 patch 被过滤
连续异常块被保留
```

当前实验中：

```text
OK 边界图的孤立响应被过滤成 0
NG 孔洞图保留 9~13 个连续异常 patch
```

这适合：

```text
缺螺丝
孔洞
局部连续缺陷
异物区域
```

## 10. DINO 热图

DINO patch 热图实际分辨率来自 patch grid。

例如：

```text
image_size = 224
patch_size = 14
grid = 16 x 16
```

平滑热图：

```text
低分辨率距离图 -> BICUBIC resize 到原图
视觉连续，但会显得比真实 patch 更精细
```

块状热图：

```text
低分辨率距离图 -> NEAREST resize 到原图
显示真实 patch 网格
```

建议两者都输出：

```text
smooth heatmap: 方便人眼看趋势
block heatmap: 真实定位粒度
```

### 10.1 PatchCore 路线

PatchCore 是 OK 特征库 + 局部最近邻距离路线的代表方法。

它的关键不是训练一个复杂分类器，而是：

```text
1. 使用 backbone 提取局部 patch 特征
2. 保留 OK 图片的局部 patch memory bank
3. 从 memory bank 中筛选代表性 coreset，降低存储和检索成本
4. 推理时，每个 query patch 到 OK memory bank 中找最近邻
5. 如果某个 query patch 到最近 OK patch 仍然很远，则认为该局部异常
6. 整图异常分数使用 Max / Top-K patch distance 聚合
```

和直接全局点积的区别：

```text
全局点积:
  一张图一个向量
  快，但小缺陷容易被整体相似度稀释

PatchCore:
  一张图多个局部向量
  对缺螺丝、孔洞、划伤、异物这类局部异常更敏感
```

和我们当前 DINO patch 方案的关系：

```text
当前 DINO patch memory:
  已经具备 PatchCore 的主体思路
  即 patch feature -> OK memory -> nearest neighbor distance -> TopK score

我们额外加入的 local-window:
  增加位置约束
  避免任意位置 patch 把真实局部缺陷解释成别处的正常区域

我们额外加入的空间邻域过滤:
  过滤孤立高响应 patch
  保留连续异常区域
```

推荐吸收的 PatchCore 工程点：

```text
coreset 采样:
  从大量 OK patch 中选代表性子集，减少向量库规模

Faiss / sqlite-vec 检索:
  加速 patch nearest neighbor topK 查询

Top-K / Top-percent 聚合:
  用前 1% / 5% 最大 patch distance 表示整图异常
```

适合场景：

```text
产品 ROI 已经裁切稳定
异常是局部的
OK 样本可以持续积累
希望得到热图解释
```

风险：

```text
patch memory 会比全局向量库大很多
ROI 对齐不好时可能需要 local-window 或位置编码
coreset、TopK 百分比、patch 尺寸都需要版本化
```

当前定位：

```text
PatchCore 是局部异常检测的重点参考路线
我们不必直接搬完整算法，但应该吸收它的 patch memory、coreset、nearest neighbor、Top-K 聚合思想。
```

## 11. AnomalyDINO 对照

官方 AnomalyDINO 路线：

```text
DINOv2 patch features
FAISS 1NN
Top1% patch distance
可选 PCA foreground mask
可选旋转增强
```

工程评价：

```text
官方代码短，容易跑通
适合 few-shot 工业异常检测
和我们当前 DINO patch 方向一致
```

本项目观察：

```text
官方 AnomalyDINO 可以检测孔洞区域
但没有我们的空间邻域过滤
边界 OK 图仍可能有较高孤立响应
```

建议：

```text
吸收 AnomalyDINO 的 patch memory / FAISS / Top1% 思路
保留我们自己的 local-window 和空间过滤
不要直接整仓库搬入主工程
```

## 12. ResNet18 特征

标准 ResNet18 最后一层卷积特征大致为：

```text
7 x 7 x 512
```

如果直接 flatten：

```text
7 x 7 x 512 -> 25088
```

问题：

```text
空间位置必须严格对齐
目标稍微偏移，点积会下降
```

更推荐全局池化：

```text
Global Average Pooling:
  7 x 7 x 512 -> 512
  稳定，适合分类/聚类

Global Max Pooling:
  7 x 7 x 512 -> 512
  更关注局部强响应，但更敏感

Avg + Max concat:
  1024 维
  同时保留稳定性和局部峰值
```

通道是固定对齐的：

```text
第 i 个维度始终对应第 i 个卷积通道
池化后可以直接点积
```

实验观察：

```text
D:\CLIP\crop 中，ResNet18 avgpool 特征也能干净分开有螺丝/孔洞
Silhouette 比 CLIP 更高
```

建议：

```text
ResNet18 可作为轻量 baseline
适合稳定 ROI 的简单分类
ONNX 部署比 CLIP/DINO 更轻
```

## 13. 向量数据库与持续学习

有向量数据库后，可以把系统做成持续学习式 OK 分布维护。

基本流程：

```text
爬坡阶段:
  人工确认 OK 后入库

生产阶段:
  高置信 OK 自动候选入库
  低置信样本进入人工复核池

纠错阶段:
  一键加入 OK / NG
  一键禁用脏样本

维护阶段:
  去重
  聚类
  离群检测
  压缩 memory
```

注意：

```text
全局向量可以大量保存
DINO patch memory 不宜无限保存
1 万张图 x 272 patch x 384 float32 约 4GB+
```

推荐：

```text
全局特征大量存
patch 特征存代表样本或压缩后的 memory
原图路径保留，必要时重算 patch
```

## 14. 可视化工具

已验证的可视化：

```text
Cosine Similarity Heatmap:
  两两相似度矩阵
  适合看同类亮块、离群行列、重复样本

PCA 2D:
  PC1/PC2 散点
  适合看主分布和离群点

PCA 3D:
  PC1/PC2/PC3 点云
  保留更多变化信息，可交互旋转

Dendrogram:
  层次聚类树
  小样本解释性强
  大样本会拥挤
```

大样本建议：

```text
先聚类，再画 cluster-level dendrogram
用 PCA/UMAP 散点图看分布
输出库质量报表
```

## 15. 当前推荐工程路线

第一阶段：轻量可用

```text
YOLO/ROI 定位
CLIP 或 ResNet18 全局特征
向量库 TopK
PCA 可视化和 OK-only score
PCA residual / LedoitWolf Mahalanobis
One-Class SVM 作为候选全局异常分数
```

第二阶段：局部可靠

```text
DINOv2 patch memory
local-window matching
patch Top5
空间邻域过滤
smooth/block heatmap
PatchCore-like memory bank
coreset 压缩
```

第三阶段：持续优化

```text
SQLite/sqlite-vec 或 Faiss
OK 样本持续入库
去重/聚类/离群清洗
PCA version 管理
人工纠错闭环
```

第四阶段：轻量监督分支

```text
当 NG 样本稳定后:
  PCA + LDA
  Logistic Regression
  Linear SVM

作为已知 NG 分类器
不替代 OK-only 异常检测
```

## 16. 关键结论

```text
直接点积是必要 baseline，但不是终点。

CLIP:
  做全局分布、聚类、入库门控、文本辅助。

ResNet18:
  做轻量全局分类 baseline，部署简单。

DINO:
  做局部结构异常和热图解释。

PCA:
  做 OK-only 分布建模、可视化、压缩、异常分数。

LDA/Logistic/SVM:
  在 NG 样本稳定后做轻量监督分类器。

One-Class SVM:
  在没有稳定 NG 时，作为 OK-only 非线性边界候选。

PatchCore:
  是局部 OK 特征库 + 最近邻异常分数的重点参考路线。

向量库:
  是持续学习和工程闭环的核心。
```
