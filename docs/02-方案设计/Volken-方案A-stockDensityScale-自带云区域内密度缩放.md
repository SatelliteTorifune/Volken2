# Volken 方案 A —— `stockDensityScale` 自带云"区域内密度缩放"

> 日期:2026-09-06(设计)+ 2026-09-06(落地,修正版)
> 状态:**已实现**
> 前置:方案 B(`useStockCloudMap` 游戏自带云分布)已实现并默认关闭
> 命名说明:本文"方案 A"= 自带云区域内的密度控制;与方案 B(自带云分布接入)、方案 C(时序超采样)并列为同一套编号体系。轨道云文档里的 "A 管线 / B 管线" 是另一语境(2D 壳着色的渲染路径取舍),与本方案无关。
> **设计演进**:本方案最初按"区域内覆盖阈值重映射"(`stockCoverage`/`stockSoft`)实现;实测/需求确认后改为**区域内密度缩放**(`stockDensityScale`),因为阈值会收缩/吃掉自带云足迹边缘,而需求是**保留边缘、只调区域内部密度**。历史设计见文末 §9。

---

## 0. 结论

新增单个参数 `stockDensityScale`(0..1,默认 1),在 shader 内对自带云分布值(`stockBand`)做**区域内密度缩放**,只作用于 `mapVal`(附加密度数据源)的 stock 侧;而 `dist`(shape 乘法门)保持用**未缩放**的 `stockBand`,从而:

- **足迹边缘由 `dist` 原样保留**(`stockBand` 在边缘处 0→1 的过渡不变);
- **区域内附加密度地板按 `scale` 下调**(`layers = 强度 × mapVal`,stock 侧乘 scale);
- 地板下降后,3D Worley 的 `shape` 项能把总密度压到覆盖阈值以下 → **内部出现空洞与密度梯度**,"一大片实心云"被拆成有结构的蓬松云。

```hlsl
float4 stockCov = stockBand * stockDensityScale;                       // 仅附加密度层
float4 mapVal = lerp(float4(planetMap...), stockCov, stockEff * valid); // layers 用缩放值
float4 dist   = lerp(float4(1,1,1,1), stockBand, stockEff * valid);     // 形状门/边缘用原值
```

- **默认零回归**:`stockDensityScale=1` → `stockCov=stockBand` → 与现状**逐字节一致**。
- 只作用于自带云足迹内,全局 `coverage`(planetMap 基线阈值)完全不动。

---

## 1. 问题背景与根因

### 1.1 现象

开启「使用游戏自带云分布」后,部分区域被 3D 体积云**完全填满成一大片实心云**;不美观,且实心区像素每帧打满光步进,开销浪费。

### 1.2 根因

游戏 Clouds cubemap 的 R/G/B = 低/中/高云密度,值域 [0,1],但游戏本体是按阈值烘成的**近 0/近 1 二值分布**。区域内 `stockBand≈1` → 密度公式里两条通路同时顶格:

```hlsl
mapVal = lerp(planetMap, stockBand, stockEff*valid);  // ① 附加项数据源
layers = cloudLayerStrengths * mapVal;                //     = 强度 × 1 → 横向均匀的"密度地板"
dist   = lerp(1, stockBand, stockEff*valid);          // ② shape 乘法门 = 1 → 形状不掏洞
totalDensity = shape·(Σ dist·falloff) + Σ layers·falloff;
return (totalDensity + cloudCoverage - 1) · cloudDensity;
```

- **① 附加项(主凶)**:`layers = 强度 × 1` 是横向均匀的高地板 → 区域内任何水平位置密度都不低于它。
- **② shape 门**:`dist=1` → 3D Worley `shape` 只是在这块地板上加小起伏,压不到阈值以下。

对比老 planetMap 通路:`mapVal = planetMap` 是软噪声,低值处 `layers→0`、`totalDensity` 落到阈值以下 → **自然形成空洞**。自带云二值把这条路堵死 → 整个足迹恒高于阈值 → 一大片实心。

### 1.3 数值示例(默认配置)

默认 `layerStrengths=(0.3, 2, 0, 0)`、层带中心 `falloff=1`、`shape∈[0,1]`、`coverage=-0.25`、`density=0.05`:

```
totalDensity = shape·2 + 2.3·scale
final = (shape·2 + 2.3·scale − 1.25)·0.05
```

| scale | final 密度范围 | 效果 |
|---|---|---|
| 1.0(现状) | `[0.0525, 0.1525]` | 全程正密度、动态范围小 → 实心云墙 |
| 0.5 | `[−0.005, 0.095]`(shape>0.05 处成云) | 边缘保留;内部密度 0→0.095 强变化 → 蓬松有结构 |
| 0.3 | 约 28% 区域(shape<0.28)无云 | 内部出现明显空洞,大片云被拆开 |

(数值仅供直觉,实际以进游戏调参为准。)

---

## 2. 方案 A 设计

### 2.1 核心公式

在 4 处自带云消费函数中,于 `stockBand` 计算之后、`mapVal`/`dist` 之前:

```hlsl
// 方案 A:区域内密度缩放(默认 1 → 恒等 → 现状逐字节一致;0 仍 0 → 足迹边缘保留)
float4 stockCov = stockBand * stockDensityScale;
// mapVal 用 stockCov(缩放后),dist 用 stockBand(未缩放,保边缘/保 shape 结构)
float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockCov, stockEff * valid);
float4 dist   = lerp(float4(1,1,1,1), stockBand, stockEff * valid);
```

要点:

- **边缘保留**:`dist` 用未缩放的 `stockBand` —— 足迹边界(0→1 的过渡)原样;`mapVal` 侧 `stockBand×scale` 在边缘也趋向 0,不会产生新的分界。
- **只压地板**:`layers = 强度 × (scale·stockBand)` —— 区域内的横向均匀密度按 scale 下降;`shape·dist` 项不受 scale 影响,保持原有 3D 结构强度 → scale 下调后形状反而更突出。
- **正交性**:与 `stockMaskInfluence`(外廓 mask)、`stockMapStrength`(lerp 系数)、全局 `coverage`(阈值)互不干扰;`stockMapStrength=0` 时新参数完全不参与。

### 2.2 参数

| 字段(CloudConfig) | shader uniform | UI 词条 | 范围 | 默认 | 语义 |
|---|---|---|---|---|---|
| `stockDensityScale` | `stockDensityScale` | `Volken.UI.StockDensityScale` | 0..1 | 1 | 区域内密度缩放:1 = 恒等(现状);下调 → 区域内附加密度下降 → 内部出现空洞/密度梯度,化解实心云墙;足迹边缘不变 |

- `scale≈0.8~0.9`:轻减,去掉最满的实心感;
- `scale≈0.5`:边缘保留、内部明显蓬松有结构;
- `scale≈0.2~0.3`:大片云被拆成稀疏云块/裂隙。

---

## 3. 与其它思路的对比

### 3.1 为什么不是"区域内覆盖阈值重映射"(初版,已弃)

初版 `stockCov = saturate((stockBand − coverage)/soft)`:
- `coverage>0` 会把低于阈值的足迹区域归零 → **足迹边缘向内收缩/被吃掉**,且若区域内部值≈1 则仍然整片填满(阈值只动边缘、动不了内部饱和)。
- 与需求"保留边缘、调内部密度"相悖 → 弃用(历史见 §9)。

### 3.2 为什么不是"区域内阈值偏置"

`cov = lerp(cloudCoverage, stockCoverageBias, inRegion)` 纯阈值平移:
- 区域内 `totalDensity` 已高出阈值 1~2 以上时,平移 ±2 仍整片填满 → 弱杠杆,既恢复不了内部结构也省不了光步进。

### 3.3 为什么不是覆盖分解 F2(rotDistStrength)

`./Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail.md` 的 F2 = `coverage ×= lerp(1, rotDistValue, strength)`:
- 该文档**未实现**(代码无 `rotDistStrength`/`biomeStrength`);
- 即便实现,自带云近二值 → F2 只做"区域内有没有云"的存在性门控,**削不薄足迹内部**。

---

## 4. 性能分析

- 主 march 有提前退出(`transmittance < 0.01` break,:687)→ 实心区主步进不长;
- **光步进 `SampleLightRay`(:549-572)无提前退出**,对每个 `density>0` 样本执行(:679)+ 阴影一次(:709);
- `stockDensityScale` 下调 → 实心云墙被拆散 → 打满光步进的像素占比下降 → 开销下降;
- 实现只加 1 次 `stockBand × scale`(每密度样本),开销可忽略;开关关闭/无自带云时仍在 uniform 分支内,零成本。

附带的独立优化(可选,正交):`SampleLightRay` 加 `if (d >= opaqueThreshold) break;`。

---

## 5. 实施接入点(已按此落地)

| 文件 | 改动 |
|---|---|
| `Clouds.shader` | 2 处 uniform 块(Clouds pass :237 / 轨道 pass :1004)加 `float stockDensityScale;`;4 个消费函数(`SampleDensity` :441-449 / `SampleDensityCheap` :528-536 / 轨道 `SampleDensityCheap` :1128-1133 / `SampleCoverageCheap` :1202-1207)插入 `stockCov = stockBand * stockDensityScale`,mapVal 用 stockCov、dist 用 stockBand;`return` 行不动 |
| `CloudConfig.cs` | 新字段 `stockDensityScale = 1f`(方案 B 字段后)+ `Clone` / `CopyFrom`(`CreateDefault` 按既有约定不列方案字段,靠字段初始化器) |
| `CloudLayer.cs` | `SetStaticShaderProperties` 下发 `mat.SetFloat("stockDensityScale", Mathf.Clamp01(...))` + 启动诊断串补 `stockDensityScale=` |
| `VolkenUserInterface.cs` | 方案 B 分组 `StockMapStrength` 后加 1 个滑条(0..1);**并同步到 extra 层分组**(把方案B/方案A 自带云整组设置补进 `CreateExtraLayerGroup`,两层控件完全一致) |
| `EN-US/ZH-CN/RU-RU.xml` | 词条 `Volken.UI.StockDensityScale` |
| `CloudRenderer.cs` | 每帧诊断串补 `stockDensityScale=` |
| 反射路径 | 无需改(`CloudReflectionRenderer` 复用 Clouds pass + `SetStaticShaderProperties` 自动同步) |

---

## 6. 边界与风险

| 项 | 说明 |
|---|---|
| 旧 XML 兼容 | 旧配置无新节点 → 默认 1 → 恒等,零迁移成本 |
| `stockMapStrength=0` | `stockEff=0` → `stockCov` 不参与 → 无论取值都逐字节一致 |
| 无自带云/开关关闭 | uniform 分支跳过 cubemap 采样,新参数零开销 |
| `stockDensityScale=0` | 区域内附加密度层归零 → 只剩 `shape·dist·falloff`(受 `coverage` 阈值裁切)→ 区域变成稀疏的 3D 形状碎片(语义上"区域内最稀") |
| 2D/3D 同步 | 4 处同源同步改;落地后须用 `orbitDebugMode=1` 分屏复验 |
| 反射一致性 | 反射路径自动同步,无需单独处理 |

---

## 7. 验收清单

1. 默认 `stockDensityScale=1` → 与现状**逐字节一致**(开/关自带云、`stockMapStrength` 任意值均无回归)。
2. 自带云星球(如 Droo):
   - 调低 `stockDensityScale` → **足迹边缘位置不变**,内部从实心变蓬松,出现空洞/密度梯度;
   - `scale` 很低 → 大片云被拆成稀疏云块;
   - 全局 `coverage`/`density` 行为与关闭自带云时一致。
3. `orbitDebugMode=1` 分屏:2D 轨道云与 3D 体积云轮廓同步。
4. 性能:实心像素占比与光步进占用随 scale 下调而下降。
5. Toggle 实时生效,A/B 肉眼对比;帧率无回退。

---

## 8. 参考

- [./Volken-方案B-游戏自带云作为全球分布形状.md](./Volken-方案B-游戏自带云作为全球分布形状.md) —— 自带云接入的原始设计(风险 1 已预告"后续可加独立补偿",现实现为 `stockDensityScale`)。
- [./Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail.md](./Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail.md) —— F2(未实现)与 §3.3 的对比。
- `Clouds.shader` `SampleLightRay`(:549-572) —— §4 附带优化的落点。

---

## 9. 设计演进(历史,勿复用)

初版(已弃)方案 A 用 `stockCoverage`/`stockSoft` 做阈值重映射:

```hlsl
float4 stockCov = saturate((stockBand - stockCoverage) / max(1e-4, stockSoft));
// mapVal 与 dist 均用 stockCov
```

- 语义:区域内覆盖阈值(0=不出云,1=全足迹)+ 软边宽度;
- 缺陷:`stockCoverage>0` 收缩/吃掉足迹边缘(与需求相悖),且内部饱和值时仍整片填满;
- 于 2026-09-06 被 `stockDensityScale`(区域内密度缩放,保边缘)取代;相关代码/词条已整体替换,勿再回退。
