# 天气预设下拉只有 Default:面板选项快照过期(2026-10-04)

> 状态:🚧 实施中 —— C# 侧已落地(`dotnet build Volken.csproj` = 0 错误 / 3 个既有警告),**未做 Unity 真机验收**
> 日期:2026-10-04
> 关联:[解耦](weather-cloud-decoupling-2026-10-01.md) §8(行星生命周期只归 `VolkenClouds`)/ [雨开关](rain-toggle-scene-switch-2026-10-02.md) §3(预设 XML = 跨场景唯一权威)/ [场景门控](monobehaviour-scene-gate-2026-10-02.md)(常驻组件按 `InFlight` 停表)
> 定位:诊断并修复"进游戏后天气配置下拉**只有 `Default`**,保存 / 重载后才出现其它已存在的预设"。范围 = 面板下拉的**选项来源与刷新时机**;不碰 XML 读写、不碰行星绑定语义。

---

## 0. 结论先行

**下拉选项是"构建面板那一刻"的拷贝,而预设列表是在面板建好之后才扫描的 —— 顺序反了。**不是文件扫描 / XML 读取的问题(文件都在,只是从没进过那个已建好的下拉)。

1. `Mod.cs:47` 先建 `VolkenUserInterface`(它的 `Awake` 订阅 `SceneLoaded`),`Mod.cs:52` 才 `VolkenClouds.Initialize()`(随后订阅)→ 场景加载事件链里 **UI 永远排在云 / 天气之前**。
2. 每次进飞行场景:`VolkenUserInterface.OnSceneLoaded` → `CreateInspectorPanel()` → `WeatherPanel.Build` 用 `weather.AvailableConfigs` 造 `DropdownModel`(`WeatherPanel.cs:169`);而 `DropdownModel` 的构造函数把 options **逐条拷进自己的 `Options`**(反编译 `ModApi.Ui.Inspector/DropdownModel.cs:46-54`)—— **构造之后再刷新列表(哪怕换实例)也进不了这个下拉**。
3. 紧接着 `VolkenClouds.OnSceneLoaded` → `ApplyEnvironment` → `PlanetChanged` → `VolkenWeather.ApplyPlanet` → `ApplyPreset` → `RefreshPresetList()`(`VolkenWeather.cs:224` **换新 list 实例**)才扫出该行星的预设 —— 对已建下拉无效。
4. `AvailableConfigs` 字段初值 = `{ "Default" }`(`VolkenWeather.cs:32`);`RefreshPresetList()` 全仓库只有两个调用点(`ApplyPreset` / `SaveAsNewConfig`),**没有一次发生在面板构建前**。⇒ 一个会话**第一次**进飞行场景,下拉里恰好只有一个 `Default`,与症状一字不差。
5. "保存 + 重载才显示"= 任何一次**面板重建**都会重新取列表:「另存为新配置」= `RefreshPresetList` + `RebuildInspectorPanel`;或再进一次场景(那时 `AvailableConfigs` 已是上次扫描的结果)。「保存当前配置」两者都不做,单点没用。

**云侧同源**:`_availableConfigs` 初值是**空表**(`VolkenClouds.cs:66`),刷新同样在面板构建之后(`ApplyPlanetToClouds`,`VolkenClouds.cs:303`)→ 修前"加载配置"下拉在第一次进场景时是空的。天气只是把它显示成一条**看似合理**的 `Default`,所以先被发现。

### 0.1 幽灵项会改坏数据(一并加护栏)

下拉显示的当前值走 `CurrentConfigName` 实时 getter → 会出现"标签写着 `Stormy`、选项里只有 `Default`"。玩家若点那唯一一项:`SwitchPreset("Default")` → `ApplyPreset` → `LoadFromFile` 在**文件缺失时就地新建一份默认 XML 并落盘**,接着 `RegisterPresetName` 把 `PlanetConfigList.xml` 里该行星的天气预设名改写成 `Default` —— **点一下 = 造垃圾预设 + 改掉行星绑定**。

---

## 1. 修法(已落地)

统一约定:**下拉选项过期 ⇒ 重建面板**。判据放 UI 层(逻辑层不反向依赖 UI),1 s 一次、开销 = 拼一段短字符串:

| 位置 | 改动 |
|---|---|
| `Core/VolkenUserInterface` | 新增 `BuildPanelOptionsSignature()`(行星名 + 云 / 天气预设名,各自排序后拼)与 `EnsureInspectorPanelUpToDate()`(`:1099`;面板存在且签名不符 → `RebuildInspectorPanel()`);`Update` 在 `InFlight` 门控之后按 1 s 检查(`:63`) |
| `OnToggleVolkenUI` | 打开面板前**天气预设列表也现刷一次**(此前只有云刷,`:190`)+ 先 `EnsureInspectorPanelUpToDate()`(`:200`) |
| `RebuildInspectorPanel` | 保住重建前的 `Visible` —— 选项集变化是常态,**重建不该顺手关掉玩家正开着的面板**;`CreateInspectorPanel` 开头记录签名(构建失败也算已尝试,避免 1 s 检查反复重建刷日志) |
| `Weather/VolkenWeather.SwitchPreset` | 改 `bool`;目标预设**文件不存在则拒绝切换**,不再静默新建默认文件 / 改写行星绑定 |
| `Weather/VolkenWeatherConfig.Exists` | 新增;纯查存在(不建目录、不落盘 —— `GetConfigPath` 会建目录,有副作用) |
| `Weather/WeatherPanel` | 下拉回调按返回值提示;失败复用既有 `Volken.UI.ErrorLoadingConfig` |

> 未新增 `SceneLoaded` 订阅者(行星生命周期仍只归 `VolkenClouds`),也没有每帧分配(签名只在 1 s 检查与面板构建时拼)。诊断日志一行:`Volken: 预设列表已变 → 重建检查器面板(planet=…, cloud=[…], weather=[…])`。

---

## 2. 验收判据(真机)

1. **冷启动 → 直接进飞行场景 → 打开面板**:天气配置下拉直接列出该行星 `UserData/VolkenWeatherConfig/{行星}/` 下的**全部**预设(如 Droo 的 `sb` / `Stormy`),**不需要先保存 / 重载**;云的「加载配置」下拉同样是完整列表(修前为空 / 缺项)。
2. 上面那行诊断日志**每场景至多一次**;面板开着时重建**不关闭**面板。
3. 下拉里选另一个预设 → 真的切换(标签 + 天气跟着变);手动删掉某预设文件后再从下拉选它 → 提示错误、**不产生新文件**、`PlanetConfigList.xml` 不被改写。
4. 回归:「另存为新配置」后新预设立即出现在下拉里;云的「另存为 / 加载」不受影响。
