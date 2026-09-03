# Volken 轨道云(2D)方案与过渡带交叉淡入 —— 技术可行性分析

> 日期:2026-08-27
> 依据:KSA 源码(`C:/renko/shitProgram/KSAre/KSA/KSA/Atmosphere/Rendering/`)实读 + Volken 现行代码
> 关联文档:`Volken-方案C-KSA体积云技术移植(BIRP).md`、`Volken-体积云优化点分析-VolRe与KSA借鉴.md`(#2 距离淡出/LOD、#8 层壳相交排序)

## 0. 结论速览

- **可行,且工作量为中(1 个新 shader pass + renderer 分支 + config 参数)**,不需要动现有体积云/TSS 管线。
- 轨道云本质 = 一个全屏 Blit 的"云壳球面着色"pass(无 raymarch、无光步进、无时序),近免费。
- 过渡带交叉淡入 = 相机海拔 uniform + 一个混合因子,可在现有 Composite pass 内完成,不新增 RT。
- 最大风险不是性能,而是**视觉匹配**:2D 云与体积云的覆盖/光照在淡入处不能"变形"。用**同源密度采样**可化解(见 §3-B、§5)。

---

## 1. KSA 机制回顾(已核实源码)

### 1.1 每个云层有两套表示

| 表示 | 数据(CloudLayerReference) | 渲染路径 | 用途 |
|---|---|---|---|
| **VolumetricCloud** | 形状 LUT + detail 贴图 + flow map + 体积颜色,3D raymarch + 时序上采样 | `RenderVolumetricsWithUpscaling` | 地面/低空高精度 |
| **TwoDimensionalCloud** | 2D 颜色贴图(立方/单通道)+ 法线贴图 + 2D flow map,屏幕投影 | `TwoDimensionalCloudsPipeline`(compute) | 轨道/高空廉价表示 |

每层两张数据:`StaticLayerData`(512B,静态形状/步长/过渡高度/颜色/纹理句柄)+ 每帧 `DynamicLayerData`(512B,`WorldToCloud`、`TemporalReprojectionMatrix`、`WorldToPlanet`、噪波偏移、flow 相位、detail 帧)。

### 1.2 海拔分派(`CloudRenderer.RenderWithCompute`,L1168-1218)

```
camAlt = |camPos − planetCenter| − MeanRadius;
if (camAlt > OrbitTransitionStartAltitude)  → 每层 dispatch TwoDimensionalCloudsPipeline(2D 云)
if (camAlt < OrbitTransitionEndAltitude)    → RenderVolumetricsWithUpscaling(体积云 + 时序)
```

三带行为:

| 相机海拔 | 渲染 | 说明 |
|---|---|---|
| `< start` | 仅体积云 | 低空高精度 |
| `start ~ end` | **体积 + 2D 同时渲染** | 用 `TransitionStartAltitude/EndAltitude`(进 StaticLayerData,同时进 CloudShadowRenderData)交叉淡入 |
| `> end` | 仅 2D 云 | 轨道廉价格式 |

默认 `OrbitTransitionStartAltitude/EndAltitude = 1 AU`(`CloudsReference.cs` L15/L19)→ **默认只走体积云**,2D 路径靠星球模板按需开启。

### 1.3 每层壳体相交(`SortLayerIntersections`,L1498-1530)

每层定义 `BottomRadius/TopRadius = MeanRadius + Bottom/TopAltitude`;按相机相对壳体位置(壳内/壳下/壳上)生成近/远相交实例,汇总 `min/maxCloudRadius` 喂体积 raymarch 包围与 upscaling。

---

## 2. Volken 现状盘点(可直接复用的资产)

- **只有体积路径**:`Clouds`(低清全量 raymarch,MRT 颜色/云面距离/MV)→ `DilateMV`(3× 3×3)→ `Upscale`(全清时序)→ `Composite`(Additive/Standard 合入场景)。另有 `ReflectionComposite`。
- **无海拔分派**:任何高度都 march 全壳 `surfaceRadius → surfaceRadius+maxCloudHeight`;detail 只沿单 ray 自适应(`detailCutoffDist` / `stepSizeMultiplier` / `stepSizeFalloff`)。
- `CloudConfig.low/mid/highAltitudeThreshold`(10000/50000/150000)**只定义+序列化,零消费**——是死配置,且与 KSA 的"一对 start/end"设计不对应。
- 现成可复用:
  - 壳数学:`sphereCenter`(`CloudRenderer.cs:236`)、`surfaceRadius`(`:274`)、`maxCloudHeight`、`RaySphereIntersect`;
  - 相机:`_CamPos`(`:258`)、`clipPlanes`(`:265`);海拔 = `|_CamPos − sphereCenter| − surfaceRadius`,CPU/GPU 侧都现成;
  - 覆盖图:游戏自带云 cubemap `StockCloudCube`(renderer `:277` 已绑定);`CloudNoise.GetPlanetMap` 已产出**经纬 RG(密度/高度)覆盖图**;
  - 光照:现有 `phaseParams`、`lightDir`、`CloudPhase`、银边参数;
  - 合成:`Composite` pass 读 `UpscaledCloudTex`+`SceneDepthTex`+`_MainTex`,是 2D 云混合的天然落点。

---

## 3. 轨道云(2D)落地方案(三档实现强度)

### A. 纯 2D 投影(最接近 KSA 语义)

新 Blit pass `OrbitClouds`,全清逐像素:ray-sphere 求外/中壳交点 → 法线 = normalize(hit−center) → 采样覆盖图(StockCloudCube 或 PlanetMapTex 经纬)→ 简单光照(Lambertian + 现有 phase)→ 输出 RGBA 云层。
- 无 raymarch、无光步进、无时序 → 每像素 1 次壳求交 + 1~2 次覆盖采样,近免费;
- 轨道视角下足够(云体积占比小,贴皮误差不可辨);
- 风险:覆盖图若与体积噪声**不同源**,淡入处云形会突变。

### B. 复用体积密度做"单样本壳着色"(推荐)

管线同 A,但覆盖采样直接用 `SampleDensityCheap`(壳交点处 1 次 3D 噪声,不求交光步):
- 覆盖与体积云**完全同源** → 过渡带云形、边缘、大体光照一致,淡入几乎察觉不到"换了一种云";
- 成本 = 每像素 1~2 次 3D 噪声 + 壳求交,仍远低于 raymarch(轨道下甚至可用半清+双线性);
- 这是把"体积云"与"轨道云"视觉统一的最佳路径。

### C. 极简行星贴片

用 PlanetMapTex 直接当云层贴到球壳,仅遮挡+渐变着色。最廉价,但光照/立体感差,与体积云差异大,仅作回退/占位。

> **推荐:A 管线 + B 的密度采样**(同一覆盖函数)。若想借力游戏自带云分布,可把 StockCloudCube 作为 A 的覆盖源,但需接受它与体积 shape 不同源导致的淡入差异(见 §5 风险 1)。

---

## 4. 过渡带交叉淡入设计

### 4.1 海拔分派与混合

```
float camAlt = distance(_CamPos, sphereCenter) - surfaceRadius;
float orbitFade = smoothstep(orbitTransitionStartAltitude, orbitTransitionEndAltitude, camAlt);
// camAlt < start  → orbitFade = 0(纯体积云)
// start ~ end     → orbitFade ∈ (0,1)(两套都渲染,按 orbitFade 混合)
// camAlt > end    → orbitFade = 1(纯 2D 云)
```

### 4.2 实现落点(两个候选,均可行)

- **(a) 独立 orbitCloudTex + 混合**:`OrbitClouds` pass 输出 `orbitCloudTex`(全清),在 Composite 前加一个 blend(或直接在 Composite 内)按 `orbitFade` 在"体积云上采样结果"与"2D 云"之间 lerp。改动小,2D 云不进时序,天然无历史依赖。
- **(b) 直接进 Composite**:给 `_OrbitFade` 加因子,Composite 里 `clouds = lerp(volClouds, orbitClouds, orbitFade)`。最少改动,与现有 Additive/Standard 模式正交。

### 4.3 参数与配置

- 在 `CloudConfig` 新增(或激活已预留的)`orbitTransitionStartAltitude / orbitTransitionEndAltitude`;替代死字段 `low/mid/highAltitudeThreshold`(删除或弃用);
- 默认 **1 AU**(≈ 关闭 2D,完全保持现状)→ 分阶段上线零回归风险;
- 每层可选覆盖,建议 start ≥ 2×`maxCloudHeight`(云不可辨厚度的高度),end 取轨道高度(云整体压进 2D 仍可读)。

---

## 5. 逐项技术可行性评估(成本 / 改动 / 风险)

### 5.1 轨道云 `OrbitClouds` pass —— 可行性:高

- **成本**:全清 1 个 Blit,每像素 1 次壳求交 + 1~2 次覆盖采样 + 简单光照;轨道视角甚至可用半清+双线性。无同步、无回读、无时序开销。
- **改动文件**:`Clouds.shader`(+Pass "OrbitClouds")、`CloudRenderer.cs`(海拔分派 + 绑定纹理/uniform)、`CloudConfig.cs`(参数)。1~2 个文件内可完成,属优化点文档的 T1 档。
- **风险与对策**:
  1. *覆盖/形状匹配*(关键):淡入处 2D 与体积云必须同轮廓。→ 用 §3-B 同源密度采样;若用 StockCloudCube,需接受差异并靠较宽 band 掩盖。
  2. *无体积感*:2D 是"贴皮",低空露馅。→ band 内以体积云为主权重(orbitFade 曲线偏后);start 取较高值,2D 只在高空出现。
  3. *光照差异*:体积云有多重散射+银边,2D 用 Lambertian+phase 显平。→ band 内体积云主导;2D 阶段可复用 phase/银边参数提升一致性。
  4. *分辨率*:全清最稳;若半清需双线性上采样(不要走 TSS 时序——2D 无历史)。

### 5.2 过渡带交叉淡入 —— 可行性:高

- **成本**:1 个海拔 uniform + 1 个 `orbitFade` + Composite 内 lerp;无额外 RT(用候选 b)。
- **风险与对策**:
  1. *淡入突兀*:band 太窄 → 云形跳变。→ start/end 间隔取几十 km 级,并让 orbitFade 用 smoothstep。
  2. *双重渲染*:band 内体积+2D 都跑。band 通常短、2D 近免费 → 可接受。
  3. *与 TSS 历史交互*:2D 云不进时序。切到 2D 瞬间 Upscale 的历史校验失败 → 走"本帧兜底"(冷启动逻辑已存在,无需新代码)。band 内体积云仍正常时序累积。
  4. *阴影*:Volken 无体积云阴影,2D 阶段无阴影需求,无回归。

### 5.3 与优化点文档的关系

- `#2 距离淡出/LOD` 是"高空跳过/缩放 raymarch";本方案是"高空**换成 2D 表示 + 平滑过渡**"——两者互补:轨道云解决"高空还要不要 march",LOD 解决"低空怎么省钱"。
- `#8 层壳相交排序` 与 KSA `SortLayerIntersections` 对应,Volken 多层共享同一球壳,收益有限;若以后每层独立壳再上。

---

## 6. 实施里程碑(分阶段,零回归)

- **M0**:接线相机海拔 uniform + `CloudConfig` 新增 start/end(默认 1 AU = 关闭 2D);纯开关,行为不变。
- **M1**:`OrbitClouds` pass(推荐 B 同源密度采样)+ 高空强制 2D 路径(`camAlt > end` 时替代体积)。
- **M2**:band 交叉淡入(Composite 内按 orbitFade 混合)+ 调参(start/end/曲线)。
- **M3**:轨道视觉打磨(2D 光照与体积云对齐、与 StockCloudMap 的取舍、半清选项)。

---

## 7. 结论

- 轨道云 + 过渡带交叉淡入在 Volken(BIRP 后处理)上**技术可行**,核心是**一个不做 raymarch/时序的 2D 云壳着色 pass** + **一个海拔分派与混合因子**,可在现有 `Composite` 内完成。
- 工作量中等(1~2 文件),性能代价可忽略,分阶段(M0→M3)上线零回归。
- 关键成功因素:**2D 与体积云覆盖同源**(§3-B),band 内以体积云为主权重,过渡才不会"变形/露馅"。
- 顺带可清理:`CloudConfig.low/mid/highAltitudeThreshold` 死配置应删除或替换为 start/end 语义。

---

## 8. 实现状态(2026-08-27)

已按 M0→M2 落地,并把 M3 的调参旋钮做成配置项:

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | `CloudConfig` 新增 `useOrbitClouds`(默认关)/ `orbitTransitionStart/EndAltitude` / `orbitSampleAltitude` / `orbitDensityBoost` / `orbitBrightness`;接线相机海拔 + 每层 `orbitFade`(smoothstep) | ✅ |
| M1 | `Clouds.shader` 新增 `Pass "OrbitClouds"`(壳求交 + **同源** `SampleDensityCheap` 单样本 + Lambertian/phase);`camAlt > end` 跳过体积 raymarch/Upscale/历史 | ✅ |
| M2 | `Composite` 内 `clouds = lerp(volClouds, orbitClouds, _OrbitFade)`;band 内双渲染+交叉淡入;进入纯 2D 时清时序历史 | ✅ |
| M3 | 调参旋钮:采样高度/密度放大/亮度/起点/终点 + UI 组(主层与额外层)+ EN/ZH/RU 文案 | ✅ |

**KSA 2D 云参考改进(2026-08-27,第二轮):**
KSA 的 2D 云 = **烘焙颜色贴图 + 法线贴图 + Lambertian(默认 0.65)+ flow map + 大气 LUT**;我们无预烘焙贴图,改为**程序化同源等效**:
- **法线浮雕**(KSA normal-map 等效):密度场水平梯度扰动球面法线 → 云不再是"平白圆斑",而是有浮雕体积感(核心改动);
- **多高度覆盖**:云带内 3 个高度(层中心 ± spread)取 max → 与体积云"列"的垂直外观一致,单层切片不再露空;
- **细节纹理**:detail 噪声(×5 尺度)作为逐像素明暗变化 → KSA 颜色贴图的颗粒感;
- **半清渲染**(`orbitResolutionScale` 默认 0.5)+ Composite 双线性软化 → 软化程序化噪声颗粒、降本。
新配置:`orbitReliefStrength`(默认 1.5)/ `orbitDetailStrength`(默认 0.4)/ `orbitResolutionScale`(默认 0.5)。密度放大默认 30→25。

**关键实现细节(与 §3/§4 的偏差,均为必要修正):**
- §3-B 的"壳交点处采样"在外壳顶(r=surface+maxCloudHeight)高度 falloff≈0 会**恒黑**;实际在**代表性云层高度**采样(`orbitSampleAltitude=0` → 层强度加权层高,默认配置 ≈ 3108 m,落在 Layer2 带内)。
- §4.2 采用候选 **(b) 直接进 Composite**:`lerp(vol, orbit, _OrbitFade)`,不新增 RT/中间 blend;2D 不进时序,天然无历史依赖。
- 海拔分派 CPU 侧完成(全 GPU 无同步):`camAlt < start` 只跑体积;`start~end` 双渲染+淡入;`> end` 只跑 2D(体积整条跳过,性能大头)。
- 反射路径(`CloudReflectionRenderer`)恒 `_OrbitFade=0`(水面相机在低空)→ 零影响。
- `low/mid/highAltitudeThreshold` 死配置**未删除**:旧 XML 含这些节点,删除会导致 `XmlSerializer` 反序列化失败回退默认配置(回归风险),仅弃用不消费。

**验收要点:** 默认配置下 `useOrbitClouds=false` → 逐帧行为与之前完全一致;开启后低空无感、过渡带(默认 25~100 km)内云形不突变、轨道上体积 raymarch 停止。调参顺序建议:先调起点/终点带宽 → 再调亮度与密度放大对齐体积云 Additive 强度 → 最后按需固定采样高度。