# Volken2 项目 —— 会话启动上下文(通用提示词)

> 用法:每次开新会话做本项目前,把本文档(或下面"0. 一句话定位 + 1. 关键路径"起的内容)作为首条上下文交给 AI,可省去大量重复调研。
> 本文件是**只读参考**,不是 plan;方案/决策类内容一律写进对应主题文档(索引见 [`README.md`](README.md))并同步更新索引与决策速查表。
> 本文档内容基于 `docs/` 内归档文档与代码现状整理;引用时保留「文件名 + 行号」,不写本机绝对路径(令牌见 [`LOCAL_PATHS.md`](LOCAL_PATHS.md),本机路径映射,**严禁提交**)。

---

## 0. 一句话定位

给 **SimpleRockets 2 / JNO**(Steam AppID 870200)写**体积云 mod `Volken`**(Unity 工程,跑在 **Built-in Render Pipeline (BIRP)** 的屏幕后处理链上)。核心是 `Clouds.shader` 的 raymarch 体积云 + 时序超采样(TSS/KSA 结构)+ 游戏自带云分布接入。

**当前进度**:方案 A/B/C 全部已实现并归档(自带云区域内密度缩放、自带云全球分布、KSA 时序超采样移植);轨道 2D 云 + 过渡带交叉淡入已实现;割裂线根因(重投影 Y 镜像)已修复;JNO 冲突已定位修复;水面反射云(方案 A)已落地。**未完成/待办**:to-do 高优先项「切换至有大气星球时 config 卡住」、星环渲染顺序、高轨道云层 scale;优化路线图剩余项(Light Volume / PlaceRays / 噪声 mipmap / HDR RT);水体大修实施未排期;方案 C 的 N/S 风重投影缺口。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `<PROJECT>` |
| Mod 源码 | `Assets/Scripts/Volken/` |
| 文档索引 | `docs/README.md`(四节:活跃 / 归档 / 决策速查 / 写入规则) |
| 会话上下文 | `docs/AGENT_CONTEXT.md`(本文件) |
| 活跃 backlog | `docs/to-do.md` |
| 本机路径映射(真实值,**仅本地**) | `docs/LOCAL_PATHS.md`(已被 `.gitignore` 排除,严禁上传) |
| 游戏运行日志 | `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log` |
| 反编译游戏源码(只读) | `<JNO_CODE>`(含 `SimpleRockets2/`、`ModApi/`) |
| 解包工程(Unity 2022.3,水体分析用) | `<JNO_D2>` |
| JNO 联机 mod 工程 | `<JNO_MP>`(冲突排查用) |
| KSA 体积云参考源码 | `<KSA_REF>`(方案 C / 轨道云 / 优化点文档的移植母本) |
| VolRe(KSP EVE)参考源码 | `<VOLRE_REF>` |

## 2. 架构与关键文件(Assets/Scripts/Volken/)

| 文件 | 职责 |
|---|---|
| `Core/Volken.cs` | mod 入口:`OnSceneLoaded` 注册/初始化、`CloudRenderer` 创建、`StockCloudMap.LoadFor/Release`(有大气 SOI 进/出) |
| `Core/VolkenUserInterface.cs` | UI(方案 A/B/C 分组、覆盖分解滑块、轨道云组、本地化);曾含自愈初始化 `Update` 驱动,已撤除 |
| `Core/PlanetConfig.cs` / `Core/SerializableTypes.cs` | 行星/配置序列化类型 |
| `Clouds/CloudRenderer.cs` | **核心**(~484 行):`[ImageEffectOpaque] OnRenderImage` 全流程(远近深度合并 → 逐层 Cloud pass → DilateMV → Upscale → Composite);`SetLayerDynamicProperties`;`BuildCloudSpaceRepro`(云空间重投影,`prevViewProjMat = GL.GetGPUProjectionMatrix(...) * worldToCameraMatrix`);订阅 `IGameView.ReferenceFrameRecentered`(原点重置清历史) |
| `Clouds/CloudLayer.cs` | 每层 RT 管理(cloudTex/cloudDepth/cloudMV、historyTex/historyDepthTex/historyCloudDepthTex);`SetStaticShaderProperties`;TSS 开关按层处理 |
| `Clouds/CloudConfig.cs` | 配置:coverage/density/layerStrengths、`useStockCloudMap/stockMapStrength/stockDensityScale/stockMaskInfluence/stockAlign*`、`useOrbitClouds/orbitTransition*`、TSS 相关;`low/mid/highAltitudeThreshold` 为**死配置**(勿删,旧 XML 兼容) |
| `Clouds/Clouds.shader` | 体积云 shader:Clouds pass(低清全量 raymarch,MRT 颜色/云面距离/MV)、DilateMV(3×3 膨胀)、Upscale(时序核心)、Composite、OrbitClouds(轨道 2D)、ReflectionComposite;`SampleDensity/SampleDensityCheap/SampleCoverageCheap`(方案 A/B/C + 覆盖分解的 4 处同源消费点) |
| `Clouds/CloudNoise.cs` / `CloudNoiseCompute.compute` | 3D Worley 噪声(GetWhorleyFBM3D)与 compute 变体 |
| `Clouds/StockCloudMap.cs` | 游戏自带云 cubemap 静态缓存:`LoadFor(IPlanetNode)` 按画质档加载、缺层检测、`Release()` |
| `Clouds/UpscalingPixelSequence.cs` | KSA 最优采样序列算法(格网变化时重建缓存) |
| `Clouds/CloudReflectionRenderer.cs` | 水面反射云渲染(方案 A;读 `ModSettings.Instance.WaterReflection`,默认关;复用 Clouds pass) |
| `Clouds/FarCameraScript.cs` / `DepthCapture.cs` | 远相机 CommandBuffer 深度抓取(`farDepthTex`,不用 OnRenderImage,避免割裂线) |
| `Clouds/CloudLayerView.cs` | 调试/视图辅助 |
| `Water/ForceSetting.cs` | 按高度切换水透明等强制设置(水体 A 阶段雏形) |
| `PlanetRing/PlanetRingPatch.cs` | 星环渲染(相关待办:星环渲染顺序错误) |
| `HarmonyPatches/` | `LayoutRebuiltPatch.cs`、`AnotherPatch.cs` 等 Harmony patch |
| `Debug/`(NoiseVisualizer / RaymarchDebug)、`Profiler/` | 诊断与性能工具 |

## 3. 已确定的技术事实(不要再重复调研)

**渲染管线(BIRP 约束)**
- 全流程挂在 `[ImageEffectOpaque] OnRenderImage` 一次调用内:远深度(`farDepthTex`,CommandBuffer)→ 合并 `combinedDepthTex/lowResDepthTex` → 每层 `Clouds` pass(低清全量 raymarch,MRT:颜色+云面距离+本帧运动矢量)→ `DilateMV`(3×3 反距离加权 ×3,无 1 帧滞后)→ `Upscale`(全清时序)→ 逐层链式 `Composite` → Blit。
- 云 raymarch 相机无关:观察射线由 C# 每帧传入 `_CamFwd/_CamRight/_CamUp/_TanHalfFovV/_Aspect` 构造。
- **时序(TSS)核心**:`Upscale` pass 逐像素 `isFresh`(本帧采样格)取本帧 raymarch,否则 `lerp(重投影历史, 本帧, tssBlend)`;运动自适应 `tssBlend = lerp(_TssBlend=0.5, 1.0, saturate(|MV|·200))`;本帧无云但历史有云 → `≥0.85` 收敛。TSS 关:运动残影 `lerp(本帧, 重投影历史, historyBlend=0.90)`。
- **重投影必须用 GPU 投影**:`prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix`(逻辑投影与 GPU clip 的 Y 约定相反 → 割裂线,已修)。
- **云空间重投影**:`φ = accumulatedRotation + 2π·runningOffset.x`(风平移折算经度旋转),`reproj = prevViewProj * RotAroundCenter(C, +Δφ)`(`CloudRenderer.BuildCloudSpaceRepro`,逐层缓存 `prevCloudAngle`);**N/S 风(`cloudOffset.z`)未覆盖**(已知缺口)。

**自带云分布(方案 B/A)**
- `StockCloudMap`(游戏 Clouds cubemap):R=低云,G=中云,B=高云,A=纬度/行星遮罩,全部 clamp01;`planetToBody` 矩阵做参考系→本体系对齐,`stockAlignSign/AngleOffset` 微调。
- 消费点在 `SampleDensity/SampleDensityCheap`(+轨道两份共 4 处)**:`stockCov = stockBand * stockDensityScale`(方案 A,仅 mapVal),`dist` 用未缩放 stockBand(保足迹边缘)**;`useStockCloudMap=0` 或星球无自带云时 uniform 分支跳过,零开销。
- 覆盖分解:`coverage = cloudCoverage × F_biome(biomeStrength)× F_rotDist(rotDistStrength)× F_tiledDetail(detailCoverageStrength)`,三强度默认 0 = 逐字节一致。

**轨道云(2D)**
- 海拔分派(CPU 侧,全 GPU 无同步):`camAlt < start` 只跑体积云;`start~end` 双渲染 + `orbitFade`(smoothstep)交叉淡入;`> end` 只跑 2D(`OrbitClouds` pass,壳求交 + 同源 `SampleCoverageCheap` 单点 + 光学厚度积分/transmittance 阻尼能量,与体积云 Beer 同源);反射路径恒 `_OrbitFade=0`。
- `orbitDebugMode` 分屏(左=体积云,右=2D;红=着色后不透明度,绿=原始足迹)、`Volken:OrbitDiag`/`Volken:Coverage` 日志为诊断用。

**深度 / 遮挡**
- 体积云靠 `DepthTex`(lowResDepthTex)在场景深度处提前终止;OrbitClouds 同样采样 DepthTex,`sceneDepth <= tStart` 返回透明(craft 遮挡一致)。
- 近/远深度相机拼接缝历史已修(CommandBuffer);RFloat `cloudDepthTex/histCloudDepthTex` 读 R 通道(勿读 alpha)。

**坐标原点重置(浮动原点)**
- SR2 会 `RecenterReferenceFrame`(离帧中心>5000m / 帧速>1000m/s / 时间加速每帧 / 表面锁定切换)→ 世界坐标整体平移 → 时序历史失效。
- 修复:订阅 ModApi `IGameView.ReferenceFrameRecentered`(委托签名 `(IReferenceFrame, Vector3d, Vector3d)`),回调清空历史 + `frameNumber=0` 冷启动。

**冲突 / 兼容**
- JNO 联机 mod 的 `MultiPlayerUI.OnSceneLoaded` 曾在 `inspectorPanel==null` 时抛 NRE 中断 `SceneLoaded` 事件链 → Volken `OnSceneLoaded` 被跳过(看不到云/自带云开关锁死);JNO 侧空值护栏已手动应用,Volken 侧自愈已撤除(纯事件驱动)。
- Mods 目录勿同时放 `Volken.sr2-mod` 与 `Volken-R.sr2-mod`(同名程序集冲突)。
- `CloudConfig.low/mid/highAltitudeThreshold` 是死配置但**不可删除**(旧 XML 含节点,删除 → XmlSerializer 反序列化失败回退默认配置)。

## 4. 游戏 API 关键入口(反编译确认 / ModApi)

- `Game.Instance.SceneManager.SceneLoaded`(Volken `OnSceneLoaded` 注册处,`Volken.cs` L69)
- `ModApi` `IGameView.ReferenceFrameRecentered`(原点重置事件,委托 `(IReferenceFrame, Vector3d, Vector3d)`)
- `PlanetCubemapUtility.LoadCubemap(data, PlanetCubemapType.Clouds, size, false)`(自带云 cubemap 加载;`PlanetCubemapType.Clouds` R/G/B/A 语义)
- `WaterReflectionPlaneScript.UpdateReflections(Vector3, Vector3[, Camera])`(水面平面反射,`_WaterReflectionTexture`;反射云挂钩点,见实时反射适配分析)
- `ReflectionProbeScript`(机体 cubemap 探头,方案 B 未做)
- `WaterMaterialModifier` / `PlanetWaterConfig` / `WaterQualitySettings`(水体调参入口,水体大修路线 A/B)

## 5. 开发流程约定

1. **文档写入与维护规则一律以 [`README.md`](README.md) §四 为准**(命名 / 状态单一事实源 / 决策记录 / 交叉链接 / 归档流程 / 编码校验 / 改后自检清单)。研究有明确结论 → 直接写进对应主题文档(加「【决策:YYYY-MM-DD】」),并同步 README 决策速查表。
2. 完成主题 → 按 README §四 归档流程移入 `docs/archive/`(头部改「✅ 已归档」+ 修链接 + 更新索引)。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/Volken/HarmonyPatches/`。
4. shader 改动注意两处同源同步:3D 体积云(`Clouds` pass)与轨道云(`OrbitClouds` pass)的密度/覆盖消费函数共 4 处,须同步改;`orbitDebugMode=1` 分屏复验。
5. 新增游戏内可见文案 → 同步改本地化文件(EN-US/ZH-CN/RU-RU,key 前缀 `Volken.UI.*`);新配置字段 → `CloudConfig.Clone/CopyFrom` + 旧 XML 兼容(默认值 = 关闭/恒等)。
6. 回复中给出改动的文件(带完整路径),方便点击。

## 6. 调试/验证

- **日志**:`Mod.LOG`(受 `ShowDevLog` 控制);游戏内诊断:`Volken:OrbitDiag`(2s 节流,相机海拔/淡入/RT 尺寸/真实配置)、`Volken:Coverage`(覆盖探针,`orbitDebugMode>0` 时启用)。
- **UI 开关**:`useTemporalUpscale`(TSS 3×3)、`historyBlend`(运动残影)、`useStockCloudMap`(方案 B)、`stockDensityScale`(方案 A)、`useOrbitClouds` + 过渡带(轨道云)、`orbitDebugMode`(分屏)、`WaterReflection`(反射云,默认关)。
- **复现/验收要点**(详见各归档文档):默认配置全部关闭时行为与旧版逐字节一致;开启各功能后对照归档文档的验收清单。
- **文本编码约定**:`.md` / `.cs` 一律 **UTF-8 无 BOM、LF**。改文档时不要用会把非 UTF-8 字节替换成 `U+FFFD` 的工具(尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8` 与 `-replace`,前者加 BOM、后者在处理含 `[`/反引号的 Markdown 链接时会吃字符)。**完整写入规则 / 校验脚本 / 改后自检清单见 [`README.md`](README.md) §四。**
