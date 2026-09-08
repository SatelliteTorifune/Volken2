# Volken 方案 A —— `stockCoverage` 自带云"区域内覆盖"重映射

> 日期:2026-09-06
> 状态:**方案文档,未实现**(备用;落地时按 §5 接入点执行)
> 前置:方案 B(`useStockCloudMap` 游戏自带云分布)已实现并默认关闭
> 命名说明:本文"方案 A"= 自带云区域内的覆盖控制;与方案 B(自带云分布接入)、方案 C(时序超采样)并列为同一套编号体系。轨道云文档里的 "A 管线 / B 管线" 是另一语境(2D 壳着色的渲染路径取舍),与本方案无关。

---

## 0. 结论

新增 `stockCoverage`(+ 可选 `stockSoft`)两个参数,在 shader 内对游戏自带云分布值(`stockBand`)做一次**区域内重映射**,再喂给原有的 `mapVal`(附加项数据源)与 `dist`(shape 乘法门):

```hlsl
float4 stockCov = saturate((stockBand - stockCoverage) / max(1e-4, stockSoft));
// 之后 mapVal / dist 全部改用 stockCov(原 stockBand 仅此两处消费)
```

- **语义**:`stockCoverage` = 自带云足迹内"有多少比例会成云"(0 = 区域内不出云;1 = 全足迹 = 现状);`stockSoft` = 软边宽度(1 = 恒等 = 现状;越小边缘越硬、内部越容易掏空)。
- **默认零回归**:`stockCoverage=0, stockSoft=1` → `saturate(stockBand/1) = stockBand` → 与现状**逐字节一致**(同方案 B 一贯的"旧 XML 无节点 → 默认值 → 零回归"策略)。
- 作用域**只在自带云区域内**,全局 `coverage`(density 阈值)对 planetMap 基线的作用完全不动 —— 这正是"调整区域内而非全局的覆盖范围"。
- 附带收益:把近 0/1 二值化的自带云分布重新拉开动态范围 → 实心"云墙"恢复内部空洞与柔和边缘,同时削减光步进浪费(见 §6)。

---

## 1. 问题背景与根因

### 1.1 现象

开启「使用游戏自带云分布」(`Volken.UI.UseStockCloudMap`)后,部分区域被 3D 体积云**完全填满**:整片足迹渲染成一块实心、无结构的云墙,不美观;且这些像素每帧打满光步进,造成开销浪费。

### 1.2 根因(现状通路)

游戏 Clouds cubemap 的 R/G/B = 低/中/高云密度,值域 [0,1]。游戏本体是把多层云噪声**按阈值烘成近 0/近 1 的二值化分布**。`SampleDensity` / `SampleDensityCheap`(及轨道 pass 的同源函数)把它**同时喂进两条通路**:

```hlsl
mapVal = lerp(planetMap, stockBand, stockEff*valid);   // ① 附加项数据源
layers = cloudLayerStrengths * mapVal;                 //     layers = 强度 × 分布
dist   = lerp(1, stockBand, stockEff*valid);           // ② shape 乘法门
totalDensity = shape*(Σ dist·falloff) + Σ layers·falloff;
return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
```

- **① 附加项**:区域内 `stockBand≈1` → `layers = cloudLayerStrengths×1` → 每层强度顶格,密度下限被抬高。
- **② 乘法门**:区域内 `stockBand≈1` → `dist≈1` → 3D Worley `shape` 失去"掏洞"能力,只在顶上再加密度。

两者叠加使区域内 `totalDensity` 远高于 `cloudCoverage` 阈值、且几乎没有动态范围 → **整片足迹 = 实心云墙**。

而现有唯一覆盖旋钮 `cloudCoverage`(UI `Volken.UI.Coverage`,范围 -2..2)是**全行星阈值**:要削薄自带云区域,会把行星其它地方(planetMap 基线)的稀疏云也一并删掉 —— 因此需要区域内的独立覆盖参数。

### 1.3 数值示例(默认配置)

默认 `layerStrengths=(0.3, 2, 0, 0)`、层带中心 `falloff=1`、`shape∈[0,1]`:

| 场景 | totalDensity | 最终密度((d+cov-1)×density,cov=-0.25,density=0.05) |
|---|---|---|
| 现状 stock=1 区域 | `shape×2 + 2.3 ∈ [2.3, 4.3]` | `[0.0525, 0.1525]` —— 厚实、动态范围极小 |
| remap 后 stockBand=0.7(coverage=0.6, soft=0.2 → 0.5) | `shape×1 + 1.15 ∈ [1.15, 2.15]` | `[-0.005, 0.045]` —— **shape 低处出现空洞**,内部结构恢复 |

(数值仅供直觉,实际以进游戏调参为准。)

---

## 2. 方案 A 设计

### 2.1 核心公式

在 4 处自带云消费函数中,于 `stockBand` 计算之后、`mapVal`/`dist` 之前插入:

```hlsl
// 方案 A:区域内覆盖重映射(默认 0/1 → 恒等 → 现状逐字节一致)
float4 stockCov = saturate((stockBand - stockCoverage) / max(1e-4, stockSoft));
// 原 mapVal / dist 行中的 stockBand 替换为 stockCov(其余不动)
float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockCov, stockEff * valid);
float4 dist   = lerp(float4(1,1,1,1), stockCov, stockEff * valid);
```

要点:

- **区域识别不需要额外计算**:`stockBand` 本身就是"区域"(自带云足迹经 mask 后的值);remap 天然只作用于区域内,区域外(`stockBand≈0`)remap 后仍 ≈0,与现状一致。
- **两条通路同时受益**:①项恢复比例感(不再是满格强度),②门重新允许 3D 形状掏洞 → 云内空洞、边缘柔化,实心墙消解。
- **保持与 mask 正交**:remap 在 `stockMask` 之后做 → `stockMaskInfluence` 继续定义"允许成云的外廓",`stockCoverage` 定义"外廓内覆盖多少",两个旋钮互不干扰。
- **保持与 strength 正交**:`stockEff = useStockCloudMap × stockMapStrength` 仍在 lerp 系数位;`stockMapStrength=0` 时 `stockCov` 完全不参与 → 无论新参数取何值都逐字节一致。`stockMapStrength<1` 时 remap 只作用于 stock 一侧,planetMap 一侧不受影响。

### 2.2 参数

| 字段(CloudConfig) | shader uniform | UI 词条 | 范围 | 默认 | 语义 |
|---|---|---|---|---|---|
| `stockCoverage` | `stockCoverage` | `Volken.UI.StockCoverage` | 0..1 | 0 | 区域内覆盖阈值:0 = 足迹内不出云;1 = 全足迹(= 现状,配合 soft=1 为恒等) |
| `stockSoft` | `stockSoft` | `Volken.UI.StockSoft` | 0.01..1 | 1 | 软边宽度:1 = 恒等(现状);减小 → 边缘变硬、足迹收缩,区域内部重新被 3D 形状掏洞 |

- `stockCoverage=1` 但 `stockSoft<1`:足迹边缘向内收出渐变带(soft 宽度),内部靠近 1 的区域仍近乎满 —— 适合"保持足迹大、但边缘柔化"。
- `stockCoverage<1` 且 `stockSoft` 适中:足迹整体收缩 + 边缘渐变,区域内部密度按 `(stockBand-threshold)` 线性衰减 → 空洞自然出现。
- `stockCoverage>0` 且 `stockSoft` 极小(如 0.01):近似硬阈值 → 可控足迹大小的近二值分布(刻意要"硬"的一档)。

### 2.3 与现有参数的关系(正交性)

| 参数 | 作用域 | 本方案不动 |
|---|---|---|
| `coverage`(全局) | 全行星 density 阈值(planetMap 基线) | ✅ 语义、作用域完全不变 |
| `stockMapStrength` | stock 与 planetMap 的替换强度(lerp 系数) | ✅ 不变,remap 只作用 stock 一侧 |
| `stockMaskInfluence` | A 通道遮罩定义"允许成云的外廓" | ✅ 不变,remap 在其后 |
| `stockMapLayer` | 用哪一层(R/G/B/按层)作分布 | ✅ 不变 |
| `stockAlignSign` / `stockAlignAngleOffset` | 对齐微调 | ✅ 不变 |

---

## 3. 与其它思路的对比

### 3.1 为什么不做"区域内覆盖偏置"(阈值平移)

字面版方案:区域内把 `cloudCoverage` 换成另一个 bias 常量:

```hlsl
float cov = lerp(cloudCoverage, stockCoverageBias, inRegion);
return (totalDensity + cov - 1.0) * cloudDensity;
```

**缺陷(数学上可证明)**:区域内 `totalDensity` 已高出阈值 1~2 以上时,纯阈值平移 ±2 仍整片填满 —— 既恢复不了内部结构,也省不了光步进。它只对"足迹边缘"有效,对"实心填满"是弱杠杆。可作为方案 A 之后的补充微调,但**不能独立解决本问题**。

### 3.2 为什么不能只靠覆盖分解 F2(rotDistStrength)

`docs/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail.md` 里的 F2 = `coverage ×= lerp(1, rotDistValue, strength)` 乘法门控:

- 现状该文档**未实现**(代码中无 `rotDistStrength`/`biomeStrength` 等任何实现,仅方案文档);
- 即便实现,自带云近 0/1 二值化 → F2 只能做"区域内有没有云"的存在性门控,**无法削薄足迹内部、无法柔化边缘**——与方案 A 互补(能清掉自带云≈0 处的零散云),但替代不了 A。

---

## 4. 性能分析

### 4.1 现状浪费点

- 主 march 有提前退出(`if (transmittance < 0.01) break;`,Clouds.shader :687)→ 实心区主步进其实不长。
- **光步进 `SampleLightRay`(:549-572)无提前退出**(固定 `numLightSamplePoints` 次循环),且对**每个** `density>0` 样本执行(:679)+ 阴影再跑一次(:709)。
- 自带云一开 → 屏幕上大面积实心云墙 → 每帧有大量像素打满整条光步进,且每个光步进样本都是一次完整 `SampleDensityCheap`(含 cubemap 采样)→ 真正的"开销浪费"。

### 4.2 方案 A 的收益

- 区域内覆盖收缩/掏空后,`density>0` 的像素占比与光步进占用同步下降(实心墙 → 稀疏云块)。
- remap 只增加 1 次 `saturate + 除法`(每密度样本),远小于省下的光步进工作量;`useStockCloudMap=0` 时整段仍在 uniform 分支内,零开销(方案 B 已保证)。

### 4.3 附带的独立优化(可选,与方案 A 正交)

`SampleLightRay` 加 `if (d >= opaqueThreshold) break;`(累计光学厚度达 Beer 不透明阈值即停)——单独一项也能砍掉实心区的光步进浪费,零视觉回归,可作为后续独立小改动。

---

## 5. 实施接入点

### 5.1 `Clouds.shader`(唯一逻辑改动)

1. **uniform 声明,2 处**:
   - Clouds pass CGPROGRAM 的方案 B 块(:227-237)追加:
     ```hlsl
     float stockCoverage;        // 方案 A:区域内覆盖阈值 0..1
     float stockSoft;            // 方案 A:软边宽度 0.01..1(1=恒等)
     ```
   - 轨道 pass CGPROGRAM 的方案 B 块(:988-997)同样追加(2 个 CGPROGRAM 各自声明,互不共享)。

2. **4 个消费函数插入 remap**(均在 `stockBand` 之后、`mapVal`/`dist` 之前;`stockBand` 仅被 `mapVal`/`dist` 两行消费,替换即可,无其它引用):

   | 函数 | 位置(当前行号) | 说明 |
   |---|---|---|
   | `SampleDensity` | :425-464 | 主 march 近场(全量) |
   | `SampleDensityCheap` | :509-546 | 主 march 远场 + 光步进 |
   | 轨道 `SampleDensityCheap` | :1108-1140 | 2D 壳着色密度(与体积同源) |
   | `SampleCoverageCheap` | :1179-1207 | 2D 覆盖掩膜(与体积同源) |

   > 4 处同源同步改,保证 3D/2D 轮廓不脱节(项目文档反复强调的同步要求);改后可借 `orbitDebugMode=1` 分屏验证。

3. `return` 行(:464/:546/:1140/:1206-1207)**不动** —— 全局 `coverage` 通路原样保留。

### 5.2 `CloudConfig.cs`

- 字段(方案 B 字段区之后,:129 `stockMapLayer` 附近):
  ```csharp
  public float stockCoverage = 0f;   // 方案 A:区域内覆盖阈值(0=不出云,1=全足迹)
  public float stockSoft = 1f;       // 方案 A:软边宽度(1=恒等/现状)
  ```
- `CreateDefault`(:264-323):显式补 `stockCoverage = 0f, stockSoft = 1f`(不补也等默认,补上更明确)。
- `Clone`(:411-416 方案 B 字段区)与 `CopyFrom`(:480-485):各加 2 行。

### 5.3 `CloudLayer.cs` — `SetStaticShaderProperties`(:131-139 方案 B 区)

```csharp
mat.SetFloat("stockCoverage", Mathf.Clamp01(config.stockCoverage));
mat.SetFloat("stockSoft", Mathf.Clamp(config.stockSoft, 0.01f, 1f));
```

启动诊断串(:154-158)可选追加 `cov=`/`soft=`。

### 5.4 `VolkenUserInterface.cs`(方案 B 分组,:558-598)

在 `StockMapStrength` 滑条(:591-592)之后加 2 个滑条:

```csharp
CreateSlider(group, Locale.GetString("Volken.UI.StockCoverage"), () => cfg.stockCoverage,
    s => { cfg.stockCoverage = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
CreateSlider(group, Locale.GetString("Volken.UI.StockSoft"), () => cfg.stockSoft,
    s => { cfg.stockSoft = s; Volken.Instance.ValueChanged(); }, 0.01f, 1.0f, 2);
```

### 5.5 本地化(3 个 XML)

| key | EN-US | ZH-CN | RU-RU |
|---|---|---|---|
| `Volken.UI.StockCoverage` | Stock Coverage | 自带云区域内覆盖 | Покрытие карты облаков |
| `Volken.UI.StockSoft` | Stock Soft Edge | 自带云软边 | Мягкость края облаков |

### 5.6 `CloudRenderer.cs`(可选)

每帧诊断串(:483 `stock=`)追加 `.Append(" cov=").Append(c.stockCoverage.ToString("F2")).Append(" soft=").Append(c.stockSoft.ToString("F2"))`。

### 5.7 反射路径

**无需额外改动**:`CloudReflectionRenderer` 复用 Clouds pass,且每次渲染前 `layer.SetStaticShaderProperties(mat)`(:162)重新下发全部静态参数 → 新 uniform 自动同步到反射 clone。

---

## 6. 边界与风险

| 项 | 说明 |
|---|---|
| 旧 XML 兼容 | 旧配置无新节点 → 反序列化保留默认 0/1 → 恒等,零迁移成本(与方案 B 同策略) |
| `stockMapStrength=0` | `stockEff=0` → remap 结果不参与 → 无论新参数取值都逐字节一致 |
| 无自带云星球 / 开关关闭 | uniform 分支跳过 cubemap 采样,新参数不产生任何开销 |
| `stockCoverage=0` | 区域内不出云(含 `valid=1` 的层);`valid=0` 的层仍按现状回退 planetMap(逐带回退逻辑不动) |
| `stockSoft` 下界 | 公式 `max(1e-4, stockSoft)` 防除零;UI 下界 0.01 |
| 2D/3D 同步 | 4 处同源同步改;落地后必须用 `orbitDebugMode=1` 分屏复验 |
| 反射一致性 | 反射路径经 `SetStaticShaderProperties` 自动同步,无需单独处理 |
| 数值边界 | `stockCoverage=1` + `stockSoft=1` 时 `(1-1)/1=0` 会误杀全足迹 —— 但默认 0/1 为恒等,用户调到该组合属"刻意关闭",语义可接受(在 UI 词条中注明即可) |

---

## 7. 验收清单

1. 默认 `stockCoverage=0, stockSoft=1` → 与现状**逐字节一致**(开/关自带云、`stockMapStrength` 任意值均无回归)。
2. 自带云星球(如 Droo):
   - `stockCoverage=0` → 自带云足迹内不出云,足迹外(planetMap 基线)不受影响;
   - 调大 `stockCoverage` → 足迹从边缘向内收缩;
   - 调小 `stockSoft` → 边缘柔化/内部空洞重现,不再是实心墙;
   - `coverage`(全局)行为与关闭自带云时完全一致。
3. `orbitDebugMode=1` 分屏:2D 轨道云与 3D 体积云轮廓同步(同一 remap)。
4. 性能:记录 `density>0` 像素占比与光步进占用,削薄后应有明显下降。
5. Toggle 实时生效,可肉眼 A/B 对比;帧率无回退。
6. 无自带云星球 / 开关关闭 → 零开销(分支跳过)。

---

## 8. 参考

- `docs/Volken-方案B-游戏自带云作为全球分布形状.md` —— 自带云接入的原始设计(风险 1 已预告"后续可加 `stockCoverage` 独立补偿")。
- `docs/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail.md` —— F2(未实现)与本文 §3.2 的对比。
- `Clouds.shader` `SampleLightRay`(:549-572) —— §4.3 附带优化的落点。
