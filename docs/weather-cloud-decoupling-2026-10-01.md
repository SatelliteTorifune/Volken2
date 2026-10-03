# 天气 ↔ 云 解耦审计与重构方案

> 状态: 🚧 部分落地(2026-10-01 已完成 A1/A3/B/C/E + 环境抑制修复;**A2、D、F 未排期**)
> 日期: 2026-10-01
> 关联: [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md)(天气母计划,本文只谈**结构**不谈功能范围)、[`proposals/thunder-realism-2026-09-28.md`](proposals/thunder-realism-2026-09-28.md)(雷电)、[`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md)(雨,本文的 C3/C4/C8 直接落在它身上)、[`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md)(雨雾复盘)
> 主题: 审计「天气」与「云」两个模块之间**实际存在**的依赖,给出把边界划回 OOP 的分阶段重构方案。

---

## 0. 结论先行

**反直觉的事实**:两边没有"粘在一起" —— `Assets/Scripts/Volken/Clouds/`(含 `*.shader`、`*.compute`)grep `-i "weather"` **0 命中**,云模块不知道天气存在。真问题三条:① 依赖方向反了 —— 天气穿透云的内部对象图 `Volken.Core.VolkenMod.Instance?.MainLayer?.config.layerHeights/layerSpreads/layerStrengths`(全局单例 → 编排器类 → 层列表 → 配置类 `Vector4` 字段布局,**四级穿透、跨 3 个命名空间**);② "云层高度带 = 启用层 `h ± spread` 的并集"在 `VolkenWeather` 与 `LightningModule` 逐字重复,**无代码拥有**;③ 该耦的没耦 —— `VolkenWeather.IsRaining` 自注「2026-09-27 起:无消费者」,`RainParticles` 对 `WeatherValue` **零读取**。

**修法**:① 云层高度带找**所有者**;② 单例穿透 → **只读接口 + 生命周期拥有者推送**;③ 生命周期收成**一个源**;顺带修 C4、C5。**不建议**:为对称让云依赖天气、DI 容器、恢复"天气值 → 云覆盖度"联动(README 决策速查已拍板"天气系统不联动云层")。

### 0.1 分支现状核对(2026-10-01,对照 `origin/main` = `7690fd7`)

审计前确认:**`main` 上根本没有天气系统**。

| 检查项 | `origin/main` 结果 |
|---|---|
| 全部 28 个 `.cs` 里 grep `weather`(忽略大小写) | **0 命中** |
| `Assets/Scripts/Volken/Weather/` 目录 | **不存在**(无 `VolkenWeather.cs` / `RainParticles` / `LightningModule` / `VolkenWeatherConfig.cs`) |
| `Core/PlanetConfig.cs` | 只有 `CloudConfigName` / `ExtraCloudConfigName`,**无 `WeatherConfigName`、无 `LegacyWeather`** |
| 编排器类名 | 还是 `Core/Volken.cs` 的 `public class Volken`(dev 才改名 `VolkenMod.cs`) |
| `Clouds/**` 与 dev 的关系 | **无结构分叉** —— 忽略空白后仅 `96 增 / 30 删`(命名空间包装 + `globalRotationAngular` 等默认值微调) |

**天气代码只活在 `dev` 的 4 个未合并提交**:`5b89ad8` → `8e8191c` → `eab24b4` → `f67ce23`;远端 PR 最高到已合并 `#17`,**无 dev → main 开放 PR**(反向:`dev` 缺 `main` 的 `LICENSE` 与 PR #9 等 7 个 merge/license 提交)。**影响**:① 结论对 `main` 暂不成立,dev 合并后 C1~C10 整体落到 `main`,故**在 dev → main 前做完 A + B**;② 阶段 A 唯一流向 `main` 的改动是**云侧纯去重**(`CloudConfig.TryGetBand` 一处);③ **不要在 `main` 上开工**。

---

## 1. 耦合点清单(逐条带证据)

> 行号对应本次审计时的代码状态(2026-10-01)。

### C1 — 云层高度带被重复实现两份,且无所有者 【严重度:高】

| 位置 | 内容 |
|---|---|
| `Assets/Scripts/Volken/Weather/VolkenWeather.cs:828-860` | `ComputeCameraCloudFade` 遍历 `layerHeights/layerSpreads/layerStrengths`,取 `strength>0` 层最小底 / 最大顶,算 `1 − InverseLerp(bottom, top−400, asl)` |
| `Assets/Scripts/Volken/Weather/LightningModule.cs:564-595` | `TryGetCloudBand` —— **同一段遍历逐字重复**(变量名 `bottom/top` vs `lo/hi`) |

契约仅在 `LightningModule.cs:561-562` 注释;`CloudConfig` / `CloudLayer` / `CloudRenderer` 无此查询 —— 层数 4 改 N 或 `spread` 语义变更须同改两处,漏改**静默出错**。

### C2 — 天气持有对云模块内部对象图的硬路径(Service Locator 穿透)【严重度:高】

```csharp
// VolkenWeather.cs:832
var main = Volken.Core.VolkenMod.Instance?.MainLayer;
var cloudCfg = main?.config;

// LightningModule.cs:570
var cloudCfg = Volken.Core.VolkenMod.Instance?.MainLayer?.config;
```

`Volken.Weather` 编译期依赖 `Volken.Core.VolkenMod` 与 `Volken.Clouds.CloudConfig` 的**字段布局**(`Vector4 layerHeights / layerSpreads / layerStrengths` 的 x/y/z/w);全局单例不可替身,字段改名静默影响天气。

### C3 — 雨按「具体组件类型」取云渲染器的产物 【严重度:中】

```csharp
// RainParticles.cs:1088-1094
var cr = _cam.GetComponent<CloudRenderer>();
if (cr != null) depthTex = cr.LinearSceneDepth;   // CloudRenderer.cs:69
```

`CloudRenderer.cs:56-69` 的「**当前无消费者**」**已过期**(`README.md` 决策速查第 72 行已更正);该只读口无契约(相机、ready、单位均未约定)。

### C4 — 相机海拔公式三份实现,回退值还不一致 【严重度:中】

| 位置 | 回退值 |
|---|---|
| `CloudRenderer.cs:518-533` `ComputeCameraAltitude()` | `0f` |
| `RainParticles.cs:594-610` `ComputeCameraAltitude(Camera)` | `−1f`(语义 = 闸门不生效) |
| `VolkenWeather.cs:735`(内联在 `UpdateCameraMetrics`) | 保持上一帧 |

三处 `|camPos − planetCenter| − radius`;**回退值差异即行为差异**。

### C5 — 持久化是共享可变状态,天气名挂在云记录上 【严重度:中高(可能丢数据)】

- `PlanetConfig`(`PlanetConfig.cs:14-69`)= `CloudConfigName` / `ExtraCloudConfigName` / `WeatherConfigName` + `LegacyWeather`(**内联 `VolkenWeatherConfig`**),故 `Core` 反向 `using Volken.Weather;`(`:10`);`UserData/VolkenConfig/PlanetConfigList.xml` 双写者:云 `AddConfig` / `SetConfig`(`:237-264`)、天气 `SetWeatherConfig`(`:199-212`,入口 `VolkenWeather.RegisterPresetName`@`VolkenWeather.cs:398-408`);整体重写 + `VolkenMod.OnSceneLoaded:193` `LoadFromFile` **替换实例** → 云/天气面板互相覆盖丢更新。

**风险场景**:两模块各持一个 `VolkenMod.Instance.planetConfigList` 引用,读-改-写之间若有重载 → 丢更新;当前没炸是靠"两边都在同一帧、都从 `VolkenMod.Instance` 现取"这个巧合。

### C6 — 天气配置里塞了"云"的概念(死字段 + 死 UI 组)【严重度:低(功能)/ 中(认知)】

- `VolkenWeatherConfig.CloudLinkageSection`(`VolkenWeatherConfig.cs:123-146`)= `enabled` + 三个 gain,**零消费者**;`WeatherPanel.BuildCloudLinkageGroup`(`WeatherPanel.cs:253-269`)禁用 + 灰字"待实现";README 决策速查第 79 行"保留完整占位"。它是**耦合的墓碑**,不是耦合。

### C7 — UI 归属:天气面板挂在云检查器里 【严重度:低(有意为之)】

`VolkenUserInterface.cs:318` 把 `WeatherPanel.Build(inspectorModel)` 追加进 `Volken.UI.CloudSettings` 检查器(`:293-294`);`WeatherPanel.cs:30` 理由"同处调才是'一套观感参数'"。**保持不动** + 调用点补注释(UI 聚合 ≠ 模块耦合)。

### C8 — 生命周期:两个编排器 + 云侧直接调天气具体类 【严重度:中】

- `VolkenMod.OnSceneLoaded`(`VolkenMod.cs:188-291`)与 `VolkenWeather.OnSceneLoaded`(`VolkenWeather.cs:207-234`)**各自**订阅 `SceneManager.SceneLoaded`、行星名、大气、SOI;`VolkenMod.cs:283` 与 `:385` 硬引 `Volken.Weather.RainParticles.AttachToCurrentView()`;初始化顺序成隐式契约(`Mod.cs:63-64`)。

**后果**:`Mod.OnModLoaded` 必须"先 `VolkenMod.Initialize()` 再 `VolkenWeather.Initialize()`";两次 `SceneLoaded` 的时序决定 `RainParticles` 读到的 `VolkenWeather.Instance.Config` 是不是新行星的 —— 现在靠"同帧 + 幂等"侥幸成立。

### C9 — 该耦合的地方没有耦合:天气 ↔ 雨/云 无语义通道 【严重度:架构级】

- `VolkenWeather.IsRaining`(`VolkenWeather.cs:452-459`)读 `Config.rain.triggerValue`,自注「无消费者」;`RainParticles.cs` 对 `WeatherValue` / `IsRaining` **零命中**。**架构级**:数据面偷云的内部状态(不该耦的耦了)、语义面无天气→降水通道(该耦的没耦);修 C1/C2 不产生"雨响应天气"(那需 §3.6,且**默认关**)。

### C10 — 注释与实现互相矛盾(文档债,直接制造"耦合感")【严重度:低】

| 位置 | 问题 |
|---|---|
| `VolkenWeather.cs:72-73` | 「预设名与云层**共用**」与同文件 `:54-58`、`PlanetConfig.cs:26-29`、README 决策速查第 75 行的「**完全独立**」矛盾(旧改动残留) |
| `CloudRenderer.cs:56-69` | `LinearSceneDepth`「当前无消费者」—— 实际 `RainParticles.cs:1089` 正在消费 |
| `VolkenWeather.cs:29-30` | 「风沿用 `CloudConfig.windSpeed/windDirection`」已失效(雨有自己的 `windInfluence`,且 SR2 无风场) |

---

## 2. 目标依赖图

>  **【2026-10-01 修订】本节初版的"接口 + 值对象 + 推送"路线已被用户判定为过度抽象并回撤。** 现行规则见 §2.1,初版保留作决策记录。

**初版规则(已放弃)**:跨模块只依赖**接口 + 值对象**,编排器推送、消费者不拉取单例;`Volken.Core` 订阅 `SceneLoaded` / SOI 并广播 `PlanetEnvironmentChanged`,向下 `push ICloudField`(→ `Volken.Clouds`)、`push IWeatherState`(→ `Volken.Weather`);`CloudBand` 唯一实现 + `IViewDepthSource`(线性深度);共用 `Volken.Environment` 的 `CameraAltitude` / `PlanetFrame` 快照(C4 归位)。

### 2.1 实际采用的规则(2026-10-01 最终)

> **两边需要对方的数据时,直接读对方的 config / 直接 `GetComponent`。不要为跨模块数据访问造接口层。**

```
VolkenClouds.Instance.MainLayer.config / .planetConfigList / .LinearSceneDepth
  ◄─ VolkenWeather:.TryGetBand(...) / 行星预设名 / 订阅 .PlanetChanged
  ◄─ RainParticles:_cam.GetComponent<CloudRenderer>()(云要天气参数时同理)
行星解析:VolkenClouds 订阅 SceneLoaded / PlayerChangedSoi 并广播
```

**保留两条(消除重复,不是加间接)**:① 云层高度带只有一份实现 —— `CloudConfig.TryGetBand(out bottom, out top)`(原 `VolkenWeather` 与 `LightningModule` 各抄一份);② 行星环境只有一个解析者 —— `VolkenClouds`(本就要按行星装云预设),原四处(云两份、天气一份、UI 一份)。**撤掉的间接层**:`ICloudBandSource` + `CloudBandRegistry` + `CloudBand`、`Core.SceneDepthRegistry`、`Core.SceneOrchestrator`(行星解析搬回 `VolkenClouds`,`PlanetEnvironment` 落在 `Volken.Clouds`)。**断言(修订版)**:`Weather/` 出现 `VolkenClouds.Instance` / `GetComponent<CloudRenderer>()` **允许且期望**;不允许同一段逻辑抄两份 —— 统一走 `CloudConfig.TryGetBand`。

---

## 3. 分阶段方案(每阶段独立可编译、可验收)

### 3.1 阶段 A —— 纯内聚,零行为变更 【风险:极低】

| 步骤 | 内容 |
|---|---|
| A1 | `CloudConfig` 加 `bool TryGetBand(out float bottomAsl, out float topAsl)`(`CloudLayer` 转发)作**唯一所有者**;`VolkenWeather.ComputeCameraCloudFade` 与 `LightningModule.TryGetCloudBand` 改调 —— **消除 C1** |
| A2 | 新增 `Volken/Environment/ViewEnvironment.cs`(值快照 + 静态查询)统一相机海拔;`CloudRenderer` / `RainParticles` / `VolkenWeather` 改调 —— **消除 C4**;回退值语义各异,传 `fallback` 保持各自语义 |
| A3 | 修 C10 三处过期注释;README 决策速查第 72 行 `LinearSceneDepth` 行标注已修 |

**验收**:同配置下 `volkenWeather` 的 `cloudFade`、雷的 `cloudBottom/Top`、雨的 `LastAltitude` 与改前**逐值一致**(回滚 = 纯删除)。

### 3.2 阶段 B —— 依赖反演(行为等价)【风险:低】——  **2026-10-01 已实施后又回撤**

| 步骤 | 内容 | 结局 |
|---|---|---|
| B1 | 新建 `Volken/Clouds/ICloudField.cs`:`bool TryGetBand(out CloudBand band);` + `float CameraCloudFade(float asl);`(`CloudBand` 纯 struct) | **❌ 已删除**(过度抽象) |
| B2 | `VolkenWeather` 加 `SetCloudField(ICloudField)` 由编排器推送,删两处 `MainLayer?.config` —— **消除 C2** | **❌ 已回退**为直接读 config |
| B3 | 新建 `IViewDepthSource`,雨改从注册表取,删 `GetComponent<CloudRenderer>()` | **❌ 已回退**(`SceneDepthRegistry` 删除) |

**验收**:§2 四组符号在 `Weather/**` 零命中;雨"软粒子开/关"A/B 一致;雷仍从同一高度带出来。**风险点**:`ICloudField` 若在 `ApplyPlanet` 之后到位会拿到上一帧引用 —— **必须**每帧现取、不缓存 `CloudBand`。

### 3.3 阶段 C —— 生命周期单点化 【风险:中(时序)】

| 步骤 | 内容 |
|---|---|
| C1 | `VolkenMod` 成**唯一** `SceneLoaded` / `PlayerChangedSoi` 订阅者,广播 `PlanetEnvironmentChanged(planetName, hasAtmosphere)`;`VolkenWeather` 删两个订阅 —— **消除 C8 一半** |
| C2 | 删 `VolkenMod.cs:283` / `:385` 的 `RainParticles.AttachToCurrentView()`,改由 `VolkenWeather` 在 `PlanetConfigLoaded` 里挂雨(它拥有 `Config`) —— **消除 C8 另一半** |

**验收**:`VolkenForceRefresh` ×5 + 快速切 SOI,`volkenWeather` 日志顺序稳定;`RainParticles` 头几次 `ApplyConfig` 的 `enabled` 与当前行星预设一致。

### 3.4 阶段 D —— 持久化拆分 【风险:中(旧档兼容)】

| 步骤 | 内容 |
|---|---|
| D1 | 天气预设名搬进 `UserData/VolkenWeatherConfig/PlanetPresetList.xml`,或退一步:`SetWeatherConfig` 改**先 `LoadFromFile` 合并再写**的读-改-写 + 进程内锁 —— **消除 C5 丢更新** |
| D2 | 复用 `PlanetConfigList.LoadFromFile` 已有的 `LegacyWeather` 迁移块(`PlanetConfig.cs:138-158`),同搬 `WeatherConfigName` |

**硬约束**:`WeatherConfigName` 与 `LegacyWeather` **都不能删**("删字段会让旧 XML 同名节点被静默丢弃",见 `VolkenWeatherConfig.cs:13-20`、README 已知问题第 5 条)。**验收**:云/天气面板"另存为新配置"交替 10 次,`PlanetConfigList.xml` 两边名字都不丢。

### 3.5 阶段 E —— 清理与界定 【风险:低】

| 步骤 | 内容 |
|---|---|
| E1 | `CloudLinkageSection` 二选一:**(推荐)** 保留字段(旧 XML 兼容)但从 `WeatherPanel` 删掉禁用分组,类注释写明"不是耦合点,是墓碑";或**删除** + 「翻案说明」。**无论哪种都要更新 README 决策速查第 79 行** |
| E2 | `VolkenUserInterface.cs:316-318` 补注释:天气面板进云检查器是**有意的 UI 聚合**,不是模块耦合(C7 固化决定) |
| E3 | 术语统一:全仓库把「天气不联动云层」与「预设名独立」对齐(C10 第一条) |

### 3.6 (可选,未排期)阶段 F —— 补上缺失的语义通道 【风险:中】

只有玩家确实想要"天气值影响降水/云量"时才做,**默认全关**:

- `VolkenWeather` 暴露 `event Action<float> WeatherValueChanged`(已存在);
- 雨/云**订阅**该事件、自定用法;天气**不去改**任何云配置(README 决策速查第 85 行);
- 面板必须是"可明确关掉、且默认关"的显式选项(`README.md` 第 73 行已确立此约定)。

>  与 C6 的区别:这里是**事件通道**,不是复活 `CloudLinkageSection` 的三个 gain;若真做建议**新增**字段(旧 gain 语义"从基线纯函数重算"已被否,见 `VolkenWeather.cs:461-473`)。

---

## 4. 推荐执行顺序与收益

| 顺序 | 阶段 | 消除 | 风险 | 建议粒度 |
|---|---|---|---|---|
| 1 | A | C1、C4、C10 | 极低 | 一次提交,可随时停 |
| 2 | B | C2、C3 | 低 | 一次提交;这是"OOP"的实质改善点 |
| 3 | C | C8 | 中 | 单独提交,便于二分回退 |
| 4 | E | C6、C7、C10 | 低 | 可搭在 A 或 B 里 |
| 5 | D | C5 | 中 | 独立提交 + 旧档回归 |
| — | F | C9 | 中 | **仅在玩家提出需求时** |

**做完 A + B 就拿到 80% 的收益**:依赖方向被扳正、重复契约消失,"天气穿透云的内部"在代码里再也写不出来(靠类型系统)。

---

## 5. 明确不做(Non-goals)

1. **不引入 DI 容器 / 事件总线框架**(工程里全是全局单例 + 事件)。
2. **不让 Clouds 反过来依赖 Weather** —— 单向依赖才是目标。
3. **不恢复"天气值 → 云覆盖度/云厚度/云风速"隐式联动**(README 决策速查第 85 行已拍板;要改走 §3.6 的显式、默认关、可关选项)。
4. **不动 `Clouds.shader` / raymarch / TSS 管线**(只谈 C# 侧对象边界)。
5. **不为解耦重写 `PlanetConfigList` 序列化格式**(阶段 D 优先"读-改-写合并")。
6. **不动 C7(UI 聚合)**。

---

## 6. 回归判据(全阶段通用)

- [x] ~~`Assets/Scripts/Volken/Weather/**` 内 grep 不到 `VolkenMod.Instance` / `CloudRenderer` / `layerHeights`~~ → **已作废**(§2.1):现在**允许**直接读云 config 与 `GetComponent<CloudRenderer>()`
- [x] **修订断言**:`Weather/` 没有一处**自己重写**云层高度带遍历(统一走 `CloudConfig.TryGetBand`)
- [ ] 同一份云配置下,`volkenWeather` 的 `cloudFade` 与改前一致
- [ ] 闪电仍从云层带内起(近地雷 + 高云雷各测一次)
- [ ] 雨软粒子开关 A/B 画面一致;云渲染器未挂时雨正常降级(不消失、不报错 —— 见雨计划 §3 的"空深度图让雨整体消失"护栏)
- [ ] 默认配置(天气全关 + 云 `enabled=false`)下行为与改前逐字节一致
- [ ] `dotnet build` 0 错误;打包前 `Player.log` 无新增 shader / NRE 报错
- [ ] 文档与代码同批提交(README §五.6)

---

## 7. 待拍板项

1. **【决策:2026-10-01】保留 `Volken` 前缀只改后缀 + 拆生命周期;范围 = 让雨/云互不联动、结构清晰;已落地 A1/A3/B/C/E。**
2. 阶段 D:"读到-改-写到合并(低风险)"还是"独立清单文件(更干净但迁移面更大)"? —— **未排期**。
3. **【决策:2026-10-01】`CloudLinkageSection`:保留字段(XML 兼容)、删除面板禁用分组**,类注释写明"遗留占位,不是耦合";逻辑删除走 §3.6 事件通道,不复用三个 gain。
4. 阶段 F? —— **未排期**(仅在玩家提出"天气值要影响降水/云量"时做)。
5. **【决策:2026-10-01,用户】接口层不留**:`ICloudBandSource`/`CloudBandRegistry`/`CloudBand`/`SceneDepthRegistry`/`SceneOrchestrator` 全部删除;双方互取数据直接读对方 config / 直接 `GetComponent`;**行星生命周期由 `VolkenClouds` 自己承担**,不另设编排器;只保留"实现去重"与"行星不重复解析"(§2.1、§8)。
6. **【决策:2026-10-01,用户】`Weather/` 按子系统分**:`Rain/`、`Lightning/`(含 `Audio/`)、`Fog/`(待实现),域级文件留在根(§8)。

---

## 8. 实施记录

### 2026-10-01 —— 改名 + 拆生命周期 + 解耦(已落地)

**改名(方案 B:保留 `Volken` 前缀,只把后缀改清楚)**

| 旧 | 新 | 位置变化 |
|---|---|---|
| `Volken.Core.VolkenMod` | `Volken.Clouds.VolkenClouds` | `Core/` → `Clouds/` |
| `Volken.Weather.VolkenWeatherConfig` | `Volken.Weather.VolkenWeatherSettings` | `Core/` → `Weather/` |
| ~~`Volken.Core.SceneOrchestrator`~~ | **已删除(同日回撤)**;行星解析并入 `VolkenClouds`,`PlanetEnvironment` 落在 `Volken.Clouds` |
| ~~`Volken.Core.ICloudBandSource` + `CloudBand` + `CloudBandRegistry`~~ | **已删除(同日回撤 —— 过度抽象,见下)** |
| ~~`Volken.Core.SceneDepthRegistry`~~ | **已删除(同日回撤)** |

机械改名 **261 处 / 18 个文件**; 无脑替换误改磁盘目录字面量 `UserData/VolkenWeatherConfig/` → `VolkenWeatherSettings`(`VolkenWeatherSettings.CONFIG_FOLDER`),已单独回退该路径串(共 6 个文件)。**教训:改类名前 grep 该标识符是否同被用作字符串/路径。**

**结构:行星生命周期收成一处(C8)**(初版抽 `Core.SceneOrchestrator`,同日**撤销**,解析改由 `VolkenClouds` 承担):`VolkenClouds` 订阅 `SceneLoaded`/`PlayerChangedSoi`,解析 `PlanetEnvironment`、广播 `PlanetChanged`,持有 `planetConfigList`(`VolkenUserInterface` 仅留 `SceneLoaded` 建面板);`VolkenWeather` 改订阅 `VolkenClouds.PlanetChanged`;原先分居 `OnSceneLoaded`/`OnPlayerChangedSoi` 的**两份不一致**装配逻辑合并(取 SOI 语义:绕恒星 / 无大气行星);`VolkenMod.cs` 两处 `RainParticles.AttachToCurrentView()` 删除;`VolkenUserInterface.OnPlayerChangedSoi`(**第四份**重复行星解析 + 大气门控 + 渲染器装配,~50 行)**整体删除**;`CloudRenderer` 改订阅 `PlanetChanged` 并在 `OnDestroy` **退订**。

**解耦(C1 / C2 / C3)**:云层高度带遍历**只剩 `CloudConfig.TryGetBand()` 一处**(`VolkenWeather` 与 `LightningModule` 的副本删除,两者**直接读** `VolkenClouds.Instance?.MainLayer?.config`);雨的软粒子直接 `_cam.GetComponent<CloudRenderer>().LinearSceneDepth`。

** 同日回撤(【决策:2026-10-01】用户:不必过度抽象)**:初版"接口 + 值对象 + 注册表"的 B 阶段整体撤掉:

| 撤掉的东西 | 替回的做法 |
|---|---|
| `ICloudBandSource` + `CloudBand` + `CloudBandRegistry`(3 个类型/文件) | `VolkenClouds.Instance?.MainLayer?.config?.TryGetBand(out bottom, out top)` |
| `Core.SceneDepthRegistry`(每相机深度图注册表) | `_cam.GetComponent<CloudRenderer>()?.LinearSceneDepth` |
| `Core.SceneOrchestrator` + `PlanetEnvironment` 在 `Core` | **行星解析并入 `VolkenClouds`**,`PlanetEnvironment` 落到 `Volken.Clouds`;天气/UI 通过 `VolkenClouds.Instance` 拿行星与清单 |

**保留的判断**:只留**实现去重**(`TryGetBand` 一份)与**行星不重复解析**(原四处);规则见 §2.1。

**顺带:按子系统重组 `Weather/`(同批)**

```
Clouds/  CloudConfig.cs / CloudLayer.cs / VolkenClouds.cs / PlanetEnvironment.cs / ... + Shader/(Clouds.shader, CloudNoiseCompute.compute)
Weather/ VolkenWeather.cs / VolkenWeatherSettings.cs / WeatherPanel.cs   ← 域级(跨子系统)
  Rain/ RainParticles.cs + Shader/(RainParticles.compute, RainParticles.shader)
  Lightning/ LightningModule.cs / LightningBolt.cs + Shader/(LightningBolt.shader, LightningFlash.shader) + Audio/(volkenThrunder-{near-1..4, far-1..5}.wav)
  Fog/ 待实现(重做雾时新建)
```

`.meta` 随文件搬(保 GUID,前提是搬的方式保住了 `.meta`)。 **`LoadVolkenAsset<T>` / `ResourceLoader.LoadAsset<T>` 按工程路径字符串读(不是 GUID)** —— 共 6 处已同步:`Clouds.shader`、`CloudNoiseCompute.compute`、`RainParticles.{compute,shader}`、`LightningBolt.shader`、`LightningFlash.shader`。

### 2026-10-01(续)—— 用户把 shader 归入 `Shader/` 子目录后,踩到 GUID 陷阱

用户自行把资产归入 `Shader/`(`Clouds/Shader/`、`Weather/Rain/Shader/`、`Weather/Lightning/Shader/`),代码路径已同步(6 处);核对 `Assets/ModData.asset` 的 `_otherAssets` 时发现真问题:

| 资产 | 状况 |
|---|---|
| `Clouds.shader` | 文件搬走、**`.meta` 新生成 → GUID 变了**;`_otherAssets` 仍是旧 GUID → **悬空** |
| `CloudNoiseCompute.compute` | 同上,**悬空** |
| `enviro_thunder_1.ogg` | 资产早已删除(`eab24b4`),GUID 仍在清单 → **悬空**(另有 4 条同类残留此前已清) |

**后果**:打包后这两个资产**不进 bundle**,云 shader 与噪声 compute 都取不到,`Shader.Find("Hidden/Clouds")` 在成品里同样取不到 → **云整体消失且无编译错误**(`AGENT_CONTEXT.md` 铁律:**删/搬资产不会自动更新 GUID**)。**已修**:两条 GUID 换新值、删掉已删 ogg 残留 —— 现 **26 条 0 悬空**(见 [`README.md`](README.md) §四之三「打包 + 核对资产清单」)。

**顺带修掉的既有 bug(C5 之外,README「四之二」#1)**:三处(`VolkenClouds` / `CloudRenderer` / `VolkenUserInterface`)把 `config.enabled` 当"环境开关"来回写,而它属**玩家预设本体**、随"保存配置"落盘 —— 绕无大气卫星保存会把该行星云预设静默写成"关闭",回到有大气行星后 `enabled` 仍 false(**"切换至有大气星球时 config 卡住"**)。改法:新增 `CloudLayer.EnvironmentSuppressed`(运行时,不落盘),渲染只认 `enabled && !EnvironmentSuppressed`。**这是本次唯一的行为变更**,也是删掉 UI 重复门控的前提。

**验证**:`dotnet build Volken2.sln -t:Rebuild` → **0 错误**,警告 8 个 = 改动前基线**完全相同**(无新增);未做 Unity 侧打包/真机验证(见 §9 剩余项)。

### 2026-10-01(续 2)—— **移除"天气值"整套** 【决策:用户】

**理由(用户原话)**:JNO 的天气涉及**整颗星球**,不适合"单个连续标量 + 状态机"这套设计模式。

**删掉的**

| 类别 | 内容 |
|---|---|
| 状态机 | `WeatherValue` / `TargetWeatherValue` / `WeatherName` / `DescribeWeatherValue` / `IsRaining` / `ForceWeatherValue` / `SetTargetWeatherValue` / `PickRandomWeather` / `EstimateLengthOfDaySeconds` / `WeatherValueChanged` 事件 + `Tick` 里的随机/淡变推进 + `_timeSinceWeather*` / `_targetWeatherDuration` / `_isFoggyDawn` |
| 配置字段 | `overall`:`dynamicWeather` / `fixedWeatherValue` / `initialWeatherValue` / `maxWeatherValue` / `minDuration` / `maxDuration` / `fadeSpeed` / `updateInterval` / `timeScaleFollow` / `foggyDawn`;`rain.triggerValue`;`lightning.stormValue`;常量 `DefaultWeatherValue` |
| 整个 Section | **`CloudLinkage`**(`CloudLinkageSection` + XML 节点 + `EnsureSections`/`ClampAll`/`CopyFrom` 各处引用)—— 按定义就是"天气值 → 云",标量没了它也就没意义 |
| UI | 状态行的天气值、动态天气开关、强制天气值滑块、天气节奏四个滑块、黎明起雾开关、雨/雷的"触发天气值"滑块;**`WeatherPanel` 的天气组现在只剩一个总开关** |
| 命令 | `volkenSetWeather` + `SetWeatherValue`(此前已随 dev 命令精简移除);`volkenWeather` 输出去掉 value/target/dynamic 三项 |
| 本地化 | 三语言各删 **16 条** key(`WeatherValue`/`WeatherDynamic`/`WeatherManualValue`/`WeatherTiming`/`WeatherMinDuration`/`WeatherMaxDuration`/`WeatherFadeSpeed`/`WeatherTimeScaleFollow`/`WeatherFoggyDawn`/`RainTriggerValue`/`LightningStormValue`/`WeatherCloudLinkage`/`CloudLinkage*`) |

**改为**:雷电 = `lightning.enabled` 开着就按 `minDelay/maxDelay` 持续落雷(删掉 `StormLoop` 里 `WeatherValue < stormValue` 的复核);雨 = `rain.enabled` 开着就下。**保留**:`LocalSolarHour` / `ComputeLocalSolarHour`(不依赖天气值)与 `CameraCloudFade`(云层带派生量);**黎明起雾挪进雾 config** —— 新增 `fog.dawnFog`(+ 三语言词条 `Volken.UI.FogDawnFog`)。**兼容**:旧 weather.xml 的 `<CloudLinkage>`、`<maxWeatherValue>`、`<triggerValue>` 等节点被 `XmlSerializer` **静默忽略**,不会反序列化失败(README §四之二 #5)。**验证**:`dotnet build -t:Rebuild` → **0 错误 / 8 警告(= 基线)**;`.cs` 里天气值符号 grep **0 命中**;三语言 key 集合**完全一致**(各 198 条)。

### 剩余(未排期)

| 项 | 内容 |
|---|---|
| A2 | 相机海拔公式仍有 **3 份**(`CloudRenderer` / `RainParticles` / `VolkenWeather`),回退值不同(0 / −1 / 保持上帧);可提取 `ViewEnvironment` |
| D | `PlanetConfigList.xml` 仍被云/天气两个写者整体重写(丢更新风险) |
| — | **Unity 侧打包 + 真机验收**:默认配置逐字节一致、云正常出现/被抑制、雨软粒子 A/B、雷仍从云层带内起 |
| — | 雾仍是占位(`fog.dawnFog` 有字段与 UI,但**没有消费者** —— 重做雾时实现) |
