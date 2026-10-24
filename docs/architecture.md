# 项目架构与配置

> 核对日期：2026-10-14。描述当前源码；运行验收状态见 [待办](backlog.md)。

## 项目与入口

Volken 是 SimpleRockets 2 / Juno: New Origins 的视觉模组。当前本机 Unity 为 **2022.3.62f3**，使用内置渲染管线 BIRP；模组版本以 `Assets/ModData.asset` 为准，当前为 **0.68**；根目录 `version.txt` 与其同步，但游戏实际加载版本仍以 ModData 为准。

| 入口 | 职责 |
|---|---|
| [Mod.cs](../Assets/Scripts/Mod.cs) | 创建 UI、水设置组件，初始化云和天气，注册控制台命令 |
| [ModSettings.cs](../Assets/Scripts/ModSettings.cs) | 开发日志、水面反射、附加相机等全局选项 |
| [ModUpdater.cs](../Assets/Scripts/ModUpdater.cs) | 更新检查（GitHub → `version.txt` → 官方 mod 页版本号，失败静默） |
| [VolumetricClouds.cs](../Assets/Scripts/Volken/Clouds/VolumetricClouds.cs) | 云层、行星解析、云预设和渲染器装配；广播 `PlanetChanged` |
| [PlanetEnvironment.cs](../Assets/Scripts/Volken/Clouds/PlanetEnvironment.cs) | 行星、飞行状态、大气和水的环境快照 |
| [VolkenWeather.cs](../Assets/Scripts/Volken/Weather/VolkenWeather.cs) | 订阅行星事件，加载天气预设，驱动雨和雷电 |
| [VolkenUserInterface.cs](../Assets/Scripts/Volken/Core/VolkenUserInterface.cs) | Inspector 生命周期、选项刷新和每秒扫描附加相机 |
| [PlanetConfig.cs](../Assets/Scripts/Volken/Core/PlanetConfig.cs) | 行星到云层 / 天气预设的映射及旧内联天气迁移 |
| [ForceSetting.cs](../Assets/Scripts/Volken/Water/ForceSetting.cs) | 按高度和设置覆盖水透明度 |

具体渲染入口和资源所有权见 [云](clouds.md) 与 [天气](weather.md)；可切换云内核的目标设计见 [新核心](cloud-core.md)，不代表当前实现。开发组件集中在 [VolkenTests](../Assets/Scripts/VolkenTests/README.md)，正式代码不得依赖其类型。

## 生命周期与模块边界

- `Mod.OnModLoaded` 必须先执行 `VolumetricClouds.Initialize()`，再初始化 `VolkenWeather`。
- 行星解析归 `VolumetricClouds`：订阅 `SceneLoaded` / `PlayerChangedSoi`，每次加载 Flight 都在当前实例去重重订阅 SOI，避免游戏卸载时清空事件后失联；完成云装配后广播环境。天气不另建场景编排器；UI 自行订阅场景事件处理面板。
- 常驻天气 ticker、雷电和 UI 等按飞行状态停表。雨挂载与绘制还检查实时 `Game.InFlightScene`，防止过场通知窗口把雨带进设计器。
- 雨、雾、雷电各看独立开关和环境门控；没有全局天气值或用户天气总开关。天气可以读取云高度带，但不修改云的形状、颜色、覆盖或风。
- 高度带统一调用 `CloudConfig.TryGetBand`，不恢复已撤掉的接口、注册表或编排器。高度带是有效高斯带的 `height ± spread` 并集，不等同于 raymarch 上限，也不是密度的严格零边界。
- `CloudLayer.EnvironmentSuppressed` 表示环境抑制，不能回写用户 `config.enabled`。渲染 / 反射用 `FillActiveLayers` 的复用集合。

## 配置与预设

以下路径均相对游戏 `Application.persistentDataPath`：

| 内容 | 路径 / 类型 |
|---|---|
| 云参数 | `UserData/VolkenConfig/{行星}/{预设}.xml` / `CloudConfig` |
| 天气参数 | `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` / `VolkenWeatherConfig` |
| 行星映射 | `UserData/VolkenConfig/PlanetConfigList.xml` / `PlanetConfigList` |

随包默认预设位于 `Assets/Resources/DefaultConfigs/`，`VolkenFileSetUp` 仅补缺失文件、缺目录先建，单份失败只记 `Mod.Log`、不得中断 `OnModLoaded`。预设名会拼进文件名：保存 / 载入 / 另存为统一校验（非法名 / 重名直接失败）；云 / 天气 / 清单落盘一律临时文件 + 原子替换，失败返回 false 并提示；读到坏文件改名 `.broken`、用内存默认值但不覆盖原文件。Extra 云层只按登记名装载，缺失时恢复默认关闭。

云层预设名和天气预设名独立；行星映射仍共用一个文件。面板修改内存配置，点保存才落盘。经过非飞行场景后以 XML 重读；Flight→Flight 同行星且仍激活时早退保留内存状态，这是已接受的行为。

预设列表变化须重建 Inspector。用户主动切换不存在的预设必须失败，不能触发缺失文件路径的新建默认 XML。加载天气预设须先同步运行态再发 `PlanetConfigLoaded`，详见 [预设同步](weather.md#预设运行态同步)。

旧 XML 未知节点由 `XmlSerializer` 忽略。`PlanetConfig.LegacyWeather` 仍用于旧内联格式迁移，不能按死字段删除。新增配置要同步默认值、`CopyFrom`、限值处理、保存 / 加载和三语 UI；不要恢复已删的 `Clone` 接口。

## 当前全局设置

| 设置 | 默认值 / 范围 |
|---|---|
| `ExtraCameraClouds` / `ExtraCameraRain` | 均为 true；每相机独立资源 |
| `ExtraCameraFog` | false；开启后附加透视相机使用独立低档雾 |
| `WaterReflection` / `AlterTransparency` | 均为 false |
| `DevMode` | false；设置页保留入口 |
| `RestoreDefaultConfig` | 默认 true；启动时补缺失默认预设，下次启动生效 |

主 / 附加云层的配置操作各并入自己的根组，天气配置操作直接位于天气根组。参数标签只留名称和必要单位，三语一起维护。

## 已失效的旧设计

本次源码核对确认下列旧文档内容不再代表当前工程：

- `CloudConfig.depthThreshold`、旧 `low/mid/highAltitudeThreshold` 已不存在；shader 内局部 `depthThreshold=5000` 是另一项合成逻辑。
- `biomeStrength`、`rotDistStrength`、`detailCoverageStrength` 已不在当前云配置 / 密度公式中；旧“覆盖分解”方案不能当作现行功能。
- `rain.respawnMirror` 配置和面板项已删除，但 `RainParticles.RespawnMirror` 静态开发开关和 compute 分支仍存在，默认 false。
- 旧天气值状态机、隐式云联动、`DepthCapture`、`UpscalingPixelSequence`、雨预览和已删除的编辑器探针不再作为接口入口。

仍有消费者的 `nearThreshold`、历史拒绝阈值、雨 / 雷限高和雾范围参数保持现状；后续审查见 [待办](backlog.md#其他未排期项)。
