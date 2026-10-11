# Volken2 项目会话上下文

> 新会话的代码导航与约束摘要。主题状态、待办和维护规则见 [README](README.md);实施记录只写进主题文档。
> 核对:2026-10-09;两种雾及可操作 UI 已写入原工程,旧占位已清理,独立 GPU 验证完成;用户指定后续直接在原工程工作,本轮不负责打包。雨碰撞 / 水花已获真机确认并归档;135 项既有 GPU 验证不代表性能或雾验收。

## 0. 定位与当前状态

- [雨滴方向修复](archive/rain-direction-diagnosis-2026-10-11.md) §6~§8:世界 Y 速度绝对值反射已去除;用户反馈与 `build=2` 游戏日志确认本次场景通过,34 组预期角差最大 0.17°。已按用户要求移除本次诊断代码 / 周期回读及专用临时日志并归档;旧 `RainDirection` 字段仅是历史证据。移动参考帧补偿及非零变体缩放缺口仍存在。
- [云可见性与噪声修复](cloud-visibility-diagnosis-2026-10-10.md) §5~§12:可见性 / 生命周期与镜头依赖已修;用户否定 §11 的画质后,§12 撤回试验主噪声与固定粗化,恢复旧体积 Worley / mip 0,轨道轮廓改由现有宏观分布决定。177 项 Unity / GPU 回归通过;最新 `build=8 shader=7`,正常 `shapeNoise=VolkenWorleyFBM noiseMips=8/8`,轨道深度仍为 scene。Worley 为正常生成器;旧 169 / 170 项不能当当前画质验收,待游戏复核,未打包。
- SimpleRockets 2 / Juno: New Origins 的 `Volken` 模组;Unity **2022.3.62f3**,内置渲染管线 **BIRP**。
- 云包含 raymarch、时序超采样、自带云分布、轨道云、水面反射;天气已有**雨视觉、雨声、闪电、雷声**。雾已有**径向薄雾、流动体积雾、三语面板和详细日志**;[雾记录](fog-implementation-plan-2026-10-09.md) §9 为实现状态,§10 为 UI 修复,§11 为薄雾查询纹理,§12 为游戏帧时间 / 调参解耦修正;两种雾资源运行正常,大幅掉帧主因仍未分离,游戏水面 / 原生透明 / 反射与性能待验收。§13 夜间光照已修正,161 项独立验证通过;`code=3 shader=3`,新增 `Fog lighting` 日志,待游戏昼夜验收。
- 原雨实现已归档,见 [雨记录](archive/sp2-rain-particledomain-port-2026-09-28.md) §10;自适应域、雨声、多相机、整数 hash、透明度与碰撞已落地。2026-10-09 用户反馈水面无水花,后续 [水面溅射修复](rain-water-splash-fix-2026-10-09.md) 已接入独立 shader / 竖直飞溅 / 场景深度遮挡,53 项 GPU 回归通过,待打包与真机确认。
- [用户 UI 清理](archive/user-ui-cleanup-2026-10-09.md) 已移除天气 / 云诊断控件与状态文字,当前设置页保留开发日志 / Profiler 开关;§4 已按截图精简三语参数标签,只留名称和单位。§6 已移除天气总开关和配置管理子组,配置操作直接并入天气根组;各效果独立启停,旧 Overall 字段仅作 XML 兼容。后台日志保留,游戏视觉待验收。
- 场景门控、预设下拉、雷电修复等记录已归档;**归档不代表真机验收通过**。待验证项目见 README §四之三。
- 未排期方案在 [proposals/](proposals/)。天气母计划是历史底账,旧状态机、命令与路径不能当作当前接口。
- [过渡曲线与反平铺提案](proposals/orbit-transition-antitiling-plan-2026-10-11.md):用户确定先改 2D–3D 过渡、后引入共用反平铺;适配不同类型行星,全球贴图相关配置暂缓。曲线候选保持互补权重,反平铺同步体积密度 / 反射 / 可选轨道 albedo;宏观覆盖继续读现有分布,尚未实施。[KSA 复核](proposals/ksa-orbit-cloud-reference-audit-2026-10-11.md) 保留源码证据,原全球贴图优先建议已被接管;同源单切片不保证体积积分形状一致。
- 当前性能待办见 [帧率优化清单](proposals/frame-rate-optimization-audit-2026-10-08.md);按 `5e4bb88` 源码核对,未实施 / 未测耗时。雷光配置应用顺序缺陷已登记 README,不要把字段已赋值当作真实 Light 已更新。

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
| `Clouds/CloudRenderer.cs` | `[ImageEffectOpaque] OnRenderImage`:独立远近深度 → Clouds / TSS → 雾化背景 → 按云深度合成;`LinearSceneDepth` 提供本相机线性深度 |
| `Clouds/CloudLayer.cs` / `CloudLayerView.cs` | 前者持有配置 / 材质 / 噪声 / 风累积,后者持有每相机每层的 RT 和时序历史;`EnvironmentSuppressed` 不能回写用户的 `config.enabled` |
| `Clouds/CloudConfig.cs` | 云配置;高度带唯一实现 `TryGetBand`;活跃层由 `VolkenClouds.FillActiveLayers` 填入复用缓冲 |
| `Clouds/Shader/Clouds.shader` | 体积云、轨道云、时序上采样与合成;体积密度 / 光照 / 云影 / 反射须一致,轨道宏观覆盖与可选 albedo 按各自表示核对 |
| `Clouds/CloudNoise.cs` / `StockCloudMap.cs` / `UpscalingPixelSequence.cs` | 噪声、自带云 cubemap 缓存、时序采样序列 |
| `Clouds/FarCameraScript.cs` / `CloudReflectionRenderer.cs` | 远相机 CommandBuffer 深度 / 水面反射云;`DepthCapture.cs` 的未接入问题见场景门控记录 |
| `Core/PlanetConfig.cs` | `PlanetConfigList.xml` 的行星到云 / 天气预设映射及旧格式迁移;两类预设各自独立 |
| `Core/VolkenUserInterface.cs` | Inspector 生命周期、选项刷新、每秒扫描额外相机;不承担行星解析 |
| `Weather/VolkenWeather.cs` | 订阅 `PlanetChanged`,读取预设与相机指标,驱动雨 / 雷;无全局天气值状态机 |
| `Weather/VolkenWeatherConfig.cs` | 当前文件名和类名均为 `VolkenWeatherConfig`;总体 / 雨 / 雾 / 雷配置、读写、迁移、限值 |
| `Weather/WeatherPanel.cs` / `GamePause.cs` | 天气面板 / 排除游戏暂停的计时 |
| `Weather/Rain/` | `RainParticles`、`RainAudio`、`Shader/`、6 条雨声;每相机独立实例 |
| `Weather/Fog/` | `FogRenderer` 由每个 CloudRenderer 持有;薄雾 2D 光学厚度查询 / 体积列积分,每相机独立 RT;`ExtraCameraFog` 默认关 |
| `Weather/Lightning/` | `LightningModule`、`LightningBolt`、`Shader/`、9 条雷声;落雷与按声速延迟的音频 |
| `Water/ForceSetting.cs` / `PlanetRing/` / `HarmonyPatches/` | 水设置、星环和 Harmony 补丁;部分星环代码未启用 |

## 3. 必须保留的契约

- **生命周期**:行星解析归 `VolkenClouds`;UI 自己订阅场景事件建面板。常驻组件按 `InFlight` 停表;雨挂载和绘制另查活体 `Game.InFlightScene`,防过场通知窗口。[场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) §1、[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §4。
- **配置**:云在 `UserData/VolkenConfig/{行星}/{预设}.xml`,天气在 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`;跨非飞行场景重读 XML,未保存改动不保留。Flight→Flight 同行星早退可保留内存状态。[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §3。
- **子系统独立**:雨 / 雷各看自己的 `enabled` 和参数;天气不改云。直接读 `CloudConfig.TryGetBand` 和本相机 `CloudRenderer.LinearSceneDepth`,不恢复接口、注册表、编排器。[解耦](archive/weather-cloud-decoupling-2026-10-01.md) §2.1、§8。
- **多相机资源**:compute / buffer / 材质和 `_assetsReady` 均按实例持有;静态雨诊断与淡出量仅主视图写,否则会影响雨声。开关为 `ExtraCameraRain`。[附加相机雨](archive/extra-camera-rain-2026-10-04.md) §1、§2。
- **雨朝向**:当前采用世界(帧)空间 `_RotationMatrix`,位置在帧空间下落,`TranslateFixed` 处理重定位。屏幕固定构轴已被后续实测推翻,不能套用早期复盘作为当前实现。[雨计划](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.3。
- **雨随机数与密度**:`id + _phase` 走整数 hash,禁止无界浮点 `frac` hash。自适应域目前只增半径,不自动重建 buffer 补密度。[落点重复](archive/rain-spawn-hash-precision-2026-10-04.md)、[雨计划](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.5。
- **雨滴透明度**:`RainSection.transparency` 为 0~1,0 保持原效果,1 完全透明;只作用于绘制 alpha,不改共享环境淡出或雨声。[雨计划](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.7。
- **雨碰撞 / 水花**:`RainCollision` 为每相机独立 GPU 管线,同帧正交深度 + 局部海平面检测;默认关闭,面板开启水花自动开启碰撞。游戏原始地形 / 建筑 / 机体 shader 没有 RenderType,26/29/31 层须空标签捕获;导出 DummyShader 的 Opaque 不能当证据。深度全空修复已获用户真机确认。详见 [雨记录](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.8。
- **预设下拉**:列表变化须重建面板并保留可见性;切换前检查文件存在,避免隐式新建默认预设。[下拉修复](archive/weather-preset-dropdown-stale-2026-10-04.md) §1。
- **坐标原点重置(浮动原点)**:离帧中心 >5000m、帧速 >1000m/s、时间加速或表面锁定切换可触发;订阅 `IGameView.ReferenceFrameRecentered(IReferenceFrame, Vector3d, Vector3d)` 清云历史。[方案 C](archive/ksa-temporal-upscale-port-2026-08-24.md) §12。
- **渲染**:重投影用 `GL.GetGPUProjectionMatrix(..., true)`,RFloat 云深度读 R 通道;N/S 风重投影仍有缺口。轨道云按海拔淡入,反射 `_OrbitFade=0`。[割裂线](archive/seamline-reprojection-2026-08-25.md)、[轨道云](archive/orbit-clouds-crossfade-2026-08-27.md)。
- **云状态契约**:每层合成用本层材质;所有云颜色 alpha 为透射率,无云为 1;轨道 shape / detail 为 3D。主材质静态参数同步递增修订,每相机 `PrepareFrame` 按修订与绘制连续性清自己的历史。见[云自查](cloud-visibility-diagnosis-2026-10-10.md) §8。
- **云噪声与镜头稳定性**:shape / detail 已恢复旧 Worley;体积密度 / 光照 / 云影 / 反射与 stock 固定 mip 0。轨道轮廓使用现有 StockCloudCube / PlanetMap,以两张噪声最后 mip 的均值作恒定标定;可选局部 albedo 用固定世界 LOD,浮雕步长固定在世界尺度。禁止按 FOV / 朝向在阈值前改变密度或动态退到均值。当前每帧采固定低清格点,TSS 使用统一运动自适应混合;无实际子像素偏移,不能轮换新鲜格权重冒充空间超采样。见[修复](cloud-visibility-diagnosis-2026-10-10.md) §12;两种表示不等价,新过渡曲线与真正反平铺尚未实施。
- **资产**:按子系统放 `Shader/`;移动时带 `.meta`,同步 `LoadVolkenAsset` 路径与 `_otherAssets` GUID。新增资产不会自动入包,清单可解析不等于已部署。
- **雾投影**:雾以纹理 UV 为输入,用 `GL.GetGPUProjectionMatrix(..., false)`;云光栅 clip / TSS 的 `true` 保持。两者已有 GPU 方向回归,不得一刀切统一。
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
4. 云密度修改核对体积 / 光照 / 云影 / 反射,轨道宏观覆盖另查分布 / 阈值与过渡;用 `orbitDebugMode=1` 对照,不要重新把短周期切片接回轨道轮廓。每帧渲染避免 LINQ 和临时集合。
5. 命令中的路径用 ASCII 或枚举得到的 `.FullName`;读取失败立即停止,不能拿空内容覆盖文件。

## 6. 调试与验证

- `Mod.Log` 受 `ModSettings.DevMode` 控制;`Mod.Diag` 始终输出,`Mod.LogThrottled` 保留异常节流。雾默认每 5 秒摘要,开发配置 `fog.diagnostics` 可关闭;雨 / 雾 GPU 回读仅开发代码显式调用,游戏面板入口已移除。云轨道诊断须同时开启开发日志和 `orbitDebugMode`;开发设置保留 XML 兼容,当前设置页仍提供开关。见 [UI 清理](archive/user-ui-cleanup-2026-10-09.md) §2;shader 错误另查 Unity `Editor.log`。
- 当前仅注册 **`frs`、`brs`、`VolkenForceRefresh`**。归档中的 `volkenAssets` / `volkenBolt` / `volkenRainAxis*` 已删除;调参走面板;手动落雷已移出用户 UI,仅保留开发 API。
- `VolkenTests/Editor` 验证脚本与菜单已按用户要求删除;主题文档中的 GPU / 性能结果为历史证据,不代表工具入口仍存在。
- 雨预览见 [开发工具说明](../Assets/Scripts/VolkenTests/README.md);场景、真实海拔、资源打包与音频仍需进游戏验证。
- 改文档必跑 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs`;只改注释用 `strip-code-comments.ps1 -Old <快照> -New <改后>` 自证。
- Mods 目录不要同时放 `Volken.sr2-mod` 和 `Volken-R.sr2-mod`;联机事件链历史冲突见 [NRE 记录](archive/jno-sceneloaded-nre-2026-08-27.md)。
