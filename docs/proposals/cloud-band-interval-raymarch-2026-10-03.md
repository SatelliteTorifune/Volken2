# Volken2 提案 —— 解析云带区间求交,把 raymarch 步进预算集中在云带内

> 状态:📋 提案(已论证,待拍板;未动手)
> 日期:2026-10-03
> 关联:[`../README.md`](../README.md) §四之三(默认关偏好)· [`cloud-optimization-roadmap-2026-08-28.md`](cloud-optimization-roadmap-2026-08-28.md) #8「层壳相交区间排序」
> 参考:theplatecrafter/Ringworld-Clouds-KSP(公开仓库)`RingCloudVolume.cginc`(`cloudShellRoots` / `cloudIntervals`)
> 范围:本项目 `Assets/Scripts/Volken/Clouds/Shader/Clouds.shader`(Clouds pass)+ `CloudRenderer.cs`(可只加 2 个 uniform 到 `SetLayerDynamicProperties`)

---

## 0. 结论速览

把 Ringworld 环世界云渲染里"**先用解析求交算出云带在射线上的区间,再把步进预算只花在区间内**"的技术移植到 Volken 的球面几何。收益:高空/远距视角下 raymarch 不再把步数浪费在云带外的空壳段;代价:一条近似零风险的 shader 改动 + 一处待定的边界处理(高斯层带 tail)。**尚未动手**——需先拍板边界取法、默认开关、与现有 `stepSizeFalloff` 的关系。

---

## 1. 动机与现状

### 1.1 Volken 现状:步进范围 = 地表 ↔ 云顶壳,预算可能浪费大半

`Clouds.shader` Clouds pass 的步进范围由 `RaySphereIntersect(camPos, viewDir, surfaceRadius)` 与 `RaySphereIntersect(camPos, viewDir, surfaceRadius + maxCloudHeight)` 的两组交点 + 场景深度组合而成:

```hlsl
float2 intersect    = RaySphereIntersect(camPos, viewDir, surfaceRadius + maxCloudHeight);
float2 surfIntersect= RaySphereIntersect(camPos, viewDir, surfaceRadius);
startRayDist = surfIntersect.x * surfIntersect.y < 0.0 ? surfIntersect.y : max(0.0, intersect.x);
maxRayDist   = surfIntersect.y > 0.0 ? surfIntersect.x : intersect.y;
maxRayDist   = min(maxRayDist, depth);
```

即预算覆盖**从地表(或云壳)到云顶壳**的整段视线。而实际密度只出现在 `layerHeights ± layerSpreads` 的高斯带里(`maxCloudHeight` 是步进上限,语义与"云带顶"不同,见 `CloudConfig.cs` 的 `TryGetBand` 注释)。典型场景:层带在 15–25 km,`maxCloudHeight = 35 km`,高空斜视时视线在云带上空/下方的空段里消耗大量步数。

现状的节流手段是**行进中**的:`stepSizeMultiplier=2`(离开云面 3 个空样本后加倍步长)+ `stepSizeFalloff`(随距离增大步长)。它们减小了空段的**单位成本**,但空段仍占用 `iter < 350` 的步数预算,而且**加倍的代价是空段大跨步,跨过云带时容易漏采薄层/卷云**。

### 1.2 参考:Ringworld 的解析区间裁剪

`RingCloudVolume.cginc` 对圆柱环几何做两步:

1. `cloudShellRoots(d, altitude)` 解析求半径 = altitude 的柱面与射线的两个交点;
2. `cloudIntervals(d, limit)` 取云带底/顶两个柱面的 4 个交点 + `{0, limit}` 共 6 个候选点,clamp 后排序,按相邻段中点的云高度是否落在 `[底,顶]` 内**合并成最多 2 段连续区间**(云带被环挡住时最多两段);

然后 volume pass:

```hlsl
float4 intervals = cloudIntervals(d, limit);
float distanceInCloud = 段1长 + 段2长;
float ds = distanceInCloud / max(1, _Quality.x);      // 步长 = 云内总长 / 步数
// 步进循环:二次分布(前密后疏),预算只花在云带内
float cellStart = distanceInCloud * f0 * f0;
float step      = distanceInCloud * (f1*f1 - f0*f0);
position = opticalPosition < 段1长 ? 段1.x + opticalPosition : 段2.x + opticalPosition - 段1长;
```

收益来源有二:
- **空域裁剪**:带外零步进(而不是"大跨步跳过"),同样预算全部转化为带内采样密度;
- **二次分布**:`f²` 让近端步更密、远端更疏,把预算进一步集中到近处云面。

Ringworld 之所以能放心硬裁,是因为它的云族密度有**硬边界 profile**(带内 `z∈[0,1]`,上下 `smoothstep` 到 0,见 `cloudTypeDensity`),带外密度严格为 0。

---

## 2. 方案:移植到球面几何

### 2.1 球面版区间求交(复用现有函数)

Volken 已有 `RaySphereIntersect(pos, dir, radius)`,球面几何下不需要新的解析式——云带底/顶就是两个同心球:

```hlsl
// 带底/顶 ASL 来自 CloudConfig.TryGetBand(out bottom, out top)(唯一实现),clamp 到 [0, maxCloudHeight]
float2 bandBottom = RaySphereIntersect(camPos, viewDir, surfaceRadius + bottomAsl);
float2 bandTop    = RaySphereIntersect(camPos, viewDir, surfaceRadius + topAsl);
```

候选点 `{0, maxRayDist, bandBottom.x, bandBottom.y, bandTop.x, bandTop.y}` 全部 clamp 到 `[0, maxRayDist]`,去重排序;相邻段中点的**球半径** `length(camPos + viewDir·tMid − sphereCenter) − surfaceRadius` 落在 `[bottomAsl, topAsl]` 即视为带内,合并成 ≤2 段(相机在带内时第一段从 0 起)。

### 2.2 步进预算集中 + 二次分布

- `distanceInCloud = 段1长 + 段2长`;为 0(射线完全在带外)→ 直接输出透明,零步进。
- 步长 `ds = distanceInCloud / 预算步数`,步进只在段内推进,到段尾跳到下一段起点。
- 二次分布(`f0²/f1²`)与 Ringworld 一致;与现有 `stepSizeMultiplier` 可共存(段内仍保留空区加倍),`stepSizeFalloff` 是否保留见 §4 决策点 3。

### 2.3 多相机 / 反射 / TSS 兼容

- 反射路径 `CloudReflectionRenderer` 复用同一 Clouds pass,自动生效;反射相机在带内时按"从 0 起"处理。
- TSS 历史/重投影只依赖采样**结果**(颜色、云面距离、MV),不依赖步进路径——区间裁剪不破坏历史有效性;**但**云面距离(首个密度>0 样本)可能因区间起点移动而变化,开关/配置切换时必须清历史(现有 `ClearHistory` 即可),否则出现割裂线。

---

## 3. 边界与风险(为什么不是直接照抄)

| 风险 | 说明 | 对策 |
|---|---|---|
| **高斯层带 tail 切边** | Volken 层带是 `exp(−(Δh/σ)²)` 高斯,无硬边界;`TryGetBand` 返回 `layerHeights ± layerSpreads`(≈±1σ,tail 仍延伸),按它硬裁会把带外残余密度切出硬边/云缘断口。Ringworld 有硬 profile 才敢硬裁 | 待拍板:① 区间外留 1–2 个缓冲步(带外稀疏而非零);② 或先全宽裁剪 + 回归看切边,再决定收窄系数 |
| **云面距离跳变** | 区间起点移到云内时,`cloudSurfaceDist` 从"旧起点"跳到"新起点",影响 Composite 近遮挡与 TSS MV | 区间起点取 `max(原 startRayDist, 段起点)` 的上界保持语义;切换配置时清历史 |
| **多层各自带** | Main/Extra 层带不同 → 每层用自己的 band,互不共享 | 按层独立计算(与路线图 #8 的"多层共享球壳排序"不是一回事,后者收益小的原因正是层带重叠) |
| **相机在带内 / 低空** | 带内场景区间基本覆盖全程,收益趋零,但不回退 | 保持恒开即可;回归只需确认无退化 |

---

## 4. 决策待拍板

- 【待拍板】**区间边界取法**:先全宽 `TryGetBand` + 缓冲步,回归看切边再定收窄系数;不做成玩家可见项。
- 【待拍板】**默认开关**:新增内部 `CloudConfig.useBandIntervalMarch`(默认关)做 A/B 像素对比;稳定后改默认开或删除开关(与"可明确关掉且默认关"的偏好一致,见 [`../README.md`](../README.md) §四之三)。
- 【待拍板】**二次分布 vs 现有 `stepSizeFalloff`**:二选一或叠加由像素回归决定;两者都改变远端步长,叠用需防远端步长过大漏采卷云。
- 【待拍板】**光步进不动**:`SampleLightRay` 已按云顶壳内长度自适应样本数(优化路线图 #1 已实现),本次不裁剪光照路径。

---

## 5. 回归判据与剩余项

- **像素回归**:同 seed/参数,开关两态逐像素/逐帧对比——密度、边缘、近遮挡一致(允许边界一个缓冲步内的差异);无硬切边、无云缘断口。
- **性能**:高空/远距视角下 `ProfilerOverlay` 的 CloudRenderInfo(平均样本数/GPU 时间)明显下降;近地/带内视角不回退。
- **三视角**:地面 / 云带内 / 云带上空各验一次;含相机恰在带边缘。
- **反射路径**:水面反射云(方案 A)同参数回归,反射云无切边/无消失。
- **TSS**:开关或配置切换瞬间清历史后无割裂线、无残影。
- **剩余项(不在本提案)**:多层带内排序(#8)、3D 噪声 mipmap + 距离 LOD(#4)、密度 LUT(#5)——均见 [`cloud-optimization-roadmap-2026-08-28.md`](cloud-optimization-roadmap-2026-08-28.md)。
