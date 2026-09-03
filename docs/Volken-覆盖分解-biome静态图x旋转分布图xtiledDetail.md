# Volken 覆盖分解: biome 静态图 × 旋转分布图 × tiled detail

> 日期:2026-08-27(与 BiomeMapTex 同年同月,系同一覆盖改造的第二步)
> 状态:已实现(shader + config + UI + 本地化),默认全关 = 现状逐字节一致

## 1. 目标

把"哪里出云"(覆盖率)从单一标量 `cloudCoverage` + 单一 biome 因子,分解为三个**独立可控、相乘**的因子:

```
coverage = cloudCoverage × F_biome × F_rotDist × F_tiledDetail
```

每个因子由一张图驱动、一个 0..1 强度滑块控制,艺术家可以分别调气候区、行星尺度滚动分布、小尺度平铺细节对云量的影响。

## 2. 现状梳理(实现前的覆盖通路)

`SampleDensity` / `SampleDensityCheap` 的密度返回:

```
totalDensity = shape·(Σ dist·falloff) + Σ layers·falloff        // shape=3D噪声, dist/layers=分布源
density      = (totalDensity + cloudCoverage·lerp(1, biome, biomeStrength) − 1) · cloudDensity
```

- `cloudCoverage`:`config.coverage`,基值偏置(可负)。
- `biome`:`BiomeMapTex.r`(低频、**加风偏移前**的经纬采样 → 地理锚定、不随云漂移,2026-08-27 加入)。
- `rotDist`(旋转分布):`mapVal = lerp(planetMap, stockBand, stockEff·valid)` 按层合并——
  planetMap 程序化(随风滚动=绕行星旋转的动画分布),方案 B(`StockCloudCube` 游戏自带云 cubemap)开时按层替换。
  现状它只进 `layers`(附加密度)和 `dist`(方案 B 的 shape 门),**从未进覆盖项**。
- `tiled detail`:`CloudDetailTex`(whorley FBM,与 shape 同 domain warp),现状只做 shape 侵蚀
  `shape -= (1-shape)²·detailStrength·detailFalloff·detail`,**从未进覆盖项**。

## 3. 分解公式(实现)

在 `totalDensity` 计算后、return 前:

```hlsl
// F1 biome 静态图: 地理锚定, 气候/生物群系区域(现有实现原样保留)
// F2 旋转分布图: 活性层的分布强度(planetMap / stock 按层合并, 随风滚动)
float rotDistValue = saturate(dot(active, mapVal) / max(1e-6, dot(active, 1)));
// F3 tiled detail: 平铺细节噪波作覆盖掩码(亮=多云), 与 shape 侵蚀同源同 warp
float coverageMask = lerp(1, biome, biomeStrength)
                   * lerp(1, rotDistValue, rotDistStrength)
                   * lerp(1, saturate(detail), detailCoverageStrength · detailFalloff);   // Cheap 版无 detailFalloff

return (totalDensity + cloudCoverage · coverageMask − 1) · cloudDensity;
```

要点:

- `rotDistValue` 用 `active`(= step(0.0001, layerStrengths))对 `mapVal` 加权平均:只有**开启的层**参与,
  单层开启时该层分布即因子;全 0 层时归一分母兜底 1e-6 防除零。`saturate` 兜住 planetMap 的负值(RGFloat 允许负)。
- `F3` 复用已在函数里取到的 `detail` 采样,**零额外纹理读取**;`detailFalloff` 使其与 shape 侵蚀一致地"近浓远淡"。
- 三强度全 0 → `coverageMask = 1` → 与现状**逐字节一致**(含 biome 原通路)。

## 4. 参数

| 字段(CloudConfig) | shader uniform | UI 词条 | 范围 | 默认 | 语义 |
|---|---|---|---|---|---|
| `biomeStrength`(已有) | `biomeStrength` | `Volken.UI.BiomeStrength` | 0..1 | 0 | F1 biome 静态图 |
| `rotDistStrength`(新) | `rotDistStrength` | `Volken.UI.RotDistStrength` | 0..1 | 0 | F2 旋转分布图 |
| `detailCoverageStrength`(新) | `detailCoverageStrength` | `Volken.UI.DetailCoverageStrength` | 0..1 | 0 | F3 tiled detail |

新字段默认 0 → 旧 XML 配置无节点时反序列化保留默认 → 零迁移成本(与方案 B 同策略)。

## 5. 改动文件

| 文件 | 改动 |
|---|---|
| `Clouds.shader` | `SampleDensity` / `SampleDensityCheap` 尾部覆盖分解 + 2 个 uniform 声明 |
| `CloudConfig.cs` | 新字段 `rotDistStrength` / `detailCoverageStrength` + Clone / CopyFrom |
| `CloudLayer.cs` | `SetStaticShaderProperties` 绑定 2 个新 uniform(Clamp01) |
| `VolkenUserInterface.cs` | 主层 + 额外层 qualityGroup 各加 2 个滑块(紧邻 BiomeStrength) |
| `EN-US.xml` / `ZH-CN.xml` / `RU-RU.xml` | 3 个新词条 + 补缺失的 `Volken.UI.BiomeStrength` |

## 6. 调参建议

- F2 适合"行星尺度"的云带/洋流走向:与 `windSpeed` / `globalRotationAngular` 联动看滚动效果;
  方案 B 开时 F2 直接吃游戏自带云的分布值,与 `stockMapStrength` 的替换强度叠加。
- F3 适合打破 coverage 层面的大片平铺重复:小值(0.1~0.3)已能在云区内部制造细碎空洞,
  配合 `detailScale` 控制空洞尺度;若与 shape 侵蚀同开,注意 `detailStrength` 勿过大以免云被掏空。
- 三因子相乘,任一因子为 0 处必无云(乘法门控);想只减不增的区域语义,保持各图暗区为 0 即可。

## 7. 已知边界

- `SampleDensityCheap`(光步进)无 `detailFalloff`,F3 的远近衰减只作用于主路径——与现有 shape 侵蚀行为一致。
- F2 的 `.y` 分量在 planetMap 模式是高度通道而非密度,仅当 Layer2 开启时进入加权平均(与 Layer2 密度同源,视觉一致)。
