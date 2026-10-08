# Volken2 项目会话上下文

> 新会话的代码导航与约束摘要。主题状态、待办和维护规则见 [README](README.md);实施记录只写进主题文档。
> 核对:2026-10-08,代码基线 `8c4110f`。本次仅核对源码与仓库资产,未重新运行 Unity 或游戏。

## 0. 定位与当前状态

- SimpleRockets 2 / Juno: New Origins 的 `Volken` 模组;Unity **2022.3.62f3**,内置渲染管线 **BIRP**。
- 云包含 raymarch、时序超采样、自带云分布、轨道云、水面反射;天气已有**雨视觉、雨声、闪电、雷声**。雾只有配置和面板占位。
- 活跃工作见 [雨计划](sp2-rain-particledomain-port-2026-09-28.md) §10;自适应域、雨声、多相机和整数 hash 已落地,不要再按早期“阶段 3、雨声未做”开工。
- 场景门控、预设下拉、雷电修复等记录已归档;**归档不代表真机验收通过**。待验证项目见 README §四之三。
- 未排期方案在 [proposals/](proposals/)。天气母计划是历史底账,旧状态机、命令与路径不能当作当前接口。

## 1. 关键路径

| 用途 | 路径 / 入口 |
|---|---|
| 模组入口 / 设置 | `Assets/Scripts/Mod.cs`、`Assets/Scripts/ModSettings.cs` |
| 正式代码 | `Assets/Scripts/Volken/` |
| 开发工具 | [VolkenTests/README](../Assets/Scripts/VolkenTests/README.md):雨预览、噪声 / raymarch 可视化、Profiler |
| 资源清单 / 三语文案 | `Assets/ModData.asset` 的 `_otherAssets` / `Assets/Content/Languages/{EN-US,ZH-CN,RU-RU}.xml` |
| 文档 / 注释验证 | [check-docs.ps1](../tools/check-docs.ps1)、[strip-code-comments.ps1](../tools/strip-code-comments.ps1) |
| 本机路径 | [LOCAL_PATHS.md](LOCAL_PATHS.md),仅本地、被 Git 忽略;公开文档只用令牌 |
| 游戏日志 | `<USERPROFILE>/AppData/LocalLow/Jundroo/SimpleRockets 2/Player.log` |
| 游戏 / 参考源码 | `<JNO_CODE>`、`<JNO_D2>`、`<JNO_MP>`、`<KSA_REF>`、`<VOLRE_REF>`、`<SP2_CODE>`、`<SP2_D4>` |

## 2. 架构与关键文件

以下路径相对 `Assets/Scripts/Volken/`。

| 文件 / 目录 | 职责与边界 |
|---|---|
| `Clouds/VolkenClouds.cs` | 云层、预设与行星生命周期;订阅 `SceneLoaded` / `PlayerChangedSoi`,广播 `PlanetChanged`;必须先于 `VolkenWeather` 初始化 |
| `Clouds/PlanetEnvironment.cs` | 行星名、飞行状态、大气和水的环境快照 |
| `Clouds/CloudRenderer.cs` | `[ImageEffectOpaque] OnRenderImage`:合并远近深度 → Clouds → DilateMV → Upscale → Composite;`LinearSceneDepth` 提供本相机线性深度 |
| `Clouds/CloudLayer.cs` / `CloudLayerView.cs` | 前者持有配置 / 材质 / 噪声 / 风累积,后者持有每相机每层的 RT 和时序历史;`EnvironmentSuppressed` 不能回写用户的 `config.enabled` |
| `Clouds/CloudConfig.cs` | 云配置;高度带唯一实现 `TryGetBand`;活跃层由 `VolkenClouds.FillActiveLayers` 填入复用缓冲 |
| `Clouds/Shader/Clouds.shader` | 体积云、轨道云、时序上采样与合成;密度 / 覆盖修改须同步两种云的 4 处消费点 |
| `Clouds/CloudNoise.cs` / `StockCloudMap.cs` / `UpscalingPixelSequence.cs` | 噪声、自带云 cubemap 缓存、时序采样序列 |
| `Clouds/FarCameraScript.cs` / `CloudReflectionRenderer.cs` | 远相机 CommandBuffer 深度 / 水面反射云;`DepthCapture.cs` 的未接入问题见场景门控记录 |
| `Core/PlanetConfig.cs` | `PlanetConfigList.xml` 的行星到云 / 天气预设映射及旧格式迁移;两类预设各自独立 |
| `Core/VolkenUserInterface.cs` | Inspector 生命周期、选项刷新、每秒扫描额外相机;不承担行星解析 |
| `Weather/VolkenWeather.cs` | 订阅 `PlanetChanged`,读取预设与相机指标,驱动雨 / 雷;无全局天气值状态机 |
| `Weather/VolkenWeatherConfig.cs` | 当前文件名和类名均为 `VolkenWeatherConfig`;总体 / 雨 / 雾 / 雷配置、读写、迁移、限值 |
| `Weather/WeatherPanel.cs` / `GamePause.cs` | 天气面板 / 排除游戏暂停的计时 |
| `Weather/Rain/` | `RainParticles`、`RainAudio`、`Shader/`、6 条雨声;每相机独立实例 |
| `Weather/Lightning/` | `LightningModule`、`LightningBolt`、`Shader/`、9 条雷声;落雷与按声速延迟的音频 |
| `Water/ForceSetting.cs` / `PlanetRing/` / `HarmonyPatches/` | 水设置、星环和 Harmony 补丁;部分星环代码未启用 |

## 3. 必须保留的契约

- **生命周期**:行星解析归 `VolkenClouds`;UI 自己订阅场景事件建面板。常驻组件按 `InFlight` 停表;雨挂载和绘制另查活体 `Game.InFlightScene`,防过场通知窗口。[场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) §1、[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §4。
- **配置**:云在 `UserData/VolkenConfig/{行星}/{预设}.xml`,天气在 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`;跨非飞行场景重读 XML,未保存改动不保留。Flight→Flight 同行星早退可保留内存状态。[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §3。
- **子系统独立**:雨 / 雷各看自己的 `enabled` 和参数;天气不改云。直接读 `CloudConfig.TryGetBand` 和本相机 `CloudRenderer.LinearSceneDepth`,不恢复接口、注册表、编排器。[解耦](archive/weather-cloud-decoupling-2026-10-01.md) §2.1、§8。
- **多相机资源**:compute / buffer / 材质和 `_assetsReady` 均按实例持有;静态雨诊断与淡出量仅主视图写,否则会影响雨声。开关为 `ExtraCameraRain`。[附加相机雨](archive/extra-camera-rain-2026-10-04.md) §1、§2。
- **雨朝向**:当前采用世界(帧)空间 `_RotationMatrix`,位置在帧空间下落,`TranslateFixed` 处理重定位。屏幕固定构轴已被后续实测推翻,不能套用早期复盘作为当前实现。[雨计划](sp2-rain-particledomain-port-2026-09-28.md) §10.3。
- **雨随机数与密度**:`id + _phase` 走整数 hash,禁止无界浮点 `frac` hash。自适应域目前只增半径,不自动重建 buffer 补密度。[落点重复](archive/rain-spawn-hash-precision-2026-10-04.md)、[雨计划](sp2-rain-particledomain-port-2026-09-28.md) §10.5。
- **预设下拉**:列表变化须重建面板并保留可见性;切换前检查文件存在,避免隐式新建默认预设。[下拉修复](archive/weather-preset-dropdown-stale-2026-10-04.md) §1。
- **坐标原点重置(浮动原点)**:离帧中心 >5000m、帧速 >1000m/s、时间加速或表面锁定切换可触发;订阅 `IGameView.ReferenceFrameRecentered(IReferenceFrame, Vector3d, Vector3d)` 清云历史。[方案 C](archive/ksa-temporal-upscale-port-2026-08-24.md) §12。
- **渲染**:重投影用 `GL.GetGPUProjectionMatrix(..., true)`,RFloat 云深度读 R 通道;N/S 风重投影仍有缺口。轨道云按海拔淡入,反射 `_OrbitFade=0`。[割裂线](archive/seamline-reprojection-2026-08-25.md)、[轨道云](archive/orbit-clouds-crossfade-2026-08-27.md)。
- **资产**:按子系统放 `Shader/`;移动时带 `.meta`,同步 `LoadVolkenAsset` 路径与 `_otherAssets` GUID。新增资产不会自动入包,清单可解析不等于已部署。
- **诊断**:量与症状相同空间的量,同时记当前值与内部状态,先排除暂停。GPU 同步回读不可常驻,shader 错误去重后统计;同一方向连续 3 轮无改善就换诊断。[复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) §5。

## 4. 游戏 API 关键入口

- `PlanetCubemapUtility.LoadCubemap(..., PlanetCubemapType.Clouds, ...)`:R/G/B 为低 / 中 / 高云,A 为纬度 / 行星遮罩。
- `WaterReflectionPlaneScript.UpdateReflections`:水面反射;机体 `ReflectionProbeScript` 适配未做。水调参入口为 `WaterMaterialModifier` / `PlanetWaterConfig` / `WaterQualitySettings`。
- `AtmosphereSample.SpeedOfSound`:雷声传播速度,取不到或无物理大气时由配置兜底。
- `CraftScript.GravityNormal` / `FrameVelocity`:帧空间径向下和飞行器速度;不能写死世界 -Y,不能把绕飞相机速度当飞行器速度。

## 5. 开发流程约定

1. 先读目标代码和主题最新结论;早期方案与旧行号仅作历史证据。文档与注释规则以 [README](README.md) §五为准。
2. 正式代码不得引用 `VolkenTests` 类型;工具可整体删除,测试命名空间用 `Volken.Tests`。
3. 新配置同步 `Clone/CopyFrom/ClampAll` 等对应路径与旧 XML 兼容;新功能默认关闭或恒等。文案同步三语,键前缀 `Volken.UI.*`。
4. 云密度 / 覆盖修改同步体积 / 轨道消费点,用 `orbitDebugMode=1` 对照;每帧渲染避免 LINQ 和临时集合。
5. 命令中的路径用 ASCII 或枚举得到的 `.FullName`;读取失败立即停止,不能拿空内容覆盖文件。

## 6. 调试与验证

- `Mod.Log` 受 `ModSettings.DevMode` 控制;`Mod.Diag` 始终输出;`Mod.LogThrottled` 节流。shader 编译错误另查 Unity `Editor.log`。
- 当前仅注册 **`frs`、`brs`、`VolkenForceRefresh`**。归档中的 `volkenAssets` / `volkenBolt` / `volkenRainAxis*` 已删除;调参走面板,手动落雷走“立刻劈一道”。
- 雨预览见 [开发工具说明](../Assets/Scripts/VolkenTests/README.md);场景、真实海拔、资源打包与音频仍需进游戏验证。
- 改文档必跑 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs`;只改注释用 `strip-code-comments.ps1 -Old <快照> -New <改后>` 自证。
- Mods 目录不要同时放 `Volken.sr2-mod` 和 `Volken-R.sr2-mod`;联机事件链历史冲突见 [NRE 记录](archive/jno-sceneloaded-nre-2026-08-27.md)。
