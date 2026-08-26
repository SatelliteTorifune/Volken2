# Volken 方案 C —— 移植 KSA 体积云技术到 Unity BIRP（时序超采样为核心）

> 日期:2026-08-24
> 适用管线:Unity **Built-in Render Pipeline (BIRP)**（Volken 现状:OnRenderImage / Graphics.Blit / _CameraDepthTexture）
> 目标:把 KSA(Kitten Space Agency) 体积云管线中可复用的技术,按 BIRP 的 API 与约束翻译成 Volken 的实现方案。**一期只做"时序超采样 + 最优采样序列"这一件事**(收益/成本比最高),运动矢量膨胀、FlowMap 风场、照地云影列为后续。
> 关联:本方案与 方案B(游戏自带云做全球分布) 正交,可叠加:时序超采样管的是"**怎么采样/怎么累积**",方案B管的是"**分布形状用什么数据**"。
> 参考实现:KSA `Atmosphere/Rendering/CloudRenderer.cs`、`UpscalingPixelSequence.cs`、`ShaderStructs/*.cs`(反编译源码,只读 C# 侧;shader 本体为 SPIR-V 不在源码内,本方案按 C# 侧逻辑与惯例重建等价 HLSL)。

---

## 0. 结论

**可行,且 Volken 现有骨架已具备 70% 前置条件**(每层独立 material/RT、历史缓冲 `historyTex`、`reprojMat`、深度感知上采样都在)。核心改动是把 KSA 的三板斧搬进 BIRP:

1. **每帧只步进 1/N 的低清像素**(N=上采样倍数),其余像素由历史累积补齐;
2. **"最优采样序列"决定每帧步进哪一格子**(搬 `UpscalingPixelSequence` 的贪心最大最小距离算法);
3. **flip/flop 双历史缓冲 + 云空间重投影 + 运动矢量校验**,把多帧信息在时域上累积成近似全分辨率结果。

预期:相同 `resolutionScale` 下画质明显提升;或保持画质把分辨率再砍一档换性能。风险集中在"云在旋转/平移时历史重投影失效导致的鬼影/断层",方案内置云空间重投影与兜底。

---

## 1. BIRP 约束清单(先对齐前提)

Volken 跑在 BIRP 的屏幕后处理链路里,移植方案必须遵守这些约束:

| BIRP 事实 | 对方案的影响 |
|---|---|
| `[ImageEffectOpaque] OnRenderImage(RenderTexture source, RenderTexture destination)` 是全流程挂点 | 所有 pass 必须发生在 source→destination 的一次调用内;每一帧是独立的(source 不含上一帧) |
| `Graphics.Blit(src, dst, mat, pass)` 是片段着色器 pass 的载体 | 低清步进 / 膨胀 / 上采样 / 合成全部可写成 Blit pass;**不强制上 ComputeShader** |
| `_CameraDepthTexture`(非线性 Z)+ `LinearEyeDepth()` | 深度校验、上采样邻居选择沿用 Volken 现有 `combinedDepthTex/lowResDepthTex` 方案 |
| MSAA:后处理 source 进入 OnRenderImage 时已 resolve | 新建 RT 不必带 AA;低清 RT 尺寸按 `resolutionScale` 缩放即可 |
| `RenderTexture.GetTemporary/ReleaseTemporary` 管理临时 RT | flip/flop 历史缓冲用每层持久 RT(现状 historyTex 模式),临时 ping-pong 用 GetTemporary |
| BIRP 不内置相机运动矢量 | 用 Volken 已维护的 `prevViewProjMat` 在 shader 里反算历史 UV,无需新相机 hook |
| 球面/大位移:云随行星自转、随 `currentRotation`/`cloudOffset` 移动 | **历史重投影必须在"云空间"做**,不能只做世界空间(见 §5) |

> 取舍:compute 与 fragment 两版。方案正文给 **fragment(Blit)版**——与现状 100% 同构、改动最小、兼容所有 BIRP 平台;ComputeShader 版(如 KSA 的 8×8 dispatch)作为可选加速,`CloudNoiseCompute.compute` 已证明工程内 compute 可用,留到验证 fragment 版后再做。

---

## 2. 目标管线总览(一期)

```
OnRenderImage(source, destination)
├─ 0. 深度(现状不变):farDepth → combinedDepth → lowResDepth
├─ 1. 对每个 active layer:
│     ├─ SetLayerDynamicProperties(每帧,新增:当前帧采样格子 cell、云空间重投影矩阵)
│     ├─ ② 低清步进 pass(Clouds,改造):只对 cell == 本帧格子的低清像素做完整步进,
│     │      同时输出 4 张低清目标:color / motionVectors / distance / avgDensity
│     │      (未步进像素写"无效哨兵",alpha=0)
│     ├─ ③ (可选)repair pass:用邻域填补低清空洞,避免冷启动断层
│     └─ ④ 运动矢量膨胀(可选,3×3 或 4 遍)——防边缘鬼影
│     └─ ⑤ 上采样 pass(Flip 或 Flop):全分辨率逐像素
│             ├─ 本帧低清该格有新鲜数据 → 用新鲜值 + 写历史
│             └─ 否则 → 用云空间重投影取另一张历史缓冲,深度+运动矢量校验后混合
│             └─ 结果写入本轮 flip/flop 目标,同时合成到场景(source)
├─ 2. 逐层链式合成(现状 Composite 逻辑,基本不动)
└─ 3. Blit → destination
```

新引入的中间资源(每层):
- `lowResColor`(原 cloudTex 升级:可 RGBA 携带"有效位"),`lowResMotionVectors`(RG Half),`lowResDistance`(R Half),`lowResAvgDensity`(R Half,可选,本期可不用)
- `historyFlip / historyFlop`(低清或全清的双缓冲历史,替代现状单一 `historyTex`)
- `rayIndex`(低清,记录本帧哪些格是新鲜步进的,供上采样判断;也可用 color.alpha 哨兵代替)

---

## 3. 核心算法:最优采样序列(直接移植 KSA)

### 3.1 算法本体

KSA `UpscalingPixelSequence.FindOptimalSamplingSequence(gridX, gridY)` 是纯 C# 数学,零依赖,直接搬:

```csharp
/// 返回 0..gridX*gridY-1 的一个排列:
/// 每步选"与已选所有点(含周期镜像邻居)最小距离之和最大"的格子。
/// 效果:连续两帧采样的格子尽量远离,时域上互相补位。
public static List<int> FindOptimalSamplingSequence(int gridDimensionX, int gridDimensionY)
{
    int count = gridDimensionX * gridDimensionY;
    var order = new List<int> { 0 };
    var remaining = new List<int>();
    for (int k = 1; k < count; k++) remaining.Add(k);

    while (remaining.Count > 0)
    {
        int best = remaining[0];
        float bestScore = -1f;
        foreach (int candidate in remaining)
        {
            float score = 0f;
            foreach (int chosen in order)
                score += CalculateMinDistance(chosen, candidate, gridDimensionX, gridDimensionY);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        order.Add(best);
        remaining.Remove(best);
    }
    return order;
}

static float CalculateMinDistance(int prev, int cur, int gx, int gy)
{
    int px = prev % gx, py = prev / gx;
    int cx = cur  % gx, cy = cur  / gx;
    float min = float.PositiveInfinity;
    // 周期边界:邻居在 ±1 格(含镜像)里取最小距离 → 保证图块间无缝衔接
    for (int i = -1; i <= 1; i++)
    for (int j = -1; j <= 1; j++)
    {
        float dx = px - (cx + i * gx);
        float dy = py - (cy + j * gy);
        min = Mathf.Min(min, Mathf.Sqrt(dx * dx + dy * dy));
    }
    return min;
}
```

### 3.2 每帧取格子

```csharp
// CloudRenderer / CloudLayer 每帧:
int totalCells = upscaleX * upscaleY;              // 如 3×3 = 9
int seqIndex  = frameNumber % totalCells;          // frameNumber 从上次重建后累计
int cell      = samplingSequence[seqIndex];        // 0..8
int cellX = cell % upscaleX;
int cellY = cell / upscaleX;
mat.SetVector("_SampleCell", new Vector4(cellX, cellY, 0, 0));
mat.SetInt("_UpscaleX", upscaleX);
mat.SetInt("_UpscaleY", upscaleY);
// 抖动周期:每 totalCells 帧一个完整周期
mat.SetInt("_Cycle", frameNumber / totalCells);
```

> KSA 的 `CloudUpscalingData.UpscalingPixelIndices = GetCurrentFrameUpscalingPixelIndices(frameNumber)` 与 `DitheringNoiseDimensionsAndCycleNumber.W = frameNumber/(mx*my)` 就是这两件事。`UpscalingPixelSequence.FindOptimalSamplingSequence(...)` 在 KSA 只在重建时算一次,缓存成数组。

---

## 4. Shader 改动(Clouds.shader,以 BIRP Blit pass 为准)

### 4.1 低清步进 pass("Clouds")—— 子集步进

在现有 `frag` 开头加格子裁剪,只有匹配当前帧格子的像素才做完整步进:

```hlsl
// 新增 uniform
float2 _SampleCell;   // (cellX, cellY),本帧要步进的格子
float2 _Upscale;      // (upscaleX, upscaleY)
float4 _ReproMat[4];  // 云空间重投影(§5),替换/补充现状 reprojMat
float  _HistoryBlend; // 沿用现状 historyBlend

// vert 里把全屏 uv 换算成"低清格坐标":
// 步进 pass 的 RT 是低清的(cloudTex),uv∈[0,1]
float2 cellCoord = floor(i.uv * _LowResSize);     // 该像素属于哪个低清格
float2 inCell    = fmod(cellCoord, _Upscale);      // 低清格内偏移
bool  isFresh    = all(inCell == _SampleCell);

// 只对新鲜格做完整步进,其余输出哨兵
if (!isFresh)
{
    // 哨兵:alpha=0 表示"本帧无新鲜数据",上采样据此走历史路径
    return float4(0, 0, 0, 0);
}
// ... 以下为现有完整步进逻辑不变(SampleDensity / SampleLightRay / HG / 自阴影) ...
```

> 说明:这里的"格子"概念等价 KSA 的 `rayPlacementIndex`(低清每像素记录本帧属于哪个采样格)。KSA 用一张 index 图 + repair pass;一期先用 `alpha=0` 哨兵 + 上采样兜底,避免新增 index 图。若冷启动出现空洞断层,再补 §4.2。

### 4.2 (可选)repair pass

对应 KSA `PlaceCloudRaysRepair` 与 `_rayPlacementTentativeTarget`:对低清图里 alpha=0 的像素,取 3×3 邻域最近有效值。Blit 一屏即可,成本可忽略,主要解决"相机刚动/刚切星球时历史还不可用"的起步帧断层。

### 4.3 运动矢量输出(本期可只做最简版)

在步进 pass 的返回里顺带带出运动矢量:

```hlsl
// 用云空间重投影矩阵,把当前视点反投影到上一帧:
float4 prevClip = mul(_ReproMat, float4(worldPos, 1));
float2 prevUV   = 0.5 * prevClip.xy / prevClip.w + 0.5;
float2 curUV    = i.uv;                              // 低清坐标
return float4(lightEnergy * cloudColor.rgb, 1)  // color
     + float4(prevUV - curUV, 0, 0) * _WriteMV;  // motionVectors 打到另一张 RT
```

> 若一期不想扩 RT,可先跳过运动矢量,只保留深度校验(现状 `depthWeight`)。**但云在旋转时无运动矢量容易鬼影**,建议至少做 3×3 max 膨胀版(见 4.4)。

### 4.4 运动矢量膨胀(可选,3×3 或 4 遍)

KSA 用 `MOTION_VECTORS_DILATE_PASSES = 4` 的膨胀遍(`DilateCloudMotionVectorsCompute`)。BIRP 简化为上采样 pass 内对低清 MV 做 3×3 邻域 max(选择"最极端"的偏移),或用一张膨胀 pass 多次 Blit:

```hlsl
float2 mv = LowResMV.Sample(sampler, uv);
for (each 3×3 neighbor n) mv = abs(mv) < abs(mvN) ? mvN : mv; // 取最大位移,防边缘穿帮
```

### 4.5 上采样 pass("Upscale")—— 时序累积核心

全分辨率输出,逐像素决定"取本帧新鲜数据"还是"取历史重投影":

```hlsl
float2 lowUV = i.uv;                                 // 低清坐标(与步进 pass 同网格)
float2 cellCoord = floor(lowUV * _LowResSize);
float2 inCell    = fmod(cellCoord, _Upscale);
bool  isFresh    = all(inCell == _SampleCell);

float4 fresh = LowResColor.Sample(samplerLowRes, lowUV);

// 历史:用云空间重投影,把本帧像素映射到上一帧的云空间位置再采样
float4 prevClip = mul(_ReproMat, float4(worldPos, 1));
float2 histUV   = 0.5 * prevClip.xy / prevClip.w + 0.5;
bool  inBounds  = all(histUV >= 0) && all(histUV <= 1);
float4 history  = (UseFlip ? HistoryFlip : HistoryFlop).Sample(sampler, histUV);

// 深度/运动校验(沿用现状 depthWeight,可加 MV 阈值)
float depthOk = abs(curDepth - histDepth) / max(curDepth, 1e-3) < _HistoryDepthThreshold;

float4 result;
if (isFresh && fresh.a > 0.5)
{
    result = fresh;                                  // 新鲜数据优先
    // 把 fresh 重投影写入本轮历史(下一帧读取),并保留旧历史供下帧参考
}
else if (inBounds && depthOk)
{
    // 没有新鲜数据 → 重投影历史,做小幅混合平滑时序噪声
    result = lerp(history, fresh, _HistoryBlend);
}
else
{
    result = 历史不可用时的兜底(邻域新鲜值 / 直接 0 → 合成层忽略);
}

// 写本轮 flip/flop + 合成到场景(现状 Composite 逻辑)
```

**flip/flop 切换**(BIRP 版,对应 KSA `PerformUpscaling` 的 `writeHistoryToFlip` 交替):

```csharp
// CloudRenderer 每帧渲染完当前层:
if (writeHistoryToFlip)
    Graphics.Blit(lowResColor, historyFlip, upscaleMat, upscalePass);
else
    Graphics.Blit(lowResColor, historyFlop, upscaleMat, upscalePass);
writeHistoryToFlip = !writeHistoryToFlip;
```

> 上采样输出本身也要 flip/flop(全分辨率两张 `upscaledCloudColorFlip/Flop`),因为下一帧的历史读取必须来自"上一帧写的缓冲",交替读写避免读写同一张的竞争。KSA 为此有 8 张目标(颜色/MV/距离 × Flip/Flop);一期最少需要 **颜色 × Flip/Flop** 两张,距离/MV 的 flip/flop 可视进度后补。

---

## 5. 关键点:历史重投影必须在"云空间"做

这是 Volken 现状 `reprojMat = prevViewProjMat`(纯世界空间)的**已知缺陷**在时序方案里的放大版:

- 云不是静止的:每层有 `currentRotation`(自转)、`cloudOffset`(风平移)、行星自转。世界空间重投影假设"云在上一帧的世界位置 = 本帧的世界位置",云一旋转就全错 → 鬼影/断层。
- KSA 的做法(`DynamicLayerData.WorldToCloud` + `TemporalReprojectionMatrix` + `CloudUpscalingData.FallbackTemporalReprojectionMatrix`):把上一帧的 view-projection 与"云空间变换(上帧→本帧)"复合,即

```
ReproMat = prevViewProj * invert(prevWorldToCloud) * worldToCloud(本帧)
```

- Volken 落地:每帧在 `SetLayerDynamicProperties` 里构造两个矩阵——把当前采样点从世界空间转到**云本体系**(含 rotation + offset),再乘上一帧的 view-projection:

```csharp
// 云空间变换:世界→云本体(把 currentRotation 与 runningOffset 代入,与步进 shader 里 rotatedOffset 的算法一致)
Matrix4x4 worldToCloud = BuildWorldToCloud(layer.accumulatedRotation, layer.runningOffset);
Matrix4x4 reproj = layer.prevViewProjMat * worldToCloudPrev.inverse * worldToCloud;
mat.SetMatrix("_ReproMat", reproj);
worldToCloudPrev = worldToCloud;   // 存给下帧
```

> 若一期嫌矩阵复合复杂,可先做**近似版**:重投影时把历史 UV 沿"云自转方向"做角度偏移(即把 `_ReproMat` 简化为"上一帧视角 + 云旋转增量"),先跑通时序链路,再换完整矩阵。验收时重点观察"云缓慢旋转时是否拖影/断裂"。

> **实现状态(2026-08-24):近似版已落地。** 复现:时序开关关闭时,历史混合路径(Clouds pass 尾部,与 `_UseTemporal` 无关)仍用纯世界空间 `reprojMat=prevViewProj` 重投影 → 云自转/风平移时 `reprojUV` 采到旧位置的云 → 运动残影/鬼影,二值 `depthWeight` 翻动边界形成**水平割裂线**。修复(与上面完整公式等价):φ = `accumulatedRotation + 2π·runningOffset.x`(风平移折算为经度旋转),`reproj = prevViewProj * RotAroundCenter(C, +Δφ)`(`CloudRenderer.BuildCloudSpaceRepro`,逐层缓存 `prevCloudAngle`,RT重建/切天体时置 NaN 回退)。时序开/关共用该矩阵,两处重投影(新鲜路径 + `!isFresh` 路径)一起修好。
> **已知局限**:N/S 风(`cloudOffset.z` 纬度相关平移)不是刚体 Y 旋转,近似版未覆盖;若开强南北风仍见残影,需走完整 worldToCloud 矩阵或在该方向做额外补偿。

---

## 6. CPU 侧改动清单

| 文件 | 改动 |
|---|---|
| `CloudLayer.cs` | `historyTex` 升级为 `historyFlip/historyFlop`(低清,与 cloudTex 同尺寸);新增 `prevWorldToCloud`;`CreateRenderTextures/ReleaseRenderTextures` 相应扩展 |
| `CloudRenderer.cs` | 每帧:算 `frameNumber/totalCells`、从缓存序列取 `_SampleCell`、构造云空间 `_ReproMat`、flip/flop 切换、把上采样结果合成到场景 |
| `Clouds.shader` | 新增 `_SampleCell/_Upscale/_ReproMat/_Cycle` 等 uniform;"Clouds" pass 加子集裁剪;上采样 pass 改时序累积逻辑(§4.5) |
| `CloudConfig.cs` | 新增(可选,均带默认值保持旧配置兼容):`upscaleX/upscaleY`(默认 3×3 或取 `resolutionScale` 反推)、`historyBlend`(沿用)、`motionVectorDilate`(0/1)、`reduceFlickerDistance`(KSA `VolumetricsFlickerReductionDistance` 的等价项) |
| 新增 `UpscalingPixelSequence.cs` | §3.1 算法,重建时算一次缓存 |

> 兼容性:所有新字段默认值 = 关闭/1×1 → `_Upscale=1` 时 `totalCells=1`、每帧格子=0,`isFresh` 恒真,退化为"每帧全低清步进 + 现状上采样",行为与现状逐字节一致(纯回退,风险=0)。**建议把"序列周期、upscale 倍率、是否开启"都做成开关,方便 A/B 对比**。

---

## 7. 兜底与兼容(BIRP 特有)

1. **无新鲜数据的起步帧**:相机刚动/首帧/切星球 → 历史不可用。repair pass 补洞;仍不行则该像素直接取邻域新鲜值,不做历史混合(现状 `badSample → historyBlend=0` 的逻辑保留)。
2. **MSAA/HDR**:上采样输出的颜色若参与 HDR 合成,建议低清/历史 RT 用 ARGBHalf;若与现状一致用 ARGB32 也可,先保持现状格式以降低迁移面。
3. **多视口/多次 OnRenderImage**:Volken 目前单一主相机挂载,保持单一入口;若后续加缩略图相机,各自独立一组 flip/flop(用 layerIndex/相机 id 区分,避免串帧)。
4. **帧号重置**:切星球/切 SOI/重建 RT 时重置 `frameNumber` 与 flip/flop,避免历史来自不同行星。
5. **关闭开关**:`useTemporalUpscale=false` → 走现状路径,零新增 RT/开销。

---

## 8. 质量与性能预算

- **步进成本** ≈ 每帧只做 1/N 的低清像素(N=upscaleX×upscaleY,3×3 即 1/9)→ 主步进成本约降到原来的 11%,但多了历史读取与上采样混合。净效果:同画质更省,或同预算画质明显更好。
- **显存**:每层新增 2 张全分辨率(Flip/Flop)或低清历史,取决于实现;多层(Volken 常见 2~4 层)按层累加,建议历史 RT 用低清(与 cloudTex 同尺寸),全清只留上采样输出。
- **闪烁控制**:KSA 有 `ReduceFlickeringDistance`(离行星一定距离后降低时序混合,抑制远景闪烁)。Volken 用 `_HistoryBlend` 与 `reduceFlickerDistance` 等价项控制。

---

## 9. 风险

| 风险 | 缓解 |
|---|---|
| 云旋转/平移导致历史重投影失效(鬼影) | 云空间重投影(§5)为必做项;先近似后完整 |
| 冷启动/相机瞬移断层 | repair pass + badSample 兜底(§4.2/§7.1) |
| 多层各自 flip/flop 显存与复杂度上升 | 历史用低清;先 1 层验证再开多层 |
| 时序累积放大低清噪声(远景抖动) | ReduceFlickerDistance / historyBlend 按距离衰减 |
| shader 分支性能(子集裁剪在 fragment 里是动态分支) | 开关关闭时 `_Upscale=1` 走老路径;实测不达标再上 ComputeShader 版 |

---

## 10. 验收清单

1. 开关关闭 → 与现状**逐字节一致**(纯回退,无回归)。
2. 开启(3×3):静止视角下,静止云体画面随时间逐步锐化到近似全分辨率;无可见扫描网格。
3. 相机平移/旋转 + 云自转:无明显鬼影、断层、闪烁;边缘无运动矢量穿帮。
4. 冷启动(切星球/首帧):1~2 帧内收敛,无持久黑洞。
5. 帧率:同画质档下 ≥ 现状;或同帧率下 `resolutionScale` 可再降一档。
6. 与方案B(游戏自带云分布)同开,两者互不干扰。

---

## 11. 后续(低优先,独立成项)

- **运动矢量完整版**(低清 MV + 4 遍膨胀,§4.3/4.4)——时序质量上限的保障。
- **FlowMap 风场**(KSA `VolumetricFlowMap` + `FlowMapPhase` + `FlowMapNoise`)——替代统一平移,风感自然。
- **照地云影**(KSA `CloudShadowVolume` 256×32 角体积,每帧 24 片渐进烘焙 + 重采样)——二期独立子系统。
- **远距 2D 云壳**(KSA `OrbitTransitionStart/End` 切换)——轨道视角性能。

---

## 12. 实施状态(2026-08-25 更新)

### 已完成

| 项 | 位置 | 说明 |
|---|---|---|
| 最优采样序列 | `UpscalingPixelSequence.cs` | KSA 算法直接移植,格网变化时重建缓存 |
| 每帧 1/N 子集步进 | `Clouds.shader` `isFresh`(L584) | `_UseTemporal=0` 或冷启动帧恒真 → 纯回退 |
| 冷启动 | `CloudRenderer` frameNumber==0 | 该帧全步进(`_Upscale=1`),RT重建/切天体/切SOI 时重置 |
| 云空间重投影(近似版) | `CloudRenderer.BuildCloudSpaceRepro` + `prevCloudAngle` | 自转+东西风折算经度角,时序/非时序共用 |
| **割裂线根因修复(前置)** | `CloudRenderer.L282` | reprojMat 改用 GPU 投影,修好重投影 Y 镜像(时序/非时序同步受益) |
| `!isFresh` 重投影+深度校验 | `Clouds.shader` L617-665 | 校验通过才用重投影历史;失败→上一帧全分辨率 `PrevUpscaledTex` 兜底;再无→透明 |

### 已知缺口(建议后续)

- **flip/flop 双缓冲**(§4.5/§6):现为单 `historyTex`。当前架构 pass 内读历史、pass 后写历史,无读写竞争,单缓冲可用;若追求与 KSA 完全一致或排查跨帧串扰再升级。
- **运动矢量 + 膨胀**(§4.3/4.4):云自转时无 MV 的理论鬼影上限,靠深度校验 + 云空间重投影已基本覆盖;高速相机边角仍有极限情况,可后续补 3×3 膨胀。
- **repair pass**(§4.2):冷启动已被 frameNumber=0 全步进覆盖;运动瞬移靠 `PrevUpscaledTex` 兜底覆盖,暂不需要。
- **N/S 风**(§5 已知局限):非刚体 Y 旋转,强南北风下重投影近似失效,需完整 worldToCloud 矩阵。

### 验收建议(下次进游戏)

1. 关 `useTemporalUpscale` → 与本次修复前逐字节一致(纯回退)。
2. 开 3×3:静止视角静止云体,画面随时间锐化到近似全分辨率、无扫描网格。
3. 相机平移/旋转 + 云自转:无鬼影、断层、闪烁;割裂线不再复发。
4. 冷启动(切星球/首帧):1~2 帧收敛,无持久黑洞。
5. 帧率:同档位 ≥ 现状,或同帧率下 `resolutionScale` 可再降一档。
