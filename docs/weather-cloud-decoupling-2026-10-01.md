# 天气 ↔ 云 解耦审计与重构方案

> 状态: 🚧 部分落地(2026-10-01 已完成 A1/A3/B/C/E + 环境抑制修复;**A2、D、F 未排期**)
> 日期: 2026-10-01
> 关联: [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md)(天气母计划,本文只谈**结构**不谈功能范围)、[`proposals/thunder-realism-2026-09-28.md`](proposals/thunder-realism-2026-09-28.md)(雷电)、[`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md)(雨,本文的 C3/C4/C8 直接落在它身上)、[`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md)(雨雾复盘)
> 主题: 审计「天气」与「云」两个模块之间**实际存在**的依赖,给出把边界划回 OOP 的分阶段重构方案。

---

## 0. 结论先行

**先说一个反直觉的事实**:两边并没有"粘在一起"。

```
grep -i "weather" Assets/Scripts/Volken/Clouds/         → 0 命中
grep -i "weather" Assets/Scripts/Volken/Clouds/*.shader → 0 命中
grep -i "weather" Assets/Scripts/Volken/Clouds/*.compute→ 0 命中
```

云模块**完全不知道天气存在**。真正的问题是另外三件事:

1. **依赖方向反了**。是天气(消费者)穿透到云的内部对象图:
   `Volken.Core.VolkenMod.Instance?.MainLayer?.config.layerHeights/layerSpreads/layerStrengths` ——
   一个全局单例 → 一个编排器类 → 一个层列表 → 一个配置类的 `Vector4` 字段布局,**四级穿透,跨 3 个命名空间**。
   这不是耦合"太紧",是**耦合打在了错的地方**(拿到的是实现细节,不是能力)。
2. **那条隐式契约被复制了两份**。同一段"云层高度带 = 所有启用层 `h ± spread` 的并集"的逻辑,在
   `VolkenWeather` 与 `LightningModule` 里逐字重复了一遍,**没有任何代码拥有它**。
3. **该耦的地方反而没耦**。`VolkenWeather.IsRaining` 注释自己写着「2026-09-27 起:无消费者」,
   而 `RainParticles` 对 `WeatherValue` 是**零读取** —— 天气状态机现在只驱动雷电,
   它和雨之间根本没有驱动关系(雨只被面板开关 / `rain.enabled` 驱动)。

**所以修法不是"把两个模块拆开",而是三件事**:
① 给"云层高度带"找一个**所有者**;
② 把"全局单例穿透"换成**只读接口 + 由生命周期拥有者推送**;
③ 把散落的场景/行星生命周期收成**一个源**。
顺带修掉两份重复公式(C4)与共享可变持久化(C5)。

**不建议做的事**:不要为了"对称"让云反过来依赖天气;不要引入 DI 容器;不要顺手恢复"天气值 → 云覆盖度"联动
(README 决策速查已拍板"天气系统不联动云层")。

### 0.1 分支现状核对(2026-10-01,对照 `origin/main` = `7690fd7`)

审计前先确认了一件事 —— **`main` 分支上根本没有天气系统**:

| 检查项 | `origin/main` 结果 |
|---|---|
| 全部 28 个 `.cs` 里 grep `weather`(忽略大小写) | **0 命中** |
| `Assets/Scripts/Volken/Weather/` 目录 | **不存在**(无 `VolkenWeather.cs` / `RainParticles` / `LightningModule` / `VolkenWeatherConfig.cs`) |
| `Core/PlanetConfig.cs` | 只有 `CloudConfigName` / `ExtraCloudConfigName`,**无 `WeatherConfigName`、无 `LegacyWeather`** |
| 编排器类名 | 还是 `Core/Volken.cs` 的 `public class Volken`(dev 才改名 `VolkenMod.cs`) |
| `Clouds/**` 与 dev 的关系 | **无结构分叉** —— 忽略空白后仅 `96 增 / 30 删`(命名空间包装 + `globalRotationAngular` 等默认值微调) |

**全部天气代码只活在 `dev` 的 4 个尚未合并的提交里**:`5b89ad8` → `8e8191c` → `eab24b4` → `f67ce23`。
远端 PR 最高到已合并的 `#17` —— **目前没有 dev → main 的开放 PR**。
(反向:`dev` 还缺 `main` 独有的 `LICENSE` 与 PR #9 等 7 个 merge/license 提交。)

**对本方案的影响(重要)**:

1. 本审计的对象是 `dev`,结论对 `main` 暂时不成立 —— 但 **dev 一旦合并,§1 的 C1~C10 会整体落到 `main`**。
   所以**在 dev → main 之前做完阶段 A + B,`main` 就永远不必接收这份耦合**。这是"现在就做"的最强理由。
2. 阶段 A 里唯一会流向 `main` 的改动是**云侧的纯去重**(`CloudConfig.TryGetBand` 一处实现),对"只有云"的 `main` 是纯收益,不会带进任何天气代码。
3. **不要在 `main` 上开工** —— 那会制造第三次分叉(`dev` / `main` 之外再多一条线)。

---

## 1. 耦合点清单(逐条带证据)

> 行号对应本次审计时的代码状态(2026-10-01)。

### C1 — 云层高度带被重复实现两份,且无所有者 【严重度:高】

| 位置 | 内容 |
|---|---|
| `Assets/Scripts/Volken/Weather/VolkenWeather.cs:828-860` | `ComputeCameraCloudFade` 遍历 `layerHeights/layerSpreads/layerStrengths`,取 `strength>0` 层的最小底 / 最大顶,再算 `1 − InverseLerp(bottom, top−400, asl)` |
| `Assets/Scripts/Volken/Weather/LightningModule.cs:564-595` | `TryGetCloudBand` —— **同一段遍历逐字重复**,只有变量名不同(`bottom/top` vs `lo/hi`) |

契约"云层高度带 = 启用层 `h±spread` 的并集"只活在这两处注释的一句口头约定里:
`LightningModule.cs:561-562` 写「与 `VolkenWeather.ComputeCameraCloudFade` 同一套数据源」——
**这句注释就是全部的契约**。`CloudConfig` / `CloudLayer` / `CloudRenderer` 都不提供这个查询。

**为什么是问题**:任何云层语义变更(层数从 4 改成 N、`spread` 从"半高"改成"底/顶"、按分辨率缩放高度)
都必须同时改两处 + 两处注释,漏一处就是**静默的错**(雷从云外劈出来、雨在云里不淡出),没有编译错误。

### C2 — 天气持有对云模块内部对象图的硬路径(Service Locator 穿透)【严重度:高】

```csharp
// VolkenWeather.cs:832
var main = Volken.Core.VolkenMod.Instance?.MainLayer;
var cloudCfg = main?.config;

// LightningModule.cs:570
var cloudCfg = Volken.Core.VolkenMod.Instance?.MainLayer?.config;
```

`Volken.Weather` 命名空间因此在编译期依赖:
- `Volken.Core.VolkenMod`(一个职责是"场景编排 + 云层持有"的类);
- `Volken.Clouds.CloudConfig` 的**字段布局**(`Vector4 layerHeights / layerSpreads / layerStrengths` 的 x/y/z/w 语义)。

**为什么是问题**:这是"不 OOP"的**核心现场**。天气拿到的不是"云在哪一层"这个**能力**,
而是"云的配置对象长什么样"这个**实现细节**;而且是通过全局单例拿的 —— 谁都不知道谁依赖谁,
单测无法替身,改 `CloudConfig` 字段名会静默影响天气(同一编译单元的强耦合)。

### C3 — 雨按「具体组件类型」取云渲染器的产物 【严重度:中】

```csharp
// RainParticles.cs:1088-1094
var cr = _cam.GetComponent<CloudRenderer>();
if (cr != null) depthTex = cr.LinearSceneDepth;   // CloudRenderer.cs:69
```

语义上应该是"向当前视图要一张线性深度图",写成了"在相机 GameObject 上找名叫 `CloudRenderer` 的组件"。

**旁证**:`CloudRenderer.cs:56-69` 的注释自己写着「**当前无消费者** —— 若确定不再需要,删除这个属性即可」——
**注释已经过期**(雨正在消费它,`README.md` 决策速查第 72 行也已经更正过这一点,但源码注释没跟上)。
这说明这个只读口从来没有被定义成一份**契约**(哪台相机、什么时候 ready、单位是什么都没有约定)。

### C4 — 相机海拔公式三份实现,回退值还不一致 【严重度:中】

| 位置 | 回退值 |
|---|---|
| `CloudRenderer.cs:518-533` `ComputeCameraAltitude()` | `0f` |
| `RainParticles.cs:594-610` `ComputeCameraAltitude(Camera)` | `−1f`(语义 = 闸门不生效) |
| `VolkenWeather.cs:735`(内联在 `UpdateCameraMetrics`) | 保持上一帧 |

三处 `|camPos − planetCenter| − radius`。**回退值不同已经是行为差异**,不只是重复代码。

### C5 — 持久化是共享可变状态,天气名挂在云记录上 【严重度:中高(可能丢数据)】

- `PlanetConfig`(`PlanetConfig.cs:14-69`)一个类同时承载 `CloudConfigName` / `ExtraCloudConfigName` /
  `WeatherConfigName`,还留着迁移用的 `LegacyWeather`(**内联一整个 `VolkenWeatherConfig`**)。
  于是 `Core` 的清单类型反向 `using Volken.Weather;`(`PlanetConfig.cs:10`)。
- 同一份 `UserData/VolkenConfig/PlanetConfigList.xml` 有两个写者:
  - 云侧:`AddConfig` / `SetConfig`(`PlanetConfig.cs:237-264`);
  - 天气侧:`SetWeatherConfig`(`PlanetConfig.cs:199-212`),入口是 `VolkenWeather.RegisterPresetName`(`VolkenWeather.cs:398-408`)。
- 每一方保存都是**整体重写**整个清单;而 `VolkenMod.OnSceneLoaded:193` 还会整个 `LoadFromFile` **替换实例对象**。

**风险场景**:云面板保存与天气面板保存互相覆盖(两个模块各持一个 `VolkenMod.Instance.planetConfigList` 引用,
读-改-写之间若有重载 → 丢更新)。当前之所以没炸,是靠"两边都在同一帧、都从 `VolkenMod.Instance` 现取"这个巧合。

### C6 — 天气配置里塞了"云"的概念(死字段 + 死 UI 组)【严重度:低(功能)/ 中(认知)】

- `VolkenWeatherConfig.CloudLinkageSection`(`VolkenWeatherConfig.cs:123-146`):`enabled` + 三个 gain,**零消费者**。
- `WeatherPanel.BuildCloudLinkageGroup`(`WeatherPanel.cs:253-269`):整组禁用 + 灰字"待实现"。
- README 决策速查第 79 行记录了"保留完整占位"的决定。

**注意**:它不是耦合,而是**耦合的墓碑**。副作用是每个读天气配置的人都会先怀疑"天气是不是在偷偷改云"——
这正是本次提问的来源之一。

### C7 — UI 归属:天气面板挂在云检查器里 【严重度:低(有意为之)】

`VolkenUserInterface.cs:318` 把 `WeatherPanel.Build(inspectorModel)` 追加进标题为
`Volken.UI.CloudSettings` 的检查器(`VolkenUserInterface.cs:293-294`)。
`WeatherPanel.cs:30` 明确写了理由:"天气与云也该在同一处调,才是'一套观感参数'"。

**建议保持不动**,只在调用点补一句注释固化这个决定(属于"合理耦合":UI 聚合 ≠ 模块耦合)。

### C8 — 生命周期:两个编排器 + 云侧直接调天气具体类 【严重度:中】

- `VolkenMod.OnSceneLoaded`(`VolkenMod.cs:188-291`)与 `VolkenWeather.OnSceneLoaded`(`VolkenWeather.cs:207-234`)
  **各自**订阅 `SceneManager.SceneLoaded`,**各自**解析行星名、**各自**判断大气、**各自**处理 SOI 切换。
- `VolkenMod.cs:283` 与 `VolkenMod.cs:385` 直接调用 `Volken.Weather.RainParticles.AttachToCurrentView()` ——
  编排器对天气的**具体静态类**硬引用。

**后果**:初始化顺序变成隐式契约 —— `Mod.OnModLoaded` 必须"先 `VolkenMod.Initialize()` 再 `VolkenWeather.Initialize()`"
(`Mod.cs:63-64`);两次 `SceneLoaded` 之间的时序决定 `RainParticles` 读到的 `VolkenWeather.Instance.Config`
是不是新行星的。现在靠"同帧 + 幂等"侥幸成立,任何一边加重活就会暴露。

### C9 — 该耦合的地方没有耦合:天气 ↔ 雨/云 无语义通道 【严重度:架构级】

- `VolkenWeather.IsRaining`(`VolkenWeather.cs:452-459`)读 `Config.rain.triggerValue`,注释自陈「无消费者」。
- `RainParticles.cs` 全文对 `WeatherValue` / `IsRaining` **零命中**;它只由面板开关与 `rain.enabled` 驱动。

**这是本次审计最重要的一条**:所谓"耦合太高"的体感,根源不是两边粘住了,而是
**数据面偷了云的内部状态(不该耦的耦了)、语义面却没有天气状态到降水的通道(该耦的没耦)**。
修 C1/C2 不会让雨"响应天气";要那件事必须显式做一套事件通道(见 §3.6),而且**默认关**。

### C10 — 注释与实现互相矛盾(文档债,直接制造"耦合感")【严重度:低】

| 位置 | 问题 |
|---|---|
| `VolkenWeather.cs:72-73` | 写「预设名与云层**共用**,换预设时云与天气一起换」—— 与同文件 `:54-58`、`PlanetConfig.cs:26-29`、README 决策速查第 75 行的「**完全独立**」直接矛盾(旧改动残留) |
| `CloudRenderer.cs:56-69` | 写 `LinearSceneDepth`「当前无消费者」—— 实际 `RainParticles.cs:1089` 正在消费 |
| `VolkenWeather.cs:29-30` | 写「风沿用 `CloudConfig.windSpeed/windDirection`」—— 当前雨不读云配置的风(雨有自己的 `windInfluence`,且 SR2 无风场),描述已失效 |

---

## 2. 目标依赖图

> ⚠️ **【2026-10-01 修订】本节初版的"接口 + 值对象 + 推送"路线已被用户判定为过度抽象并回撤。**
> 现行规则见下方"实际采用的规则",§2 初版内容保留作决策记录。

**初版规则(已放弃)**:跨模块只能依赖**接口 + 值对象**,不能依赖对方的对象图;编排器**推送**能力,消费者**不拉取**单例。

```
                    ┌──────────────────────────────┐
                    │  Volken.Core (编排者)         │
                    │  · 唯一订阅 SceneLoaded / SOI │
                    │  · 广播 PlanetEnvironmentChanged
                    └───────┬──────────────┬───────┘
              push ICloudField       push IWeatherState
                            │              │
        ┌───────────────────▼──┐        ┌──▼─────────────────────┐
        │ Volken.Clouds        │        │ Volken.Weather          │
        │ 拥有 CloudBand 的唯   │        │ 状态机 + 雷 + 雨         │
        │ 一实现;实现 IViewDepth│        │ 只读接口,不碰 CloudConfig│
        │ Source(线性深度)      │        │                         │
        └──────────────────────┘        └─────────────────────────┘
                            ▲              │
                            └──────────────┘
                     两者共用 Volken.Environment 的
                     CameraAltitude / PlanetFrame 快照(C4 归位)
```

### 2.1 实际采用的规则(2026-10-01 最终)

> **两边需要对方的数据时,直接读对方的 config / 直接 `GetComponent`。不要为跨模块数据访问造接口层。**

```
        ┌──────────────────────────────┐        ┌──────────────────────────┐
        │ Volken.Clouds                │        │ Volken.Weather           │
        │  VolkenClouds.Instance       │◄───────│ 读 MainLayer.config      │
        │    .MainLayer.config         │        │   .TryGetBand(...)       │
        │    .planetConfigList         │◄───────│ 读行星预设名             │
        │    .PlanetChanged 事件       │───────►│ 天气订阅它               │
        │    .LinearSceneDepth         │◄───────│ RainParticles:           │
        │  VolkenWeather.Instance      │───────►│   GetComponent<>()       │
        │    .Config                   │        │  (云要天气参数时同理)     │
        │                              │        │                          │
        │ 【行星解析:订阅 SceneLoaded / │        │                          │
        │   PlayerChangedSoi,广播】     │        │                          │
        └──────────────────────────────┘        └──────────────────────────┘
```

**保留的两条(是"消除重复",不是"加间接")**:

1. **云层高度带的实现只有一份** —— `CloudConfig.TryGetBand(out bottom, out top)`,
   原先在 `VolkenWeather` 与 `LightningModule` 里各抄了一份。
2. **行星环境只有一个解析者** —— `VolkenClouds`(它本来就要按行星装云预设),原先有四处(云两份、天气一份、UI 一份)。

**撤掉的全部间接层**:`ICloudBandSource` + `CloudBandRegistry` + `CloudBand`、`Core.SceneDepthRegistry`,
以及 `Core.SceneOrchestrator`(行星解析搬回 `VolkenClouds`,`PlanetEnvironment` 落在 `Volken.Clouds`)。

**验收断言(修订版)**:`Weather/` 里出现 `VolkenClouds.Instance` / `GetComponent<CloudRenderer>()` 是**允许且期望**的;
不允许的是**把同一段逻辑抄两份**(如各自遍历 `layerHeights/layerSpreads/layerStrengths`)——
统一走 `CloudConfig.TryGetBand`。

---

## 3. 分阶段方案(每阶段独立可编译、可验收)

### 3.1 阶段 A —— 纯内聚,零行为变更 【风险:极低】

| 步骤 | 内容 |
|---|---|
| A1 | 把"云层高度带"归位成**唯一所有者**:在 `CloudConfig` 上加 `bool TryGetBand(out float bottomAsl, out float topAsl)`(`CloudLayer` 转发一层)。`VolkenWeather.ComputeCameraCloudFade` 与 `LightningModule.TryGetCloudBand` 都改为调用它 —— **消除 C1 的重复** |
| A2 | 新增 `Volken/Environment/ViewEnvironment.cs`(纯值快照 + 静态查询),统一相机海拔公式;`CloudRenderer` / `RainParticles` / `VolkenWeather` 三处改调它 —— **消除 C4**。注意各调用点**回退值语义不同**,先保持各自语义(传一个 `fallback` 参数),不要顺手改行为 |
| A3 | 修 C10 的三处过期注释 + 在 README 决策速查第 72 行的 `LinearSceneDepth` 行标记"已过期注释已修" |

**验收**:同一份云配置下,`volkenWeather` 日志的 `cloudFade`、雷电起点的 `cloudBottom/Top`、
雨的 `LastAltitude` 与改前**逐值一致**。回滚 = 纯删除,无状态迁移。

### 3.2 阶段 B —— 依赖反演(行为等价)【风险:低】—— ⚠️ **2026-10-01 已实施后又回撤**

| 步骤 | 内容 | 结局 |
|---|---|---|
| B1 | 新建 `Volken/Clouds/ICloudField.cs`:`bool TryGetBand(out CloudBand band);` + `float CameraCloudFade(float asl);`。**`CloudBand` 是纯 struct** | **❌ 已删除**(过度抽象) |
| B2 | `VolkenWeather` 增加 `SetCloudField(ICloudField)` 并由编排器推送;删除两处 `MainLayer?.config` —— **消除 C2** | **❌ 已回退**为直接读 config |
| B3 | 新建 `IViewDepthSource`,雨改为从注册表取,删除 `GetComponent<CloudRenderer>()` | **❌ 已回退**(`SceneDepthRegistry` 删除) |

**验收**:
- 静态:`grep` 断言 §2 的四组符号在 `Weather/**` 下零命中;
- 动态:`volkenWeather` + 雨"软粒子开/关"A/B 截图一致;雷仍从同一高度带出来。

**唯一风险点**:B2 的推送时机。若 `ICloudField` 在 `ApplyPlanet` 之后才到位,`ComputeCameraCloudFade` 会拿到上一帧的引用 →
**必须**像现有 `VolkenMod.Instance` 那样"每帧现取",即把"是否可用"做成 `ICloudField` 的 `null` 与否,
不要缓存 `CloudBand`。

### 3.3 阶段 C —— 生命周期单点化 【风险:中(时序)】

| 步骤 | 内容 |
|---|---|
| C1 | `VolkenMod` 成为**唯一**的 `SceneLoaded` / `PlayerChangedSoi` 订阅者,对外广播 `PlanetEnvironmentChanged(planetName, hasAtmosphere)`;`VolkenWeather` 删除自己的两个订阅 —— **消除 C8 的一半** |
| C2 | 删除 `VolkenMod.cs:283` 与 `:385` 的 `RainParticles.AttachToCurrentView()`,改由 `VolkenWeather` 在 `PlanetConfigLoaded` 事件里挂雨(它本来就有那个事件、也拥有 `Config`) —— **消除 C8 的另一半** |

**验收**:连续 `VolkenForceRefresh` ×5 + 快速切 SOI,`volkenWeather` 日志顺序稳定;
`RainParticles` 的头几次 `ApplyConfig` 读到的 `enabled` 与当前行星预设一致(现在靠时序侥幸,改后应确定性成立)。

### 3.4 阶段 D —— 持久化拆分 【风险:中(旧档兼容)】

| 步骤 | 内容 |
|---|---|
| D1 | 天气预设名搬进独立清单(`UserData/VolkenWeatherConfig/PlanetPresetList.xml`),或退一步:保留字段但 `SetWeatherConfig` 改为**先 `LoadFromFile` 合并再写**的读-改-写,并加进程内锁 —— **消除 C5 的丢更新** |
| D2 | 迁移:复用 `PlanetConfigList.LoadFromFile` 里已有的 `LegacyWeather` 迁移块(`PlanetConfig.cs:138-158`),把 `WeatherConfigName` 也搬过去 |

**硬约束**:`WeatherConfigName` 与 `LegacyWeather` 字段**都不能删** —— 项目约定"删字段会让旧 XML 同名节点被静默丢弃"
(见 `VolkenWeatherConfig.cs:13-20` 的兼容约定与 README 已知问题第 5 条同类教训)。

**验收**:手工 A/B —— 云面板"另存为新配置"与天气面板"另存为新配置"交替执行 10 次,
`PlanetConfigList.xml` 两边的名字都不丢。

### 3.5 阶段 E —— 清理与界定 【风险:低】

| 步骤 | 内容 |
|---|---|
| E1 | `CloudLinkageSection` 二选一:**(推荐)** 保留字段(旧 XML 兼容)但从 `WeatherPanel` 删掉该禁用分组,并在类注释里写明"这里不是耦合点,是墓碑";或**删除**并写「翻案说明」。**无论哪种,都要更新 README 决策速查第 79 行** |
| E2 | `VolkenUserInterface.cs:316-318` 补注释:天气面板进云检查器是**有意的 UI 聚合**,不是模块耦合(C7 固化决定) |
| E3 | 术语统一:全仓库把「天气不联动云层」与「预设名独立」两句话对齐(C10 的第一条) |

### 3.6 (可选,未排期)阶段 F —— 补上缺失的语义通道 【风险:中】

只有玩家确实想要"天气值影响降水/云量"时才做,**默认全关**:

- `VolkenWeather` 暴露 `event Action<float> WeatherValueChanged`(已存在);
- 雨/云**订阅**该事件,自己决定怎么用;天气**不去改**任何云配置(遵守 README 决策速查第 85 行);
- 面板上必须是"可以明确关掉、且默认关"的显式选项(`README.md` 第 73 行已确立此约定)。

> ⚠️ 与 C6 的区别:这里说的是**事件通道**,不是把 `CloudLinkageSection` 的三个 gain 复活。
> 若将来真做,建议**新增**字段而不是复用那三个占位 gain(它们的语义"从基线纯函数重算"已被否过,见
> `VolkenWeather.cs:461-473` 的历史记录)。

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

**做完 A + B 就拿到了 80% 的收益**:编译期依赖方向被扳正,重复契约消失,
"天气穿透云的内部"这件事在代码里再也写不出来(不是靠约定,是靠类型系统)。

---

## 5. 明确不做(Non-goals)

1. **不引入 DI 容器 / 事件总线框架**。工程里全是全局单例 + 事件,引入容器只会让读者多一层间接,
   收益远小于 `ICloudField` 一个接口。
2. **不让 Clouds 反过来依赖 Weather**。"对称"不是目标,单向依赖才是。
3. **不恢复"天气值 → 云覆盖度/云厚度/云风速"的隐式联动**。README 决策速查第 85 行已拍板;
   要改必须走 §3.6 的显式、默认关、可关选项。
4. **不动 `Clouds.shader` / raymarch / TSS 管线**。本文只谈 C# 侧的对象边界。
5. **不为了解耦而重写 `PlanetConfigList` 的序列化格式**。阶段 D 优先选"读-改-写合并"这个低风险选项,
   独立清单文件是备选。
6. **不动 C7(UI 聚合)**。

---

## 6. 回归判据(全阶段通用)

- [x] ~~`Assets/Scripts/Volken/Weather/**` 内 grep 不到 `VolkenMod.Instance` / `CloudRenderer` / `layerHeights`~~ → **该断言已作废**(见 §2.1):现在**允许**直接读云的 config 与 `GetComponent<CloudRenderer>()`
- [x] **修订断言**:`Weather/` 里没有任何一处**自己重写**云层高度带遍历(统一走 `CloudConfig.TryGetBand`)
- [ ] 同一份云配置下,`volkenWeather` 的 `cloudFade` 数值与改前一致
- [ ] 闪电仍从云层带内起(近地雷 + 高云雷各测一次)
- [ ] 雨的软粒子开关 A/B 画面一致;云渲染器未挂时雨正常降级(不消失、不报错 —— 见雨计划 §3 的"空深度图让雨整体消失"护栏)
- [ ] 默认配置(天气全关 + 云 `enabled=false`)下行为与改前逐字节一致
- [ ] `dotnet build` 0 错误;打包前 `Player.log` 无新增 shader / NRE 报错
- [ ] 文档与代码同批提交(README §五 第 7 条)

---

## 7. 待拍板项

1. ~~是否开工?范围取 **A + B**(推荐)还是全量 A~E?~~ → **【决策:2026-10-01】用户选定:保留 `Volken` 前缀只改后缀 + 拆生命周期;范围 = 让雨/云互不联动、结构清晰。已按此落地 A1/A3/B/C/E。**
2. 阶段 D 选"读到-改-写到合并(低风险)"还是"独立清单文件(更干净但迁移面更大)"? —— **未排期**。
3. ~~C6 / E1:`CloudLinkageSection` 处置?~~ → **【决策:2026-10-01】保留字段(XML 兼容),删除面板禁用分组**,并在类注释里写明"这是遗留占位,不是耦合"。逻辑删除时按 §3.6 走事件通道,不复用这三个 gain。
4. 阶段 F 是否需要? —— **未排期**(仅在玩家提出"天气值要影响降水/云量"时做)。
5. ~~接口层留不留?~~ → **【决策:2026-10-01,用户】不留。** `ICloudBandSource`/`CloudBandRegistry`/`CloudBand`/`SceneDepthRegistry`/`SceneOrchestrator` 全部删除;**天气与云互相需要数据时直接读对方的 config / 直接 `GetComponent`**;**行星生命周期由 `VolkenClouds` 自己承担**,不另设编排器。保留的只有"实现去重"与"行星不重复解析"。详见 §2.1 与 §8。
6. ~~`Weather/` 目录怎么分?~~ → **【决策:2026-10-01,用户】按子系统分**:`Rain/`、`Lightning/`(含 `Audio/`)、`Fog/`(待实现);域级文件留在 `Weather/` 根。详见 §8。

---

## 8. 实施记录

### 2026-10-01 —— 改名 + 拆生命周期 + 解耦(已落地)

**改名(方案 B:保留 `Volken` 前缀,只把后缀改清楚)**

| 旧 | 新 | 位置变化 |
|---|---|---|
| `Volken.Core.VolkenMod` | `Volken.Clouds.VolkenClouds` | `Core/` → `Clouds/`(类名与命名空间一起表明"这是云") |
| `Volken.Weather.VolkenWeatherConfig` | `Volken.Weather.VolkenWeatherSettings` | `Core/` → `Weather/` |
| ~~`Volken.Core.SceneOrchestrator`~~ | **已删除(同日回撤)**;行星解析并入 `VolkenClouds`,`PlanetEnvironment` 落在 `Volken.Clouds` |
| ~~`Volken.Core.ICloudBandSource` + `CloudBand` + `CloudBandRegistry`~~ | **已删除(同日回撤 —— 过度抽象,见下)** |
| ~~`Volken.Core.SceneDepthRegistry`~~ | **已删除(同日回撤)** |

机械改名共 **261 处 / 18 个文件**。⚠️ 踩过的坑:无脑替换把**磁盘目录字面量** `UserData/VolkenWeatherConfig/` 也改成了 `VolkenWeatherSettings`
(`VolkenWeatherSettings.CONFIG_FOLDER`),会让所有老玩家配置"消失" —— 已单独回退该路径串(共 6 个文件)。
**教训:改类名前先 grep 这个标识符是否同时被用作字符串/路径。**

**结构:行星生命周期收成一处(C8)**

> ⚠️ 初版是抽出一个 `Core.SceneOrchestrator` 类;同日按用户意见**撤销**,行星解析改由 `VolkenClouds` 自己承担
> (见下方"同日回撤")。下面是**最终形态**。

- `VolkenClouds` 订阅 `SceneLoaded` / `PlayerChangedSoi`,解析 `PlanetEnvironment` 并广播 `PlanetChanged`;
  它同时持有 `planetConfigList`。(`VolkenUserInterface` 仍订阅 `SceneLoaded`,但只为"进飞行场景建面板"。)
- `VolkenWeather` 删掉自己的两个游戏事件订阅,改为订阅 `VolkenClouds.PlanetChanged`。
- `VolkenClouds` 原先分居 `OnSceneLoaded` / `OnPlayerChangedSoi` 的**两份不一致**的行星装配逻辑合并成一份
  (统一取 SOI 那份更完整的语义:明确处理"绕恒星"与"无大气行星")。
- `VolkenMod.cs` 直接调 `RainParticles.AttachToCurrentView()` 的两处删除;雨改由天气侧自己管。
- `VolkenUserInterface.OnPlayerChangedSoi`(**第四份**重复的行星解析 + 大气门控 + 渲染器装配,~50 行)**整体删除**。
- `CloudRenderer` 的 `PlayerChangedSoi` 订阅改为订阅 `VolkenClouds.PlanetChanged`,并在 `OnDestroy` 里**退订**
  (旧实现订阅了却从不退订 → 相机销毁后仍被事件链引用)。

**解耦(C1 / C2 / C3)**

- 云层高度带的遍历**只剩 `CloudConfig.TryGetBand()` 一处**;`VolkenWeather` 与 `LightningModule` 里各抄一份的
  副本删除,两者**直接读** `VolkenClouds.Instance?.MainLayer?.config` 调它。
- 雨的软粒子直接 `_cam.GetComponent<CloudRenderer>().LinearSceneDepth`。

**⚠️ 同日回撤(【决策:2026-10-01】用户:不必过度抽象)**

初版按"接口 + 值对象 + 注册表"做的 B 阶段被整体撤掉:

| 撤掉的东西 | 替回的做法 |
|---|---|
| `ICloudBandSource` + `CloudBand` + `CloudBandRegistry`(3 个类型/文件) | `VolkenClouds.Instance?.MainLayer?.config?.TryGetBand(out bottom, out top)` |
| `Core.SceneDepthRegistry`(每相机一张深度图的注册表) | `_cam.GetComponent<CloudRenderer>()?.LinearSceneDepth` |
| `Core.SceneOrchestrator`(编排器类)+ `PlanetEnvironment` 在 `Core` | **行星解析并入 `VolkenClouds`**(它本来就要按行星装云预设),`PlanetEnvironment` 落到 `Volken.Clouds`;**天气/UI 通过 `VolkenClouds.Instance` 拿行星与清单** |

**保留的判断**:只有两条留在代码里 ——
**实现去重**(`TryGetBand` 一份)与**行星不重复解析**(原先四处)。
它们消除的是**重复**,不是加**间接**;而接口层/注册表/编排器只是把"直接读"换成"绕一圈",没有消除任何重复。
现行规则见 §2.1。

**顺带:按子系统重组 `Weather/`(同批)**

```
Clouds/   CloudConfig.cs / CloudLayer.cs / VolkenClouds.cs / PlanetEnvironment.cs / ...
  Shader/    Clouds.shader / CloudNoiseCompute.compute

Weather/  VolkenWeather.cs / VolkenWeatherSettings.cs / WeatherPanel.cs   ← 域级(跨子系统)
  Rain/       RainParticles.cs
              Shader/  RainParticles.compute / RainParticles.shader
  Lightning/  LightningModule.cs / LightningBolt.cs
              Shader/  LightningBolt.shader / LightningFlash.shader
              Audio/   volkenThrunder-{near-1..4, far-1..5}.wav
  Fog/        待实现(重做雾时新建)
```
`.meta` 随文件一起搬(保 GUID,**前提是搬的方式保住了 `.meta`**)。
⚠️ **`LoadVolkenAsset<T>` / `ResourceLoader.LoadAsset<T>` 是按工程路径字符串读的(不是 GUID)** ——
共 6 处已同步:`Clouds.shader`、`CloudNoiseCompute.compute`、`RainParticles.{compute,shader}`、
`LightningBolt.shader`、`LightningFlash.shader`。

### 2026-10-01(续)—— 用户把 shader 归入 `Shader/` 子目录后,踩到 GUID 陷阱

用户随后自行把资产按子系统归入 `Shader/`:`Clouds/Shader/`、`Weather/Rain/Shader/`、`Weather/Lightning/Shader/`。
代码路径已同步(6 处)。**但核对 `Assets/ModData.asset` 的 `_otherAssets` 时发现真问题**:

| 资产 | 状况 |
|---|---|
| `Clouds.shader` | 文件是搬走的、**`.meta` 新生成了 → GUID 变了**;`_otherAssets` 里还是旧 GUID → **悬空** |
| `CloudNoiseCompute.compute` | 同上,**悬空** |
| `enviro_thunder_1.ogg` | 该资产早已删除(`eab24b4`),它的 GUID 仍留在清单里 → **悬空**(另外 4 条同类残留此前已清) |

**后果**:打包后这两个资产**不会进 bundle** —— 云 shader 与噪声 compute 都取不到,
`Shader.Find("Hidden/Clouds")` 在成品里同样取不到 → **云整体消失,且没有任何编译错误**。
这正是文档里 `AGENT_CONTEXT.md`「mod 打包清单」那条铁律的实例:**删/搬资产不会自动更新 GUID**。

**已修**:清单里两条 GUID 换成新值、删掉已删 ogg 的残留 —— 现 26 条 **0 悬空**
(自检脚本见 [`to-do.md`](to-do.md) 第 3 条)。

**顺带修掉的既有 bug(C5 之外,README「四之二」#1)**

三处(`VolkenClouds` / `CloudRenderer` / `VolkenUserInterface`)都把 `config.enabled` 当"环境开关"来回写 ——
而它是**玩家预设本体里的字段**,会随"保存配置"落盘。后果:绕一颗无大气卫星时点保存 → 该行星的云预设被静默写成"关闭";
回到有大气行星后 `enabled` 仍是 false = **"切换至有大气星球时 config 卡住"**。
改法:新增 `CloudLayer.EnvironmentSuppressed`(运行时,不落盘),渲染只认 `enabled && !EnvironmentSuppressed`;
环境与用户开关彻底分离。**这是本次唯一的行为变更**,也是删掉 UI 里那份重复门控的前提(否则会真回归)。

**验证**:`dotnet build Volken2.sln -t:Rebuild` → **0 错误**,警告 8 个 = 与改动前基线**完全相同**(无新增)。
未做 Unity 侧打包/真机验证(见 §9 剩余项)。

### 2026-10-01(续 2)—— **移除"天气值"整套** 【决策:用户】

**理由(用户原话)**:JNO 的天气涉及**整颗星球**,不适合用"单个连续标量 + 状态机"这套设计模式。

**删掉的**

| 类别 | 内容 |
|---|---|
| 状态机 | `WeatherValue` / `TargetWeatherValue` / `WeatherName` / `DescribeWeatherValue` / `IsRaining` / `ForceWeatherValue` / `SetTargetWeatherValue` / `PickRandomWeather` / `EstimateLengthOfDaySeconds` / `WeatherValueChanged` 事件 + `Tick` 里的随机/淡变推进 + `_timeSinceWeather*` / `_targetWeatherDuration` / `_isFoggyDawn` |
| 配置字段 | `overall`:`dynamicWeather` / `fixedWeatherValue` / `initialWeatherValue` / `maxWeatherValue` / `minDuration` / `maxDuration` / `fadeSpeed` / `updateInterval` / `timeScaleFollow` / `foggyDawn`;`rain.triggerValue`;`lightning.stormValue`;常量 `DefaultWeatherValue` |
| 整个 Section | **`CloudLinkage`**(`CloudLinkageSection` + XML 节点 + `EnsureSections`/`ClampAll`/`CopyFrom` 各处引用)—— 它按定义就是"天气值 → 云",标量没了它也就没意义 |
| UI | 状态行的天气值、动态天气开关、强制天气值滑块、天气节奏四个滑块、黎明起雾开关、雨/雷的"触发天气值"滑块;**`WeatherPanel` 的天气组现在只剩一个总开关** |
| 命令 | `volkenSetWeather` + `SetWeatherValue`(此前已随 dev 命令精简移除);`volkenWeather` 输出去掉 value/target/dynamic 三项 |
| 本地化 | 三语言各删 **16 条** key(`WeatherValue`/`WeatherDynamic`/`WeatherManualValue`/`WeatherTiming`/`WeatherMinDuration`/`WeatherMaxDuration`/`WeatherFadeSpeed`/`WeatherTimeScaleFollow`/`WeatherFoggyDawn`/`RainTriggerValue`/`LightningStormValue`/`WeatherCloudLinkage`/`CloudLinkage*`) |

**改为**:雷电 = `lightning.enabled` 开着就按 `minDelay/maxDelay` 持续落雷(删掉 `StormLoop` 里 `WeatherValue < stormValue` 的复核);
雨 = `rain.enabled` 开着就下。

**保留**:`LocalSolarHour` / `ComputeLocalSolarHour`(不依赖天气值)与 `CameraCloudFade`(云层带派生量)。
**黎明起雾挪进雾的 config**:新增 `fog.dawnFog`(+ 三语言词条 `Volken.UI.FogDawnFog`),不再是"总体"的一部分。

**兼容**:旧 weather.xml 里的 `<CloudLinkage>`、`<maxWeatherValue>`、`<triggerValue>` 等节点由 `XmlSerializer` **静默忽略**,不会导致反序列化失败(与"死配置字段可删"的结论一致,见 README §四之二 #5)。

**验证**:`dotnet build -t:Rebuild` → **0 错误 / 8 警告(= 基线)**;`.cs` 里天气值相关符号 grep **0 命中**;三语言 key 集合**完全一致**(各 198 条)。

### 剩余(未排期)

| 项 | 内容 |
|---|---|
| A2 | 相机海拔公式仍有 **3 份**(`CloudRenderer` / `RainParticles` / `VolkenWeather`),且回退值不同(0 / −1 / 保持上帧)。可提取 `ViewEnvironment` |
| D | `PlanetConfigList.xml` 仍被云/天气两个写者整体重写(丢更新风险) |
| — | **Unity 侧打包 + 真机验收**:默认配置下逐字节一致、云正常出现/被抑制、雨软粒子 A/B、雷仍从云层带内起 |
| — | 雾仍是占位(`fog.dawnFog` 有字段与 UI,但**没有消费者** —— 重做雾时实现) |
