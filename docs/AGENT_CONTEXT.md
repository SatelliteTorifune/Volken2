# Volken2 项目 —— 会话启动上下文

> 用法:开新会话做本项目前,把本文档作为首条上下文交给 AI,省去重复调研。
> 本文件是**只读参考**,不是 plan;方案 / 决策写进主题文档(索引 [`README.md`](README.md)),写入规则见其 §五。
> 引用保留「文件名 + 行号」;**不写本机绝对路径**(令牌 → 真实值见 [`LOCAL_PATHS.md`](LOCAL_PATHS.md),**严禁提交**)。

---

## 0. 定位与当前状态

给 **SimpleRockets 2 / JNO**(Steam 870200)写**体积云 mod `Volken`**:Unity 工程,跑在 **BIRP** 的屏幕后处理链上。核心 = `Clouds.shader` 的 raymarch 体积云 + 时序超采样(TSS/KSA 结构)+ 游戏自带云分布接入。

- **唯一在动手**:**SP2 雨重做** → [`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md)。**实施记录在其 §10,那是唯一事实源,本文不复制**。现状:阶段 3 雨滴 shader 已落地并搬进**编辑器预览台**(`VolkenTests/RainPreview.cs`,免去每轮 5 分钟打包);定稿后仍需打包做游戏内验收。
- **已落地 / 已归档**:方案 A(自带云区域内密度缩放)、B(自带云全球分布)、C(KSA 时序超采样);轨道 2D 云 + 过渡带;割裂线(重投影 Y 镜像);JNO 冲突定位与修复;水面反射云(方案 A)。
- **天气**:范围已缩减为**只做雷电**;雨 / 雾实现曾整体移除,现按雨计划重做;雷声真实化代码已落地(**待 Unity 打包 + 真机验收**,未做则雷声静音)。天气母计划 / 雷声 / 优化路线图 / 水体大修都在 [`proposals/`](proposals/)。
- **2026-10-01 解耦重构已落地**:`VolkenMod` → `Clouds.VolkenClouds`、`VolkenWeatherConfig` → `Weather.VolkenWeatherSettings`、行星生命周期并入 `VolkenClouds`、`Weather/` 按子系统分子目录、`CloudConfig.TryGetBand` 成为唯一实现;期间抽出的接口 / 编排器层已按"过度抽象"**全部回撤**。剩余 A2 / D / F 未排期 → [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md) §8。
- ⚠️ **铁律:命令行里绝不写非 ASCII 路径** —— 曾因中文路径被 shell 编码破坏,`ReadAllText` 失败却未停,把 986 行的计划文档覆盖成 1 节。用编辑工具、或 `Get-ChildItem -Filter` 取 `.FullName`;`ReadAllText` 后必须确认读取成功。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `<PROJECT>` |
| Mod 源码 | `Assets/Scripts/Volken/` |
| 测试 / 开发工具(可整体删除,正式代码零引用) | `Assets/Scripts/VolkenTests/`(雨预览台 `RainPreview.cs`、构轴探针 `RainAxisProbe.cs`、噪声 / 步进可视化、`Profiler/`;契约见该目录 `README.md`) |
| 文档索引 / 会话上下文 / **待办台账** | `docs/README.md`(§四之三 = 待办 + 已修复)· `docs/AGENT_CONTEXT.md` |
| **文档体检脚本**(改完文档必跑) | `tools/check-docs.ps1`(仓库根,9 项机械检查,退出码 0 才算过);配套反模式清单见 `docs/README.md` §五.11 |
| **注释自证脚本**(只动注释时必跑) | `tools/strip-code-comments.ps1`(仓库根) |
| **雨重做计划(开工前必读)** | `docs/sp2-rain-particledomain-port-2026-09-28.md` |
| **雨雾移植失败复盘(通用铁律,做细长 billboard / GPU 粒子 / 相机相关朝向前必读)** | `docs/archive/weather-rain-fog-postmortem-2026-09-27.md` |
| 天气↔云解耦(活跃,部分落地) | `docs/weather-cloud-decoupling-2026-10-01.md` |
| 天气母计划(决策沿革 + 素材底账,已转 proposals) | `docs/proposals/sp2-weather-port-2026-09-27.md` |
| 雷声真实化(已转 proposals,待打包) | `docs/proposals/thunder-realism-2026-09-28.md` |
| **雨 / 雷性能审计(C# 侧已改,GPU 侧未排期)** | `docs/proposals/rain-lightning-perf-audit-2026-10-02.md` |
| **高开销剩余四组清单 + D 各方案代价(未排期;A 组已决定不改)** | `docs/proposals/hotpath-optimization-backlog-2026-10-02.md` |
| **MonoBehaviour 场景门控 + 每帧热点(已改,待真机验收)** | `docs/monobehaviour-scene-gate-2026-10-02.md` |
| 本机路径映射(真实值,**仅本地**) | `docs/LOCAL_PATHS.md`(已被 `.gitignore` 排除) |
| 游戏运行日志 | `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log` |
| 反编译游戏源码(只读)/ 解包工程 / JNO 联机 mod | `<JNO_CODE>` · `<JNO_D2>` · `<JNO_MP>` |
| 参考源码 | `<KSA_REF>`(方案 C / 轨道云母本)· `<VOLRE_REF>`(KSP EVE) |

## 2. 架构与关键文件(`Assets/Scripts/Volken/`)

| 文件 | 职责 |
|---|---|
| `Clouds/VolkenClouds.cs` | **云系统 + 行星生命周期的唯一来源**(原名 `Core/VolkenMod.cs`,2026-10-01 改名移入 `Clouds/`):持有全部云层、按行星装载云预设、装配 `CloudRenderer` / `FarCameraScript`;自己订阅 `SceneLoaded` / `PlayerChangedSoi`,解析 `PlanetEnvironment` 后广播 `PlanetChanged`(天气订阅它)。持有 `planetConfigList`。⚠️ `Mod.OnModLoaded` 里必须**先于** `VolkenWeather` 初始化 |
| `Clouds/PlanetEnvironment.cs` | 当前行星环境快照(行星名 / 是否飞行 / 是否天体 / 大气 / 水),由 `VolkenClouds` 解析并广播 |
| `Core/VolkenUserInterface.cs` | UI(配置分组、覆盖分解滑块、轨道云组、本地化);**只做 UI 自己的场景生命周期**(建面板)——行星解析 / 大气门控 / 渲染器装配已交给 `VolkenClouds` |
| `Core/PlanetConfig.cs` | **行星 → 预设名映射**:`PlanetConfig`(`PlanetName` / `CloudConfigName` / `ExtraCloudConfigName` / `WeatherConfigName` + 仅用于迁移旧内联格式的 `LegacyWeather`)+ `PlanetConfigList`(清单读写 + 旧格式迁移 + `Get/SetWeatherConfig`)。落盘 `UserData/VolkenConfig/PlanetConfigList.xml`。**云与天气预设互相独立**(各自的名字 / 列表 / 新建保存,不配对)⚠️ `AddConfig` 对已有行星只更新预设名(勿改回 `Add`) |
| `Weather/`(域根) | `VolkenWeather.cs`(状态机)+ `VolkenWeatherSettings.cs`(预设本体,类名仍是 `VolkenWeatherConfig`)+ `WeatherPanel.cs`(面板,含各子系统分组) |
| `Weather/Rain/` | `RainParticles.cs` + `RainAudio.cs` + `Shader/`(`RainParticles.compute` / `.shader`)+ `Audio/`(6 条 `volkenRain-{light,heavy}-1~3.wav`) |
| `Weather/Lightning/` | `LightningModule.cs` / `LightningBolt.cs` + `Shader/`(`LightningBolt.shader` / `LightningFlash.shader`)+ `Audio/`(9 条雷声 wav) |
| `Weather/Fog/` | **待实现**(`FogSection` 占位字段现在 `VolkenWeatherSettings.cs` 里) |
| `Weather/VolkenWeather.cs` | **天气系统**:按行星装载天气预设、相机指标(海拔 / 太阳时 / 云内淡化)、雷电启停。**没有"天气值"状态机** —— 各子系统只看自己的 `enabled` + 节奏参数(雨 `rain.enabled`;雷 `lightning.enabled` + `minDelay/maxDelay`)。与云只做"直接读 config"(`VolkenClouds.Instance?.MainLayer?.config?.TryGetBand(...)`),**不造接口层** |
| `Weather/VolkenWeatherSettings.cs` | 天气预设本体(2026-10-01 从 `Core/` 移入 `Weather/`):4 个 Section(总体 / 雨 / 雾 / 雷)+ `LoadFromFile/SaveToFile/ClampAll/UpgradeUneditedDefaults`。**黎明起雾是 `fog.dawnFog`**,不属于总体 |
| ~~`WeatherTypes.cs`~~ 与 ~~"天气值"标度~~ | **❌ 均已删除**(2026-09-27 / 2026-10-01):SP2 全局档位、`WeatherValue` 连续标度、随机 / 淡变状态机、`DescribeWeatherValue`、`rain.triggerValue`、`lightning.stormValue`、`CloudLinkage` 联动占位。理由(用户):JNO 的天气是**整颗星球**尺度,单个标量状态机不匹配 |
| `Core/SerializableTypes.cs` | 序列化辅助类型 |
| `Clouds/CloudRenderer.cs` | **核心**(~1000 行):`[ImageEffectOpaque] OnRenderImage` 全流程(远近深度合并 → 逐层 Cloud pass → DilateMV → Upscale → Composite);`SetLayerDynamicProperties`;`BuildCloudSpaceRepro`(云空间重投影);订阅 `IGameView.ReferenceFrameRecentered`(原点重置清历史)+ `VolkenClouds.PlanetChanged`(切天体清本相机时序历史,`OnDestroy` 退订);`LinearSceneDepth` = 本相机线性场景深度(雨的软粒子直接 `GetComponent<CloudRenderer>()` 取) |
| `Clouds/CloudLayer.cs` | 每层 RT 管理(cloudTex / cloudDepth / cloudMV / history*);`SetStaticShaderProperties`;TSS 按层处理。**`EnvironmentSuppressed`(运行时,不落盘)** = 绕恒星 / 无大气时的抑制标志;渲染只认 `config.enabled && !EnvironmentSuppressed`(用户开关与环境分离,**勿再回写 `config.enabled`**) |
| `Clouds/CloudConfig.cs` | 配置:coverage / density / layerStrengths、`useStockCloudMap/stockMapStrength/stockDensityScale/stockMaskInfluence/stockAlign*`、`useOrbitClouds/orbitTransition*`、TSS 相关。**`TryGetBand(out bottom, out top)` = 云层高度带的唯一实现**(勿再各自遍历 `layerHeights/layerSpreads/layerStrengths`,也**不要再为它加接口 / 注册表**) |
| `Clouds/Shader/Clouds.shader` | 体积云:Clouds pass(低清全量 raymarch,MRT 颜色 / 云面距离 / MV)、DilateMV(3×3 膨胀)、Upscale(时序核心)、Composite、OrbitClouds(轨道 2D)、ReflectionComposite;`SampleDensity/SampleDensityCheap/SampleCoverageCheap`(方案 A/B/C + 覆盖分解的 4 处同源消费点) |
| `Clouds/CloudNoise.cs` + `Shader/CloudNoiseCompute.compute` | 3D Worley 噪声(`GetWhorleyFBM3D`)与 compute 变体 |
| `Clouds/StockCloudMap.cs` | 自带云 cubemap 静态缓存:`LoadFor(IPlanetNode)` 按画质档加载、缺层检测、`Release()` |
| `Clouds/UpscalingPixelSequence.cs` | KSA 最优采样序列(格网变化时重建缓存) |
| `Clouds/CloudReflectionRenderer.cs` | 水面反射云(方案 A;读 `ModSettings.Instance.WaterReflection`,默认关;复用 Clouds pass) |
| `Clouds/FarCameraScript.cs` + `Clouds/DepthCapture.cs` | 远相机 CommandBuffer 深度抓取(`farDepthTex`,不用 `OnRenderImage`,避免割裂线) |
| `Clouds/CloudLayerView.cs` · `Debug/` · `Profiler/` | 调试视图、噪声 / raymarch 可视化、性能工具 |
| `Water/ForceSetting.cs` | 按高度切换水透明等强制设置(水体 A 阶段雏形) |
| `PlanetRing/PlanetRingsZWriteFix.cs` | 星环渲染(相关待办:星环渲染顺序错误) |
| `HarmonyPatches/` | `LayoutRebuiltPatch.cs`、`PlanetRingsShaderPatch.cs` 等 |
| **约定:资产按子系统归 `Shader/`** | 每个子系统的 `.shader` / `.compute` 放自己的 `Shader/`(`Clouds/Shader/`、`Weather/Rain/Shader/`、`Weather/Lightning/Shader/`),不散放在代码旁。⚠️ **移动资产必须同步路径字符串** —— `Mod.LoadVolkenAsset<T>` 按**工程路径**读,不是 GUID(共 6 处);新增 / 移动资产还要改 `Assets/ModData.asset` 的 `_otherAssets`,否则静默不进 bundle |

## 3. 已确定的技术事实(不要再重复调研)

**渲染管线(BIRP 约束)**
- 全流程挂在一次 `[ImageEffectOpaque] OnRenderImage` 内:远深度(`farDepthTex`,CommandBuffer)→ 合并 `combinedDepthTex/lowResDepthTex` → 每层 `Clouds` pass(低清全量 raymarch,MRT:颜色 + 云面距离 + 本帧 MV)→ `DilateMV`(3×3 反距离加权 ×3,无 1 帧滞后)→ `Upscale`(全清时序)→ 逐层链式 `Composite` → Blit。
- 云 raymarch 相机无关:观察射线由 C# 每帧传 `_CamFwd/_CamRight/_CamUp/_TanHalfFovV/_Aspect` 构造。
- **时序(TSS)核心**:`Upscale` 逐像素 `isFresh`(本帧采样格)取本帧 raymarch,否则 `lerp(重投影历史, 本帧, tssBlend)`;`tssBlend = lerp(_TssBlend=0.5, 1.0, saturate(|MV|·200))`;本帧无云但历史有云 → `≥0.85` 收敛。TSS 关:运动残影 `lerp(本帧, 重投影历史, historyBlend=0.90)`。
- **重投影必须用 GPU 投影**:`prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix`(逻辑投影与 GPU clip 的 Y 约定相反 → 割裂线,已修)。
- **云空间重投影**:`φ = accumulatedRotation + 2π·runningOffset.x`(风平移折算经度旋转),`reproj = prevViewProj * RotAroundCenter(C, +Δφ)`(逐层缓存 `prevCloudAngle`);**N/S 风(`cloudOffset.z`)未覆盖**(已知缺口)。

**自带云分布(方案 B/A)与覆盖分解**
- `StockCloudMap`:R/G/B = 低 / 中 / 高云,A = 纬度 / 行星遮罩,全部 `clamp01`;`planetToBody` 矩阵做参考系→本体系对齐,`stockAlignSign/AngleOffset` 微调。
- 消费点在 `SampleDensity/SampleDensityCheap`(+ 轨道两份共 4 处):`stockCov = stockBand * stockDensityScale`(方案 A,仅 mapVal),`dist` 用**未缩放** stockBand(保足迹边缘);`useStockCloudMap=0` 或无自带云时 uniform 分支跳过,零开销。
- 覆盖分解:`coverage = cloudCoverage × F_biome(biomeStrength) × F_rotDist(rotDistStrength) × F_tiledDetail(detailCoverageStrength)`,三强度默认 0 = 逐字节一致。

**轨道云(2D)**
- 海拔分派(CPU 侧,全 GPU 无同步):`camAlt < start` 只跑体积云;`start~end` 双渲染 + `orbitFade`(smoothstep)交叉淡入;`> end` 只跑 2D(`OrbitClouds` pass:壳求交 + 同源 `SampleCoverageCheap` 单点 + 光学厚度积分 / transmittance 阻尼,与体积云 Beer 同源);反射路径恒 `_OrbitFade=0`。
- `orbitDebugMode` 分屏(左 = 体积云,右 = 2D;红 = 着色后不透明度,绿 = 原始足迹);`Volken:OrbitDiag` / `Volken:Coverage` 日志供诊断。

**深度 / 遮挡**
- 体积云靠 `lowResDepthTex` 在场景深度处提前终止;`OrbitClouds` 同样采样它,`sceneDepth <= tStart` 返回透明(craft 遮挡一致)。
- 近 / 远深度相机拼接缝历史已修(CommandBuffer);RFloat `cloudDepthTex/histCloudDepthTex` 读 **R 通道**(勿读 alpha)。

**通用架构铁律(雨 / 雾移植失败的沉淀,详见 [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) §5)**
- **细长 billboard 的"长"必须在屏幕平面内表达**:长度轴只由**物理量**决定,宽度轴由**视线**决定(`W = normalize(cross(L, viewDir))`);世界空间长度轴 + 任意宽度轴 → 视角相关地退化成"横块"。
- **沿视线的方向分量对屏幕方向贡献恒为 0** → 归一化近零向量 = 噪声(径向爆散);退化情况必须显式兜底。
- **每帧都会调到的路径里禁止"重置动画进度"**;重入守卫只能比较**目标值**,且**只能读状态,不能挡在"改状态的逻辑"前面**(否则状态机被自己的守卫锁死)。
- **诊断必须量你关心的那个量所在的轴**(屏幕问题就量屏幕空间),并同时输出**当前值 + 内部状态**,才能区分"从没触发"与"触发后被重置"。
- **日志按消息去重后再统计次数**(同一条 Unity shader 错误会对 N 个 kernel × M 个平台各报一遍);优先用**累计计数器**而非节流日志。
- **先标定数量级,再调观感**:密度(个/m³)、域半径、停留时长(域直径 / 相机速度)要同时满足;`GraphicsBuffer.GetData` 是同步回读,不可常驻。
- **天气数据的唯一权威来源 = 预设 XML**(`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`):面板未保存的改动**不跨场景保留**,进飞行场景 / 换行星一律重读文件(开关"自己变回文件里的值"= 预期)。**不要**为了"记住面板改动"去缓存或回写 `Config`,也不要改 `ApplyPlanet` 的同行星早退判据(Flight→Flight 快速读档/回退发射保留内存状态属**正常**,不用管)。

**坐标原点重置(浮动原点)**
- SR2 会 `RecenterReferenceFrame`(离帧中心 > 5000m / 帧速 > 1000m/s / 时间加速每帧 / 表面锁定切换)→ 世界坐标整体平移 → 时序历史失效。
- 修复:订阅 ModApi `IGameView.ReferenceFrameRecentered`(委托签名 `(IReferenceFrame, Vector3d, Vector3d)`),回调清空历史 + `frameNumber=0` 冷启动。

**冲突 / 兼容**
- JNO 联机 mod 的 `MultiPlayerUI.OnSceneLoaded` 曾在 `inspectorPanel == null` 时抛 NRE、中断 `SceneLoaded` 事件链 → Volken `OnSceneLoaded` 被跳过(看不到云、自带云开关锁死);JNO 侧空值护栏已手动应用,Volken 侧自愈已撤除(纯事件驱动)。
- Mods 目录勿同时放 `Volken.sr2-mod` 与 `Volken-R.sr2-mod`(同名程序集冲突)。
- **每帧回调的场景边界**:挂在相机 / 场景物体上的组件(`CloudRenderer` / `FarCameraScript` / `RainParticles`)随 Flight 场景卸载,天然只在飞行场景;常驻(`DontDestroyOnLoad`)组件(天气 ticker / `RainAudio` / `LightningModule` / `VolkenUserInterface`)必须按 `VolkenClouds.PlanetChanged` 的 `InFlight` 停表 —— 新增每帧组件照此办理,见 [场景门控](monobehaviour-scene-gate-2026-10-02.md) §0~§2。
- **相机上组件的实例资源不得用 `static` 门控**:换场景 = 组件销毁 + 新实例,而 compute / shader / material / buffer 都在实例上;`static` 就绪标志会让新实例跳过加载并**永久 NOT READY**(雨视觉"换场景后打不开"就是这样),且静态开关会让雨声照响 → 表现为"只有视觉死"。见 [雨开关](rain-toggle-scene-switch-2026-10-02.md) §0。
- ~~`CloudConfig.low/mid/highAltitudeThreshold` 是死配置但不可删除~~ → **⚠️ 已更正(2026-10-01)**:这三个字段早在 `fe87e59` 就删掉了(全仓库 0 命中),**且没有任何反序列化问题** —— `XmlSerializer` 对未知节点默认忽略、不抛异常。**结论:`CloudConfig` 里废弃字段可以放心删**;归档文档里"不可删除"的旧说法是未经验证的推测,勿再引用。

## 4. 游戏 API 关键入口(反编译确认 / ModApi)

- `Game.Instance.SceneManager.SceneLoaded` —— Volken `OnSceneLoaded` 注册处。
- `IGameView.ReferenceFrameRecentered` —— 原点重置事件,委托 `(IReferenceFrame, Vector3d, Vector3d)`。
- `PlanetCubemapUtility.LoadCubemap(data, PlanetCubemapType.Clouds, size, false)` —— 自带云 cubemap 加载(R/G/B/A 语义见 §3)。
- `WaterReflectionPlaneScript.UpdateReflections(Vector3, Vector3[, Camera])` —— 水面平面反射 / 反射云挂钩点。
- `ReflectionProbeScript` —— 机体 cubemap 探头(方案 B,未做)。
- `WaterMaterialModifier` / `PlanetWaterConfig` / `WaterQualitySettings` —— 水体调参入口(水体大修路线 A/B)。
- `AtmosphereSample.SpeedOfSound` —— 按行星大气成分 + 平均表面温度算(不随高度变);雷声延迟用它(实测 Droo 340 / Cylero 233 / Tydos 931 m/s)。
- `CraftScript.GravityNormal` → `_flightData.GravityFrameNormalized`(**帧空间**);`ReferenceFrame` 旋转是 `Quaternion.Euler(0, yaw, 0)`(**yaw-only**)—— SR2 的雨必须用**帧空间径向**"下",不是世界 -Y。

## 5. 开发流程约定

1. **文档写入与维护规则以 [`README.md`](README.md) §五 为准**(命名 / 状态单一事实源 / 三区流转 / 编码校验 / 自检清单 / 注释规范)。研究有明确结论 → 写进对应主题文档(加「【决策:YYYY-MM-DD】」)并同步 README 决策速查。
2. **三区**:根目录 = 活跃(已动手,当前只有雨计划);`docs/proposals/` = 已论证待拍板;`docs/archive/` = 已完成 / 历史。状态变化按 README §五.4 流转。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/Volken/HarmonyPatches/`。
4. shader 改动注意**同源同步**:3D 体积云(`Clouds` pass)与轨道云(`OrbitClouds` pass)的密度 / 覆盖消费函数共 4 处必须一起改;`orbitDebugMode=1` 分屏复验。
5. 新增游戏内可见文案 → 同步改 EN-US / ZH-CN / RU-RU(key 前缀 `Volken.UI.*`);新配置字段 → `CloudConfig.Clone/CopyFrom` + 旧 XML 兼容(默认值 = 关闭 / 恒等)。
6. 回复中给出改动的文件(带完整路径),方便点击。
7. **代码注释** —— 条文见 [`README.md`](README.md) §五.10,**以它为准**;动手前必过的检查:
   - **写之前问**:删掉它,下一个来改这里的人会不会犯错 / 多花时间?不会 → 别写。
   - **只留**:职责;不变量与契约(不知道就会写错的那种);单位 / 范围 / 默认值(行尾);失败降级语义;**反例**(「不要改成 X —— 会 Y」)。
   - **不留**:`阶段 N` / `第 N 轮`、日期、踩坑史、方案论证、SP2 / KSA 对照、「曾经…后来…」、调试流水、打包清单。判据:**两年后改这里的人需不需要知道?**
   - **复述名字的注释一律删**(如挂在 `public bool enabled` 上的 `/// <summary>是否启用雷电。</summary>`)。
   - 体量:类注释 ≤ 3 行、方法 1 行;`.shader` / `.compute` 头部不设 3 行限制(不变量集中),但只放不变量。
   - **过期 / 与实现矛盾的注释 = 缺陷**,发现即修;代码里的 `TODO` 必须登记 [`README.md`](README.md) §四之三(待办清单)。
   - **硬约束:清理注释不得改动任何代码(含字符串字面量)** → 用 [`tools/strip-code-comments.ps1`](../tools/strip-code-comments.ps1) 自证,退出码 0 才算"只动注释"(见 §6)。
   - 基线(2026-10-02 实测):`.cs` 7.6%(健康);`.shader` + `.compute` 18.8%(**超标待清理**),残留 19 处「阶段 N」+ 13 处日期(已登记 to-do)。
8. **改完文档先跑体检脚本** —— [`tools/check-docs.ps1`](../tools/check-docs.ps1)(9 项机械检查:编码 / 死链 / 本机路径 / 索引体量预算 / 孤儿表格 / **重复文档** / 残留临时文件 / 圈号标记重复 / git 索引异常);`-Root docs`、退出码 0 才算过。**它对应的反模式清单(9 条,全部真实发生过)见 [`README.md`](README.md) §五.11 —— 动文档前先扫一眼那张表**,尤其是:改名后确认旧文件消失、别把正文抄进索引、外部事实(临时文件/打包产物)写前先实测、删除/移动后复核结果而不是只看命令没报错。

## 6. 调试 / 验证

- **日志**:`Mod.LOG`(受 `ShowDevLog` 控制);游戏内诊断:`Volken:OrbitDiag`(2s 节流:相机海拔 / 淡入 / RT 尺寸 / 真实配置)、`Volken:Coverage`(覆盖探针,`orbitDebugMode>0` 时启用)。
- **UI 开关**:`useTemporalUpscale`(TSS 3×3)、`historyBlend`(运动残影)、`useStockCloudMap`(方案 B)、`stockDensityScale`(方案 A)、`useOrbitClouds` + 过渡带、`orbitDebugMode`(分屏)、`WaterReflection`(反射云,默认关)。
- **复现 / 验收要点**:默认配置全关时行为与旧版逐字节一致;开启各功能后对照归档文档的验收清单。
- **只动注释的验证法**:改注释前把 `Assets/Scripts` 快照到 `%TEMP%`,改完跑 [`tools/strip-code-comments.ps1`](../tools/strip-code-comments.ps1) `-Old <快照> -New Assets/Scripts` —— **退出码 0 才算"只动了注释"**(改字符串字面量、改数值都会被判为代码变更)。用法与判定见 README §五.10。
- **文本编码**:`.md` / `.cs` 一律 **UTF-8 无 BOM、LF**。不要用会把非 UTF-8 字节替换成 `U+FFFD` 或加 BOM 的工具(尤其 PS 5.1 的 `Set-Content -Encoding UTF8` 与 `-replace`)。校验脚本与自检清单见 [`README.md`](README.md) §五.7 / §五.8。
