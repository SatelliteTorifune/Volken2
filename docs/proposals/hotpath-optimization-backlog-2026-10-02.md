# 高开销点剩余清单 + 方案代价(2026-10-02)

> 状态:📋 规划 —— **未拍板、未排期**。A 组按用户决定**不做**(仅留档);D 组待选值/待拍板。
> 日期:2026-10-02
> 关联:[场景门控 + 每帧回调审计](../archive/monobehaviour-scene-gate-2026-10-02.md)(已落地部分;其 §3 的剩余项本文件不重复)· [云优化路线图](cloud-optimization-roadmap-2026-08-28.md)(其 §3 首选 #1 已同步现状,见 §5)· [雨/雷性能审计](rain-lightning-perf-audit-2026-10-02.md)(GPU 侧 §3 的原始条目)
> 定位:把"还有哪些高开销"分成 **A 拖 UI 尖峰 / B 每帧 CPU / C 一次性尖峰 / D GPU 大头** 四组落到可执行条目,并给出 **D 各方案的代价**(改动面 / 是否动 shader 或 compute / 是否重打 asset bundle / 真机验证点)。分析口径 = 源码追踪 + 量级估算,无 profiler 捕获。

---

## 0. 结论速览

- 真正决定帧率的是 **D(GPU)**;A/B/C 是"卡一下"或"多几微秒",别在这里期待帧数。
- D 里只有 **D3 需要半天**;D1/D4 各 1 行,且**不动 shader、不重打 bundle**。
- D2/D5 的收益(数十 µs / 一次全清 blit)远小于风险(compute 编译失败毒掉整条雨 / 割裂线),**建议不做**。
- 【决策:2026-10-02】A 组(拖滑块时每帧重建)**用户明确不改**,只留档备查(收到"拖面板就卡"的反馈时直接看 §1)。

## 1. A 组 · 拖滑块时每帧重建(用户决定不改,留档)

| # | 触发 | 每帧干的事(证据) |
|---|---|---|
| A1 | 雨面板 `amount` 滑块 | 面板 `SliderElement` 拖动中就回调(`finished=false`):`WeatherPanel.cs:209-211` → `ApplyRain`(`WeatherPanel.cs:300-306`)→ `RainParticles.ApplyConfig`(`RainParticles.cs:503-544`);整数取值几乎每帧都变 → `RebuildBuffers`(`RainParticles.cs:277-281`)→ 2×`ComputeBuffer(cap,16)`(20 万粒 = 6.4 MB 重建/帧)+ 3125 组 randomize dispatch(`:265`)+ `LogSelfCheck`(`:267`)+ 一条 always-on Diag(`:538`) |
| A2 | 云面板**任意**滑块 | `VolkenUserInterface.cs:604` 等 ~60 处 → `VolkenClouds.ValueChanged`(`VolkenClouds.cs:461`)→ `SetAllLayersShaderProperties`(`CloudRenderer.cs:192-207`)→ 每层把 `CloudLayer.cs:72-123` 的 ~50 个字符串命名 uniform 全推一遍 ×2 层 |
| A3 | `resolutionScale` / `upscaleX` / TSS / `orbitResolutionScale` 滑块 | `OnRenderImage` 的 `needsCreate` 判定(`CloudRenderer.cs:661-672`)→ `CloudLayerView.CreateRenderTextures`(`CloudLayerView.cs:53-93`)**每次 9 张 RT/层/相机**(含 4 张全清 ARGB32/RFloat 历史) |

改法(留档,不实施):A1 按容量上限分配 buffer、拖动期只改 `_amount`(compute 已支持 `amount < capacity`,`TestRow` 就是 20 < capacity 在跑);A2 `Shader.PropertyToID` 缓存成 `static readonly int` 用 int 重载;A3 给"改了就重建 RT"的参数加 100~150 ms 去抖。

## 2. B 组 · 每帧 CPU(飞行内)

| # | 位置 | 说明 | 状态 |
|---|---|---|---|
| B1 | `CloudRenderer.cs:710` `:711` `:715` `:737` `:771` `:798` `:821` | `OnRenderImage` 里每帧 6~8 次 `Material.FindPass("<字符串>")`(native pass 名查找)→ 缓存 pass index | 新发现 |
| B2 | `CloudRenderer.cs:301-336` `:357-379` | `SetLayerDynamicProperties` 每层每帧 ~28 次字符串命名 `Set*` → `PropertyToID` 缓存 | 新发现 |
| B3 | `CloudRenderer.cs:765` | 每层每帧 `new RenderBuffer[3]` | 新发现 |
| B4 | `LightningBolt.Update` / `.LateUpdate` + `SelfDestruct.Update`(`LightningBolt.cs`)→ `GamePause.cs:11-28` | `GamePause.Now/IsPaused` **每个闪电对象每帧各查一次**(一次落雷最多 549 分叉 → ~1600 次/帧,每次含 `Time.realtimeSinceStartup` + `Time.timeScale` + `FlightScene.TimeManager` 接口链)→ 给 `GamePause` 加"每帧只算一次"(`Time.frameCount` 判等) | 新发现 |
| B5 | 反编译 `TextModel.cs:62-69` + `InspectorPanelScript.cs:404-417`;mod 侧 `WeatherPanel.cs:44-54` `:190` `:194` `:361` | 面板可见时游戏侧**每帧**调每个 `TextModel` 的 getter → 天气面板 ~10 个字符串/帧(`StatsLine()` 等)→ getter 加 0.25 s 缓存(照 `ProfilerOverlay.cs:12` 的做法) | 新发现 |
| B6 | `LightningModule.cs` `StormLoop` | 等待期每帧 `GamePause.IsPaused` + `Time.deltaTime` | 随 B4 一并消除 |
| B7 | `RainParticles.cs:712-719` | `OnPreCull` 在 `Enabled == false` 时仍每帧一次调用,并每 5 s 一条 always-on Diag | 新发现 |
| B8 | `RainParticles.cs:925-1022` | 每 10 s 一次 `RequestReadbacks`:3 次 `AsyncGPUReadback` + 5~6 条 always-on Diag | 新发现(与审计 §3.5 同源) |
| B9 | `VolkenUserInterface.cs:55-66` `:84-87` | 每秒一次 `Camera.allCameras`(数组分配)+ 每相机 5 次 `GetComponent` | 新发现(量级小) |
| B10 | `VolkenWeather.UpdateCameraMetrics` | `IsActive == false` 时照跑完整 craft 链 + 三角函数,且与 `RainParticles.SampleCraft` 重复 | **已登记**,见[场景门控](../archive/monobehaviour-scene-gate-2026-10-02.md) §3 / 审计 §3.6 |

## 3. C 组 · 一次性尖峰(进场 / 启动)

| # | 位置 | 说明 | 状态 |
|---|---|---|---|
| C1 | `CloudLayer.cs:47` `:50` → `CloudNoise.cs:33-79` | `GetWhorleyFBM3D` **4 次**(每层 worley + detail):128³ R8 3D 纹理 + 每 octave 一个 `ComputeBuffer(numCells³)` + `SetData` + dispatch,另建 `Vector3[numCells³]` 托管数组 | 新发现 |
| C2 | `VolkenUserInterface.cs:122` + `VolkenClouds.cs:315` `:328` | 每次进飞行场景面板**建两次**(`OnSceneLoaded` 一次 + 云的 `RebuildInspectorPanel` 一次)→ 行星没变时可复用 | 新发现 |
| C3 | `CloudLayerView.cs:53-93` | 首次 / 改分辨率时 9 张 RT/层/相机一次性分配 + TSS 历史失效冷启动 | — |
| C4 | `StockCloudMap.LoadFor` | 换行星时按画质档加载自带云 cubemap | **已登记**(静态缓存) |

## 4. D 组 · GPU 大头 + 代价评估

### 4.1 逐项代价

| 项 | 现状 | 改动面 | 动 shader/compute? | 重打 bundle? | 真机验证点 | 实感代价 |
|---|---|---|---|---|---|---|
| **D1** 光样本数(路线图 #1) | `CloudConfig.cs:309` `numLightSamplePoints = 25`(上限 50,面板范围 1~25) | 1 行(+可选 6 行迁移) | 否(**解耦已在位**,见 §5) | **否** | 云内自阴影层次(晨昏线附近最明显) | 分钟级 |
| **D4** 雷击点光源 | `LightningBolt.lightRange = 8000f`(`LightningBolt.cs`,在 `Build()` 里应用;`shadows = None`) | 1 行 | 否 | **否** | 暗侧/夜间的照亮范围 | 分钟级 |
| **D3** 分叉合批 | `LightningBolt.SpawnSplit()`(`LightningBolt.cs`)每条分叉 = `new GameObject` + `LineRenderer` + 8 点 + `SelfDestruct`(85~549 个 draw call) | ~100-150 行 / 1 文件 | 否(要自己按 shader 的 `COLOR` 约定生成 ribbon) | 否 | ribbon 是否仍朝向视线、分叉不变紫红(审计 §2.1 回归) | 半天 + 1~2 轮真机调视觉 |
| **D2** `FillArgs` 组内归约 | `RainParticles.compute:196-205`(5 行) | ~12 行 HLSL | **是** | **是** | kernel 编译通过 + 雨不能整体消失 | **不建议**(收益数十 µs) |
| **D5** 深度 pass 合并 | `Clouds.shader:78` `:121` 两个 pass | pass 重构 | **是** | **是** | 割裂线 + 雨软粒子 | **不建议** |

### 4.2 D1:只剩"改默认值"(路线图 §3 的改动点已完成)

- **shader 侧解耦已在位**:`Clouds.shader:291` `float lightStepSize;`、`:567-568` `float step = lightStepSize; int lightSamples = min(numLightSamplePoints, ceil(span / step));` → 光样本数**已按射线在壳内的长度自适应**,且 `CloudLayer.cs:85-87` 上传 `lightStepSize = lightMarchDistance / lightSamples`,面板也已有这两个滑块(`VolkenUserInterface.cs:725-728`)。
- 因此 D1 = 改 `CloudConfig.cs:309` 一个数。生效范围要看清:`CloudConfig.LoadFromFile`(`CloudConfig.cs:241-278`)在**文件存在时以磁盘值为准** → 已有行星保持 25。两种选择:
  - ① 只影响"没有配置文件的行星"(0 行额外代码);
  - ② 仿 `CloudConfig.cs:260-267` 的 legacy 迁移,写一条"`numLightSamplePoints == 25 && lightMarchDistance == 12000` → 视为未编辑过的默认,改 6"(~6 行)。
- **取值**:25 → 6 使 `lightStepSize` 从 480 m 变 2000 m,厚云带(默认约 1~9.7 km 顶底)内光样本从 ~19 掉到 ~5 → **云内自阴影会变平/硬**。建议先 12(step 1000 m)再试 6;对标见[路线图](cloud-optimization-roadmap-2026-08-28.md) §4 #1(VolRe/KSA 生产值 6)。
- 用户也可完全不改代码:面板滑块本来就暴露了 `numLightSamplePoints`。

### 4.3 D3:代价细节

- 分叉几何**算完就不动**(`SpawnSplit()` 里一次性算完,只有主干 `StepGrow` 在动)→ 可烘成一个 Mesh。
- 难点是**视线朝向的 ribbon**:现在是 `LineRenderer`(`alignment = View`)在顶点阶段做的;烘成静态 Mesh 后相机在 0.2~0.5 s 寿命内转动会露馅,要保真就得每帧重建顶点 → 必须预分配 `List<Vector3>` + `Mesh.SetVertices` 走零 GC 路径。
- 收益:落雷瞬间几十~几百 draw call → 1,同时消掉 549 个 `SelfDestruct.Update` + 549 个原生 `LineRenderer`。**只在"落雷那一瞬卡"这个症状上才值得做。**

### 4.4 D2 / D5:为什么不建议

- **D2**:同一地址的原子加在现代 GPU 上 warp 内本来就会被硬件聚合,你们自己的量级估算是"数十 µs"量级 → 而代价是动 HLSL:必须重打 bundle + 真机验收,且本仓库的实测铁律是"**一个 kernel 编译失败毒掉整个 compute**"(雨整体消失)。
- **D5**:`NearDepth`(全清线性化)与 `DownsampleDepth`(低清)两个产物**分辨率不同且都被下游吃**(`combinedDepthTex` → Composite 的 `SceneDepthTex` + 雨的 `LinearSceneDepth`;`lowResDepthTex` → Clouds 遮挡)→ 本来就合并不了;唯一还能省的是远深度 RT 那一发,而 `FarCameraScript` 头部已明确"不要改用 `OnRenderImage` —— 会在近相机远裁剪面画缝线"。

## 5. 对已有文档的更正

- [云优化路线图](cloud-optimization-roadmap-2026-08-28.md) §0/§3/§4 #1 的两条旧描述已在 2026-10-08 同步更正:①默认值不是 50 而是 `CreateDefault()` 的 **25**(`CloudConfig.cs:309`;另一份 `CreateAnotherDefault()` 是 5,`CloudConfig.cs:373`);②"光步进用视图主步长、未解耦"不成立 —— `lightStepSize` 已是独立 uniform。
- [雨/雷性能审计](rain-lightning-perf-audit-2026-10-02.md) §3.1/§3.2/§3.4 的条目本身仍然准确,本文件只补"代价"这一维。

## 6. 建议顺序与待拍板

1. **D1**:默认值取 6 还是 12;要不要连带做老配置迁移(选择 ②)。
2. **D4**:缩 range(1500 m 级,保逐像素光照)还是 `LightRenderMode.ForceVertex`(保 range,低模地形会有顶点光照块感)。
3. **D3**:是否为"落雷那一瞬"投半天 + 真机调视觉。
4. **B1 / B2 / B4**:机械且低风险(缓存 pass index、缓存 `PropertyToID`、`GamePause` 每帧缓存),可随时插队。
5. A 组:已决定不做;`B3` / `B5`~`B9` / `C1`~`C3` 未排期。
