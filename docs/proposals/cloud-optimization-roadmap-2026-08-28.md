# Volken2 体积云优化点分析 —— 借鉴 VolRe(blackrack KSP EVE)与 KSAre(KSA)

> 2026-10-08 复核:当前排序和源码依据见 [帧率优化清单](frame-rate-optimization-audit-2026-10-08.md)。反射步长 ×5 / 光样本 2 已实现;每个 CloudLayer 独立绘制,单层 shader 内才有四个高斯带。下文长期方案和星级保留历史含义,不是当前收益实测;旧配置不能仅因取值等于默认值而被自动覆盖。

> 状态:📋 规划(部分已由 TSS / 轨道云实现,其余未排期)
> 核对:2026-10-08 更新 #1 光照与 #2 轨道分派现状;其它排名保留原分析口径,收益为估算,实施前须重新核对源码和 profiler。
> 分析日期:2026-08-28
> 关联:[雨实现记录](../archive/sp2-rain-particledomain-port-2026-09-28.md)(已归档;#2 距离淡出、#8 层壳相交排序与其雨渲染同源)
> 范围:
> - 本项目:`Assets/Scripts/Volken/`(CloudRenderer / CloudLayer / CloudNoise / CloudConfig / Clouds.shader / CloudNoiseCompute.compute)
> - 参考 A:`<VOLRE_REF>`(KSP 体积云,反编译 C#)
> - 参考 B:`<KSA_REF>`(KSA Vulkan 体积云,方案 C 移植母本)

---

## 0. 结论速览

Volken2 的「方案 C」主流程(低清全量 raymarch → MV 膨胀 → 时序上采样)已经与 KSA 对齐。
剩余候选集中在光样本数与旧配置迁移、自适应步长、噪声 mipmap、密度 LUT、Light Volume、PlaceRays。独立光步长和按海拔切换轨道云已有实现。

---

## 1. 综合排行

> 「性能开销」指该优化**新增**的成本,不是现状成本。
> ★ 越多收益越大;「—」表示该项对帧数无直接贡献。

| 排名 | 优化项 | FPS 收益 | 额外性能开销 | 移植难度 | 一句话理由 |
|---|---|---|---|---|---|
| 1 | 光照样本数与配置迁移 | ★★★★★ | 近零(反而大降) | 极低 | 独立光步长已实现;CreateDefault 为 25,剩余降低样本数与旧配置迁移 |
| 2 | 远距离淡出 / LOD 分级 | 已部分实现 | 轨道云过渡带双渲染 | 待评估 | `useOrbitClouds` 已按海拔分派;不恢复已删阈值字段 |
| 3 | 空域自适应步长连续化 | ★★★★ | 近零 | 低 | 已有离散 2 档雏形,升级为 base/max/factor 连续模型 |
| 4 | 3D 噪声 mipmap + 距离 LOD | ★★★☆(远处明显) | 一次性生成 mip | 低~中 | 远处云质量 + 带宽同时改善 |
| 5 | 密度/层形状 LUT 烘焙 | ★★★ | 一次性烘焙(启动时) | 低~中 | 砍掉每样本 4 层 exp + 部分纹理采样 |
| 6 | RT 格式升 HDR(ARGB32→ARGBHalf) | 无(纯质量) | 带宽略增,可忽略 | 极低 | 防色带/高亮溢出,历史缓存精度更好 |
| 7 | 反射探头 5× 步长 / 跳过 light volume | ★★★(有探头场景) | 近零 | 低 | 仅反射相机场景有效 |
| 8 | 层壳相交区间排序(march 区间裁剪) | ★★(多层分离时) | 近零 | 中~高 | 当前共享同一球壳收益有限 |
| 9 | MV 膨胀 3→4 次 + LastPass 修复 | 无(纯质量) | 一次全屏 Blit | 极低 | 减少时序重投影鬼影 |
| 10 | KSA PlaceRays(逐像素放新射线) | ★★(间接) | +2 个低清 compute pass | 高 | 提升 TSS 质量,同质量可降 upscale 系数 |
| 11 | Light Volume(光照体纹理) | ★★★★★(天花板最高) | +每帧 3D 光照体预渲染 | 高~很高 | 彻底替代逐样本光照 march |
| 12 | 截图模式多次降噪 | 负(仅截图瞬间) | 截图时 N 次全清累积 | 低~中 | 只为截图质量 |
| 13 | Non-tiling 噪声 / Curl noise / FlowMap | 无(纯质量) | 生成期/采样略增 | 中 | 消除平铺、提升细节 |

---

## 2. 分档

### T0 — 优先评估(未排期)
1. **光样本数**(#1):选默认值并评估旧配置迁移;独立光步长已完成。
2. **距离分派**(#2):先复用现有轨道云方案做性能对照,再决定是否还需要额外 LOD;旧 low/mid/highAltitudeThreshold 已删除。

### T1 — 高性价比(1~2 个文件内可完成)
3. 自适应步长连续化(#3)
4. 噪声 mipmap(#4)
5. 密度 LUT(#5)

### T2 — 质量项(别指望 FPS,按需做)
6. HDR 颜色(#6)、MV 修复(#9)、非平铺/curl/flowmap(#13)

### T3 — 长期/重工程
7. Light Volume(#11,预计算光照体积与当前逐样本光步进的取舍)
8. PlaceRays(#10,下一个「方案 D」级别,质量型)

---

## 3. 优先评估项与验证代价

### 首选:#1 光照样本数与配置迁移

- **2026-10-08 源码核对**:`Clouds.shader` 已用独立 `lightStepSize`,样本数按壳内长度自适应;`CloudConfig.CreateDefault()` 为 **25**,`CreateAnotherDefault()` 为 **5**。不再重复实施光步长解耦。
- 剩余提案:默认值采用 6 或 12,以及是否迁移旧 XML;代价见 [热点清单](hotpath-optimization-backlog-2026-10-02.md) §4.2 / §5。
- 调整仅影响光照步进预算,仍须对比内部阴影、散射和 GPU 耗时;收益是待实测假设,不能保证画面不变。

### 次选:#2 远距离淡出 / LOD 分级

- 当前 `useOrbitClouds` 已通过 `orbitTransitionStartAltitude/EndAltitude` 分派体积 / 轨道云,高于过渡带可跳过体积 raymarch;见 [轨道云记录](../archive/orbit-clouds-crossfade-2026-08-27.md)。
- 若仍有性能需求,先测关闭 / 开启轨道云与过渡带的耗时,再评估额外步长分级;不直接照旧方案恢复废弃字段。

### 第三:#3 空域自适应步长连续化

- **为什么视觉可接受**:空样本区域本来就没有云,步长加大不改变任何可见密度;
  只在命中云面时回退基础步长,云内视觉不变。
- **为什么帧数明显**:大幅减少云外/云间的空步进次数。
- **改动点**:把 Clouds.shader 里离散的 stepSizeMultiplier(连续 3 空样本后 ×2)
  升级为 baseStepSize / maxStepSize / adaptiveStepSizeFactor 连续模型(参考 VolRe ApplyShaderParams、KSA StaticLayerData)。

---

## 4. 详细说明(按排名)

### #1 光照解耦(VolRe lightMarchSteps / KSA LightSamples=6)
- VolRe 把光照拆成独立参数:lightMarchSteps(典型 6)+ stepSizeLight = LightMarchDistance / LightMarchSteps(粗步长)。
- KSA RaymarchingReference.cs 默认 LightSamples = 6,LightDistance 独立于主步长。
- Volken2 现状:独立 `lightStepSize`,壳内长度自适应采样;默认配置 25 / 另一配置 5,详见 §3。

### #2 距离淡出 / LOD
- VolRe CloudsRaymarchedVolume.checkVisible:scaledFadeStartAltitude/EndAltitude,超高度直接 SetActive(false) + 淡出。
- KSA RenderWithCompute:高于 OrbitTransitionStartAltitude 只画 2D 云层,低于 OrbitTransitionEndAltitude 才跑体积云。
- Volken2 现状:`useOrbitClouds` 与过渡高度已接入默认值、Clone、CopyFrom 和渲染分派;旧高度阈值字段已删除。

### #3 自适应步长(VolRe/KSA 三参数模型)
- VolRe CloudsRaymarchedVolume.ApplyShaderParams:baseStepSize / maxStepSize / adaptiveStepSizeFactor。
- KSA StaticLayerData:RaymarchingStepSize / RaymarchingStepSizeIncreaseFactor / RaymarchingMaxStepSize。
- Volken2 现状:离散 2 档(stepSizeMultiplier 1→2)+ detailCutoffDist + stepSizeFalloff 距离倍增。

### #4 3D 噪声 mipmap + 距离 LOD
- VolRe CreateRT:useMipMap=true; autoGenerateMips=false + RT.GenerateMips();生成时逐 slice + mip 链归一化。
- KSA:_noiseMipLevels = log2(128)+1,GenerateWorleyNoise() 后 GenerateMipmaps,采样器 MaxLod = mip 级数。
- Volken2 现状:GetWhorleyFBM3D 里 useMipMap=false;shader SampleLevel(..., 0) 固定 LOD0。

### #5 密度/层形状 LUT 烘焙
- VolRe ProcessCloudTypes / BakeCurvesTexture:把「高度→覆盖/密度」烘焙成 128×128 RG16 纹理,
  shader 用 GetPixelBilinear(cloudType, altitude) 一次采样,替代解析计算。
- KSA:CloudShapeLutTextureId(128 维 LUT)+ CloudTypeDataArray。
- Volken2 现状:每样本算 4 层 exp(-falloffExponent*falloffExponent) + atan2/acos 球坐标 + 多次纹理采样;
  SampleDensity 与 SampleDensityCheap 两段近似重复。

### #6 HDR 颜色
- KSA 颜色 R16G16B16A16SFloat;VolRe 颜色 DefaultHDR。
- Volken2 现状 cloudTex/upscaledCloudTex/historyTex 为 ARGB32(LDR 8bit)。

### #7 反射探头处理
- VolRe 反射探头用 5× 步长、跳过 light volume、可选不渲染。
- Volken2 现状走 OnRenderImage,未区分反射相机。

### #8 层壳相交区间排序
- VolRe ResolveLayerOverlapIntervals / KSA SortLayerIntersections:把多层球壳按半径排序成区间,
  只 march 每个 shell 区间,重叠层用第一层/最后一层标记。
- Volken2 现状:多层单 pass 一起算;当前共享同一球壳收益有限。

### #9 MV 膨胀/修复
- KSA MOTION_VECTORS_DILATE_PASSES = 4,带 Iteration / LastPass push constant。
- VolRe ApproximateMotionVectors(4 次迭代)给没有上一帧对应像素的新射线推 MV。
- Volken2 现状:3 次 3×3 反距离加权 Blit(DilateMV)。

### #10 PlaceRays(KSA)
- KSA PlaceRays + PlaceRaysRepair 两个 compute pass,产出 R8 rayPlacementIndex + R8 rayPlacementTentative。
- 上采样 shader 绑定 rayPlacementIndex 逐像素决定「本帧放新射线」,替代固定 cell。
- 附带 FallbackTemporalReprojectionMatrix / ReduceFlickeringDistance / 时域抖动。
- Volken2 现状:固定 _SampleCell 整格写新鲜值。

### #11 Light Volume(VolRe)
- LIGHT_VOLUME_ON/OFF 关键字:预计算 3D 光照体纹理,替代逐样本光照 raymarch。
- 收益最大,但需要每帧/每相机的 light-volume 预渲染路径,工程量大。

### #12 截图模式降噪(VolRe)
- screenshotModeIterations = 8:截图时全清多帧累积降噪。

### #13 Non-tiling / Curl / FlowMap(VolRe + KSA)
- NonTiling3DNoise 消除 3D 噪声平铺重复;curl noise / flow map 替代手写 domain warping。

---

## 5. 实施顺序

1. 选择 numLightSamplePoints 默认值并评估旧配置迁移(光步长解耦已完成)
2. 对照现有轨道云分派,评估是否还需要额外 LOD
3. 自适应步长连续化
4. 3D 噪声 mipmap + 距离 LOD
5. 密度/层形状 LUT 烘焙
6. RT 格式升 HDR
7. (可选)MV 4 次修复 / 反射探头处理
8. (长期)KSA PlaceRays
9. (长期)Light Volume
