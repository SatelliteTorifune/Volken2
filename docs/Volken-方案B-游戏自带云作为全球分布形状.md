# Volken 方案 B —— 用游戏自带云作为"全球分布形状"(保留其它参数)

> 日期:2026-08-23
> 分支:planetMap(当前工作树)
> 目标:在**不改变 Volken 现有体积云参数**的前提下,把游戏本体(SimpleRockets 2)的 Clouds cubemap 作为 Volken 的**全球分布形状**——"哪里出云、云多大、纬度带怎么走"由游戏图案决定,而云体本身仍由 Volken 的 3D Worley + 高斯层带 + 步进渲染。
> 关联:本方案区别于已放弃的 planet-map-based 分支(那是"全面复刻薄壳模型",结果令人失望)。本方案只"借分布、不换引擎"。

---

## 0. 结论

**可行,风险低。** 核心改动是把 shader 里"全球分布"的两个使用点(`layers` 项的数据源、`shape` 项的分布门)从 Volken 的 `PlanetMapTex` 换成游戏的 Clouds cubemap(按方向采样),**其余(垂直层带/高斯衰减、coverage/density 阈值、3D Worley、步进、光照、合成)一行不动**。
- `stockMapStrength = 0` 时输出与现状**逐字节一致**(纯回退);
- `stockMapStrength = 1` 时,云的出现位置/密集程度完全由游戏图案决定;
- 中间值可平滑混合,便于对比调参。

---

## 1. 数据源

| 项 | 说明 |
|---|---|
| 纹理 | `PlanetCubemapType.Clouds` cubemap(游戏在 CUBEMAP 阶段把 3 层云噪声链烘进 R/G/B,A=纬度/行星遮罩) |
| 通道语义 | R=低云密度, G=中云密度, B=高云密度, A=纬度带/水/地形遮罩(全部 clamp01 ∈ [0,1]) |
| 加载 | `PlanetCubemapUtility.LoadCubemap(data, PlanetCubemapType.Clouds, size, false)`;尺寸按画质档 `MaxSize/2^i` 逐档尝试(1024→512→256) |
| 回退 | 见"兜底机制"一节:① 无 cubemap(无 Clouds modifier / `renderClouds=false` / 画质档未生成)→ 整体回退;② cubemap 有但某云层不存在(缺层/dummy 全 0)→ 该层逐带回退;③ 三层全空 → 整包回退。均回退到 Volken 的 `PlanetMapTex`,行为与现在完全一致 |
| 对齐 | cubemap 烘在**星球本体系**;Volken 的 `dir` 在**参考系**。每帧用 `ReferenceFrame.FrameToPlanetVector` 构造 3×3 旋转矩阵 `planetToBody`,采样前把 `dir` 转到本体系。另加 `stockAlignSign`(旋转方向符号)与 `stockAlignAngleOffset`(度)两个一次性微调旋钮 |

---

## 2. Shader 改动(`Clouds.shader`,仅 "Clouds" pass)

### 2.1 新声明

```hlsl
// Properties 块(必须,否则 SetTexture 失效):
StockCloudCube("Stock Cloud Cube", Cube) = "" {}

// CGPROGRAM 内:
TextureCube<float4> StockCloudCube;
SamplerState samplerStockCloudCube;
float useStockCloudMap;        // 0/1 总开关(由 C# 在无 cubemap 时强制 0)
float stockMapStrength;        // 0..1 混合强度
float stockMaskInfluence;      // 0..1 纬度遮罩影响
float stockAlignSign;          // ±1
float stockAlignAngleOffset;   // 度
float4x4 planetToBody;         // 参考系→本体旋转
```

### 2.2 采样辅助函数

```hlsl
float4 SampleStockDistribution(float3 dir, float windAngle) {
    // 风:东西风近似为绕 Y 旋转(替代 planetMap 的 UV 平移)
    float yAngle = windAngle + stockAlignSign * (stockAlignAngleOffset * 0.0174532925);
    float ca = cos(yAngle), sa = sin(yAngle);
    float3 sd = float3(dir.x*ca - dir.z*sa, dir.y, dir.x*sa + dir.z*ca);
    sd = mul(planetToBody, float4(sd, 0.0)).xyz;   // 参考系→本体
    return StockCloudCube.SampleLevel(samplerStockCloudCube, sd, 0);
}
```

### 2.3 在 SampleDensity / SampleDensityCheap 中(两处同改)

```hlsl
float4 stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
float  eff    = useStockCloudMap * stockMapStrength;      // 0 时完全回退
float  mask   = lerp(1.0, stock.a, stockMaskInfluence * eff);
// 分布层选择:0=低云(R), 1=中云(G), 2=高云(B), 3=按层对应(默认)
float  sel    = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
float4 band   = lerp(sel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer)) * mask;

// ① layers 数据源:planetMap ⇄ stock 平滑切换
float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), band, eff);
layers.x = cloudLayerStrengths.x * mapVal.x;
layers.y = cloudLayerStrengths.y * mapVal.y;
layers.z = cloudLayerStrengths.z * mapVal.z;
layers.w = cloudLayerStrengths.w * mapVal.w;

// ② shape 分布门(方案 B 核心):shape 按游戏图案逐带缩放
float4 dist = lerp(float4(1,1,1,1), band, eff);
float totalDensity = shape * (dist.x*falloff.x + dist.y*falloff.y
                              + active.z*dist.z*falloff.z + active.w*dist.w*falloff.w)
                   + layers.x*falloff.x + layers.y*falloff.y
                   + layers.z*falloff.z + layers.w*falloff.w;
return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
```

> eff=0 ⇒ dist=1、mapVal=planetMap ⇒ 与现有公式完全一致。

---

## 3. 新增配置字段(`CloudConfig`)

| 字段 | 默认 | 说明 |
|---|---|---|
| `useStockCloudMap` | false | 总开关(UI 用 Toggle 切换,方便对比) |
| `stockMapStrength` | 1.0 | 0..1 混合强度 |
| `stockMaskInfluence` | 1.0 | 0..1 纬度/行星遮罩(A 通道)影响 |
| `stockAlignSign` | 1.0 | ±1 对齐旋转方向 |
| `stockAlignAngleOffset` | 0.0 | 度,一次性对齐微调 |
| `stockMapLayer` | 3 | 用游戏哪一层云作为分布:0=低云(R),1=中云(G),2=高云(B),3=按层对应(默认) |

> 全部为新字段,旧 XML 反序列化后为默认值 → 行为不变;只在需要时通过 UI 修改。

---

## 4. CPU 侧接线

| 文件 | 改动 |
|---|---|
| `StockCloudMap.cs`(新) | 静态类:缓存 `Current` cubemap;`LoadFor(IPlanetNode)` 按画质档加载;`Release()` 释放 |
| `Volken.cs` | 进入有大气 SOI → `StockCloudMap.LoadFor(craftNode.Parent)`;进入太阳/无云 SOI → `Release()` |
| `CloudRenderer.cs` | `SetLayerDynamicProperties` 每帧:绑定 `StockCloudCube`、构造 `planetToBody` 矩阵 |
| `CloudLayer.cs` | `SetStaticShaderProperties`:下发 `useStockCloudMap/stockMapStrength/stockMapLayer/stockMaskInfluence/stockAlign*`(无 cubemap 时 `useStockCloudMap=0`) |
| `VolkenUserInterface.cs` | Main 层新增 "游戏自带云分布" 分组:Toggle + 分布层下拉 + Strength/Mask/Align 滑条 |

---

## 5. UI(对比用)

- **Toggle 「使用游戏自带云」**:开/关即对比"游戏分布 vs Volken 分布",同一时刻其它参数不变。
- **下拉「自带云分布层」**:选择用游戏哪一层云作为分布 —— 低云 / 中云 / 高云 / 按层对应。
- 滑条:Strength(0..1)、Mask 影响(0..1)、对齐角(-180..180)、对齐方向(-1..1 整档)。
- 新增本地化 key:`Volken.UI.UseStockCloudMap`、`Volken.UI.StockMapLayer`(+ 四个选项)、`Volken.UI.StockMapStrength`、`Volken.UI.StockMaskInfluence`、`Volken.UI.StockAlignSign`、`Volken.UI.StockAlignAngleOffset`。

---

## 5.5 兜底机制(缺层/无云 fallback 老 Volken)

**目标**:别的星球没有云、或没有三层云时,不依赖自带云数据的那部分自动回退到老 Volken 分布。

- **加载期检测**:`StockCloudMap.LoadFor` 加载 cubemap 后,用低 mip(约 32²/面)采样 6 个面,算出各通道最大值,得到 `LayerValid`(R,G,B,A)=(低云,中云,高云,遮罩)的 0/1 存在性。
- **整体回退**:三层 R/G/B 全空(`LayerValid` 的 xyz 全 0,如 dummy 空 cubemap)→ 丢弃 cubemap、返回 null → `useStockCloudMap` 强制 0 → 完全老 Volken。
- **逐带回退**:某层不存在(如只做了低云、或该层 inputChannel=-1)→ 该 Volken 层的 `mapVal` 用 `planetMap`、分布门 `dist=1`(不门控),即该层保持老 Volken;其它有数据的层仍用游戏分布。
- **遮罩兜底**:A(纬度/行星遮罩)不存在 → `stockMaskValid=0` → mask 置中性(=1),不会把整片云乘成 0。
- **单通道选择兜底**:选"中云层/高云层"而该层不存在时,`selValid=0` → 所有层回退 planetMap,不会出现"整片零云"。
- shader 实现:`mapVal = lerp(planetMap, stockBand, stockEff * valid)`、`dist = lerp(1, stockBand, stockEff * valid)`(`valid` 为逐带 0/1);仍受 uniform 分支保护,关闭时零开销。

## 6. 预期效果与风险

- **预期**:"Volken 的蓬松体积云 + 游戏的全球分布/纬度带"。云的位置、大小、纬度带与游戏一致,体积与光照仍是 Volken。
- **风险 1(已知)**:coverage 阈值与 stock 值域([0,1])的平衡。若实测出现"零云/整片糊",可先调 `stockMapStrength` 或微调 `coverage`;后续可加 `stockCoverage` 独立补偿(本次不做,保持精简)。
- **风险 2**:对齐约定(参考系→本体系)需进游戏实测一次;已有 `stockAlignSign/AngleOffset` 旋钮兜底。
- **性能**:每次密度采样多 1 次 cubemap SampleLevel(比 3D Worley 便宜)。**重要**:该采样被 `if (useStockCloudMap > 0.5)` uniform 分支包裹——开关关闭或星球无自带云时整段采样被跳过,零额外开销(修复了初版"无条件采样导致关掉开关仍变卡"的回归)。开启时按每次密度采样计,代价为一次 cache 友好的 cubemap 读取;若仍觉卡,优先调低 `numLightSamplePoints` / `resolutionScale`(现有质量滑条)。

---

## 7. 验收清单

1. 无云星球 / 关开关 → 与当前程序化云一致(无回归)。
2. 有云星球(Droo 等)开开关 → 云团分布/纬度带与游戏自带云一致,南北对称。
3. Toggle 切换实时生效,可肉眼对比。
4. 帧率无明显下降。