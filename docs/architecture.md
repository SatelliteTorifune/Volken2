# 项目架构与配置

> 核对日期：2026-10-14。描述当前源码；运行验收状态见 [待办](backlog.md)。

## 项目与入口

Volken 为 SimpleRockets 2 / Juno: New Origins 视觉模组，开发环境 Unity **2022.3.62f3** / BIRP；版本以 `Assets/ModData.asset` 为准（当前 **0.68**），`version.txt` 同步；游戏加载以 ModData 为准。

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

渲染 / 资源见 [云](clouds.md) / [天气](weather.md)，设计见 [云核心](cloud-core.md) / [地区天气](weather-regions.md)。开发组件见 [VolkenTests](../Assets/Scripts/VolkenTests/README.md)，正式代码不得依赖它。

## 生命周期与模块边界

- `Mod.OnModLoaded` 必须先执行 `VolumetricClouds.Initialize()`，再初始化 `VolkenWeather`。
- `VolumetricClouds` 订阅 `SceneLoaded` / `PlayerChangedSoi`；每次进入 Flight 在当前实例去重重订阅 SOI，防游戏卸载清事件后失联，云装配后广播环境。天气不另建场景编排器，UI 自订阅场景事件。
- 常驻天气 ticker、雷电和 UI 等按飞行状态停表。雨挂载与绘制还检查实时 `Game.InFlightScene`，防止过场通知窗口把雨带进设计器。
- 雨 / 雾 / 雷电各自开关与门控，无全局天气值 / 总开关；天气可读云高度带，不改云形、色、覆盖或风。
- 高度带统一调用 `CloudConfig.TryGetBand`，不恢复已撤掉的接口、注册表或编排器。高度带是有效高斯带的 `height ± spread` 并集，不等同于 raymarch 上限，也不是密度的严格零边界。
- `CloudLayer.EnvironmentSuppressed` 表示环境抑制，不能回写用户 `config.enabled`。渲染 / 反射用 `FillActiveLayers` 的复用集合。

## 配置与预设

以下路径均相对游戏 `Application.persistentDataPath`：

| 内容 | 路径 / 类型 |
|---|---|
| 云参数 | `UserData/VolkenConfig/{行星}/{预设}.xml` / `CloudConfig` |
| 天气参数 | `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` / `VolkenWeatherConfig` |
| 行星映射 | `UserData/VolkenConfig/PlanetConfigList.xml` / `PlanetConfigList` |

默认资源在 `Assets/Resources/DefaultConfigs/`；`VolkenFileSetUp` 先建目录、只补缺失文件，单份失败记 `Mod.Log`、不中断加载。保存 / 载入 / 另存为统一校验预设名，拒绝非法名 / 重名；云 / 天气 / 清单用临时文件原子替换，失败返回 false 并提示。坏文件改名 `.broken`，内存用默认值而不覆盖原文件；Extra 仅按登记名加载，缺失则默认关闭。

云 / 天气预设名独立，共用行星映射文件。面板只改内存，保存才落盘；经非飞行场景后重读 XML，Flight→Flight 同行星且仍激活时早退保留内存，此行为已接受。

列表变化重建 Inspector；拒绝主动切换同名 / 不存在预设，不得借缺失路径新建默认 XML。天气加载先同步运行态再发 `PlanetConfigLoaded`，见 [预设同步](weather.md#预设运行态同步)。

`XmlSerializer` 忽略未知节点；`PlanetConfig.LegacyWeather` 仍迁移旧内联格式，不得删。新增配置同步默认值、`CopyFrom`、限值、保存 / 加载及三语 UI，不恢复 `Clone`。

## 当前全局设置

| 设置 | 默认值 / 范围 |
|---|---|
| `ExtraCameraClouds` / `ExtraCameraRain` | 均为 true；每相机独立资源 |
| `ExtraCameraFog` | false；开启后附加透视相机使用独立低档雾 |
| `WaterReflection` / `AlterTransparency` | 均为 false |
| `DevMode` | false；设置页保留入口 |
| `RestoreDefaultConfig` | 默认 true；启动时补缺失默认预设，下次启动生效 |

主 / 附加云层与天气各在自身根组操作配置；标签仅留名称 / 必要单位，三语同步。

## 已失效的旧设计

- `CloudConfig.depthThreshold`、旧 `low/mid/highAltitudeThreshold` 已不存在；shader 内局部 `depthThreshold=5000` 是另一项合成逻辑。
- `biomeStrength`、`rotDistStrength`、`detailCoverageStrength` 及旧覆盖分解已撤出配置 / 密度公式。
- `rain.respawnMirror` 配置和面板项已删除，但 `RainParticles.RespawnMirror` 静态开发开关和 compute 分支仍存在，默认 false。
- 旧天气值状态机、隐式云联动、`DepthCapture`、`UpscalingPixelSequence`、雨预览和已删除的编辑器探针不再作为接口入口。

`nearThreshold`、历史拒绝阈值、雨 / 雷限高、雾范围仍有消费者，审查见 [待办](backlog.md#其他未排期项)。
