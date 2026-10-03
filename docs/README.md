# Volken2 文档索引(docs/)

> 项目:Volken(SR2 / JNO 体积云 mod,Unity BIRP)。**新会话先读 [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**。
> **三区**:根 = 活跃(已动手) → [`proposals/`](proposals/) = 已论证待拍板 → [`archive/`](archive/) = 已完成 / 历史;规则见 [§五](#五文档写入规则维护约定)。
> 游戏日志:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`。

---

## 一、当前活跃(已动手)

| 文档 | 状态 | 一句话 |
|---|---|---|
| [`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md) | 🚧 **唯一在动手** | SP2 雨重做:4 条真难点 + 8 阶段计划;**实施记录 §10 是唯一事实源**(本文不复制) |
| [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md) | 🚧 部分落地 | 天气↔云解耦审计(C1~C10);已落地改名 / 生命周期 / 目录 / `TryGetBand`;A2 / D / F 未排期 |
| [`monobehaviour-scene-gate-2026-10-02.md`](monobehaviour-scene-gate-2026-10-02.md) | 🚧 实施中(待真机) | `Update` 是不是只在 FlightScene 跑:常驻组件按 `InFlight` 停表、`FindObjectsOfType` → `Camera.allCameras`、渲染路径去每帧 LINQ |
| [`rain-toggle-scene-switch-2026-10-02.md`](rain-toggle-scene-switch-2026-10-02.md) | 🚧 实施中(待真机) | 雨视觉开关换场景后打不开:资产就绪标志误用 `static` + 解耦后无人重挂雨 |

> **待办清单与已修复台账**见 [§四之三](#四之三待办清单backlog)(2026-10-02 由原独立文件 `to-do.md` 并入 —— 根目录只留「活跃方案 + 本文档 + 会话上下文」)。

## 二、提案 · 待拍板(`proposals/`,未在动手)

| 文档 | 状态 | 一句话 |
|---|---|---|
| [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md) | ⏸ 暂停(母计划) | 天气移植的**决策沿革 + 素材/反编译底账**;雨/雾实现已移除,活跃部分已拆给雨计划与雷声 |
| [`proposals/thunder-realism-2026-09-28.md`](proposals/thunder-realism-2026-09-28.md) | ⏸ 代码已落地,待打包 | 雷声按行星声速定延迟(Droo 340 / Cyleros 233 / Tydos 931 m/s);闪电残留与紫红已修;收尾 = Unity 打包 + 真机验收 |
| [`proposals/rain-lightning-perf-audit-2026-10-02.md`](proposals/rain-lightning-perf-audit-2026-10-02.md) | 📋 C# 侧已改,GPU 侧未排期 | 雨 / 雷高开销语句审计 + 收益排序;材质/网格/渐变共用、craft 链与 uniform 瘦身已落地,`FillArgs` 归约与分叉合批待定 |
| [`proposals/cloud-optimization-roadmap-2026-08-28.md`](proposals/cloud-optimization-roadmap-2026-08-28.md) | 📋 未排期(其 §3 #1 现状已更正) | 13 项优化点按收益排名(T0 部分已被方案 C / 轨道云消化) |
| [`proposals/hotpath-optimization-backlog-2026-10-02.md`](proposals/hotpath-optimization-backlog-2026-10-02.md) | 📋 未排期 | 剩余高开销四组(A 拖 UI 尖峰 / B 每帧 CPU / C 一次性 / D GPU)+ D 各方案**代价**;A 组已决定不改 |
| [`proposals/water-system-overhaul-2026-09-02.md`](proposals/water-system-overhaul-2026-09-02.md) | 📋 未排期 | 水体大修路线 A~E(A/B 零风险调参先行,E 换 shader 为终局) |

## 三、已归档(`archive/`,已完成 / 历史)

| 文档 | 一句话 |
|---|---|
| [`archive/ksa-temporal-upscale-port-2026-08-24.md`](archive/ksa-temporal-upscale-port-2026-08-24.md) | 方案 C:KSA 时序超采样移植(低清全量 raymarch + 全清上采样 + 运动自适应;含坐标原点重置) |
| [`archive/stock-cloud-distribution-2026-08-23.md`](archive/stock-cloud-distribution-2026-08-23.md) | 方案 B:游戏自带云 cubemap 作全球分布形状(`stockMapStrength=0` 逐字节回退) |
| [`archive/stock-density-scale-2026-09-06.md`](archive/stock-density-scale-2026-09-06.md) | 方案 A:自带云区域内密度缩放(初版阈值重映射已翻案弃用) |
| [`archive/coverage-decomposition-2026-08-27.md`](archive/coverage-decomposition-2026-08-27.md) | 覆盖分解 = `cloudCoverage × F_biome × F_rotDist × F_tiledDetail`(三强度默认 0) |
| [`archive/orbit-clouds-crossfade-2026-08-27.md`](archive/orbit-clouds-crossfade-2026-08-27.md) | 轨道 2D 云 + 过渡带交叉淡入(海拔分派;与体积云同源密度采样) |
| [`archive/seamline-reprojection-2026-08-25.md`](archive/seamline-reprojection-2026-08-25.md) | 割裂线根因 = 逻辑投影与 GPU clip 的 Y 约定相反 → `GL.GetGPUProjectionMatrix` |
| [`archive/jno-sceneloaded-nre-2026-08-27.md`](archive/jno-sceneloaded-nre-2026-08-27.md) | JNO 冲突:`OnSceneLoaded` NRE 中断事件链 → Volken 初始化被跳过 |
| [`archive/reflection-adaptation-2026-09-02.md`](archive/reflection-adaptation-2026-09-02.md) | 实时反射:方案 A(水面)已落地,方案 B(机体 cubemap 探头)未做 |
| [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) | **雨/雾移植失败复盘** —— 4 层根因 / 27 条铁律 / 8 条已证伪思路;**重做雨/雾前必读** |

> 归档文档头部「状态:」是最终结论;文档内未勾选项 = 待复跑 / 未排期,勿当待办执行。

## 四、决策速查

> **只放结论 + 出处,细节一律不在此重复** —— 主题文档才是事实源。
> 简写:「母计划」= [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md);「复盘」= [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md);「解耦」= [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md)。

| 决策 / 结论 | 出处 |
|---|---|
| 方案编号:A = 自带云区域内密度缩放;B = 自带云全球分布;C = KSA 时序超采样(三者均已实现并归档) | 见 §三 |
| **跨界移植五条铁律**:①细长 billboard 的长度轴必须在屏幕平面内表达;②诊断要量"你关心的那个量"所在的轴;③每帧路径禁止重置动画进度;④先标定数量级再调观感;⑤同一问题 3 轮无改善就换方案 | [复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) §5 |
| 天气**没有**"天气值"/全局档位:各子系统只看自己的 `enabled` + 节奏参数 | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 云与天气预设**各存各的、不配对**:云 `UserData/VolkenConfig/{行星}/{预设}.xml`,天气 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 / 母计划 §10.6 |
| **天气数据的唯一权威来源 = 预设 XML**:面板未保存的改动**不跨场景保留**(进飞行场景 / 换行星重读文件;开关"自己变回文件里的值"是预期);Flight→Flight 快速读档/回退发射**除外**(保留内存状态,属正常) | [雨开关](rain-toggle-scene-switch-2026-10-02.md) §3 |
| 天气**不联动云**;"读对方的 config" ≠ 联动 | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 命名:`VolkenMod` → `Clouds.VolkenClouds`;`VolkenWeatherConfig` → `Weather.VolkenWeatherSettings` | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 行星生命周期只归 `VolkenClouds`(订阅 `SceneLoaded` / `PlayerChangedSoi`,广播 `PlanetChanged` 给天气) | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 不搞过度抽象:`ICloudBandSource` / `CloudBand` / `CloudBandRegistry` / `SceneDepthRegistry` / `SceneOrchestrator` 均已撤 | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 云层高度带的**唯一实现** = `CloudConfig.TryGetBand`(勿再加接口 / 注册表 / 各自遍历) | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 资产按子系统归 `Shader/`;**移动资产必须同步 6 处路径字符串**(按工程路径读,不是 GUID) | [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md) §2 |
| 打包清单 = `Assets/ModData.asset` 的 `_otherAssets`(GUID);**增删资产都要改,悬空即静默失效** | 母计划 §10.5 |
| shader 错误只能靠 Editor.log 的 `Shader error in '<名>'` 离线抓;Unity 的 HLSL **没有 `expm1`** | 母计划 §10.2 ⑭ |
| GPU 回读间接绘制 `instanceCount` 可把"剔除"与"绘制"一次分开 | 母计划 §10.8 ④ |
| 雷:分叉材质 = **整道雷共享一份拷贝**,所有权归 `LightningBolt`(既不是"每分叉独占",也不是主干材质) | [性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) §2.1 |
| 程序化网格 / 渐变 = **静态共用**;雨每帧只采样一次 craft 链 + 缓存 `CloudRenderer` + 只上传变化过的 uniform | [性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) §2.2 / §2.3 / §2.4 |
| 每帧组件按 `PlanetEnvironment.InFlight` 启停(常驻天气 ticker / `LightningModule` / UI);**不新增 `SceneLoaded` 订阅者** | [场景门控](monobehaviour-scene-gate-2026-10-02.md) §1 / §2 |
| **挂在场景物体上的组件,换场景 = 新实例**:实例资源(compute / shader / material / buffer)的就绪标志**不得用 `static`** 门控,否则新实例跳过加载 → 永久 NOT READY | [雨开关](rain-toggle-scene-switch-2026-10-02.md) §0 |
| 取"本帧云层"一律走 `VolkenClouds.FillActiveLayers(缓冲)`,判据单点 `IsActiveLayer`;**渲染路径禁止 LINQ + 临时 List** | [场景门控](monobehaviour-scene-gate-2026-10-02.md) §2 |
| 路线图 #1 的光照解耦**已实现**(`lightStepSize` 是独立 uniform,样本数已按壳内长度自适应),`CreateDefault()` 的 `numLightSamplePoints` 现为 **25** → 该条只剩"改默认值 + 老配置迁移" | [hotpath 清单](proposals/hotpath-optimization-backlog-2026-10-02.md) §4.2 / §5 |
| 全局调试开关必须 `static`;多相机共享材质 → 每相机绘制前重设 uniform | 母计划 §10.4 J / G |
| 雨:密度基线 **0.139 个/m³**(EVE `rain-Kerbin`);域随相机速度放大时按 **r^2.5** 补密度。"时有时无"的真因是密度不足 | [复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) §5.1 / 母计划 §10.3 |
| 雨:"减玩家速度"与"减相机速度"是两个量,混成一个量是多次返工的根源 | [复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) §5.1 ⑥ |
| JNO **没有风系统**(`WindManager` / `WindVelocity` 零命中)→ 风只能来自 `CloudConfig.windSpeed/windDirection` | 母计划 §10.4 A |
| ❌ **已删除 / 勿照做**:`WeatherTypes`、天气值整套(`WeatherValue` 等)、`CloudLinkage`、`rain.triggerValue`、`lightning.stormValue`、天气独立配置目录 | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 / 母计划 §10.5 / §10.6 |
| ❌ 旧雨/雾实现已移除,**且不可回取**:`%TEMP%\volken-rain-fog-stash` 已被清理、不存在 | 母计划 §10.5 / 雨计划 §10 |
| ✅ 更正:`CloudConfig` 里的废弃字段**可以放心删**(`XmlSerializer` 忽略未知节点,不会反序列化失败) | §四之二 #4 |

**当前待定**

- 水体大修实施顺序(未排期)→ [`proposals/water-system-overhaul-2026-09-02.md`](proposals/water-system-overhaul-2026-09-02.md)
- 优化点剩余项(Light Volume / PlaceRays / HDR RT / MV 膨胀等)→ [`proposals/cloud-optimization-roadmap-2026-08-28.md`](proposals/cloud-optimization-roadmap-2026-08-28.md)
- 雨 / 雷 GPU 侧优化(`FillArgs` 组内归约、85 个 `LineRenderer` 合批、`arcs × splits` 乘积上限)→ [性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) §3
- 方案 C 已知缺口:N/S 风重投影、flip/flop 双缓冲、TSS 开时 `!isFresh` 硬回退闪烁
- 解耦剩余项:A2(相机海拔公式仍 3 份)、D(`PlanetConfigList.xml` 双写者)、F(天气值→降水通道,需玩家提出)+ Unity 真机验收 → [解耦](weather-cloud-decoupling-2026-10-01.md) §8

## 四之二、当前代码里的已知问题

> 已核实、尚未动手修的问题,供下次开工直接取用。

| # | 问题 | 证据 / 影响 |
|---|---|---|
| 1 | **星环渲染顺序错误**(低) | 星环相对云 / 水体的层级不对 |
| 2 | **Craft 高轨道时云层 scale 错误**(低) | 高轨道下云的缩放不符合预期 |
| 3 | **N/S 风重投影缺口**(云空间重投影只覆盖绕 Y 自转 + 东西风平移) | 方案 C §5 / 割裂线 §7;强南北风 → 残影鬼影,需完整 worldToCloud 矩阵 |
| 4 | ~~`CloudConfig.low/mid/highAltitudeThreshold` 死配置~~ **✅ 已更正(2026-10-01)** | 字段早在 `fe87e59` 删除且全仓库 0 命中;**"不可删除"的旧告警是错的推测**(`XmlSerializer` 对未知节点默认忽略、不抛异常)。归档文档里的旧结论仅作决策记录 |

---

## 四之三、待办清单(backlog)

> 📋 活跃。**方案与实施记录一律在主题文档**,此处只留可执行条目;修好后移入下面的「已修复台账」(一行摘要 + 指针)。
> (本节 2026-10-02 由原独立文件 `to-do.md` 并入 —— 减少散落的 md。)

**🚧 进行中:雨重做(SP2 `ParticleDomain` 移植)** —— 执行细案与全部实施记录见 [雨计划](sp2-rain-particledomain-port-2026-09-28.md) §10(**唯一事实源,此处不复制**)。现状:阶段 3 雨滴 shader 已落地,并已搬进编辑器预览台(`Assets/Scripts/VolkenTests/RainPreview.cs`)迭代。**下一步** = 拿 SP2 实机对比观感 → 阶段 4(密度 0.139~0.191 个/m³ 标定、自适应域半径、`_mainLightIntensity`、遮挡剔除);**定稿后仍需打包一次**做游戏内验收。

**真机验证(打包后才能做)**

1. **雷电**:`LightningBolt.shader` 是否被 Unity 编译、日志 `loaded thunder clips: near=4/4 far=5/5`、`volkenBolt` 手动劈雷。
2. **雷声真实化**:日志应逐条打印 `thunder [near|far] dist=… delay=… vol=…`。判据:① `thunderDistanceAttenuation=0` 时听感回到原版 0.05 s;② Droo(340 m/s)与 Tydos(931 m/s)同落点距离下延迟差约 2.7 倍;③ 连劈多次不互相打断;④ 素材 near / far 分类需**人工确认**。
3. **天气预设 XML 往返**:面板改 → 保存 → 文件跟着变;**读**方向(手改 XML → 重进场景 / 换预设 → 面板跟着变)也要试;「另存为新配置」要能新建并出现在下拉里;并回归云的「另存为 / 加载」不受影响。 失败是**静默**的(回落默认全关,不报错)。
4. **打包 `Volken.sr2-mod` + 核对资产清单**:权威来源 `Assets/ModData.asset` 的 `_otherAssets`(GUID),生成物 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`;应含 6 个 shader/compute(`Clouds` / `CloudNoiseCompute` / `RainParticles.{compute,shader}` / `LightningBolt` / `LightningFlash`)+ 9 条 wav,**不含**任何 rain / fog / 旧 ogg 路径;进游戏用 `volkenAssets` 复核。
    **移动 / 删除资产不会自动更新 GUID**(2026-10-01 实测:shader 挪进 `Shader/` 后留了 2 条悬空 + 1 条已删 ogg 残留 → **资产静默不进 bundle,云会整体消失,但没有编译错误**;已修,现 26 条 0 悬空)。自检(输出非空即有悬空):

```powershell
$map=@{}; gci -Recurse -File Assets -Filter *.meta | %{ $l=(Select-String $_.FullName -Pattern '^guid: '|select -First 1); if($l){$map[$l.Line.Replace('guid: ','').Trim()]=1} }
(Select-String Assets\ModData.asset -Pattern 'guid: ([0-9a-f]{32})').Matches | %{ $g=$_.Groups[1].Value; if(-not $map[$g]){"悬空 $g"} }
```

**低优先度**

- **代码注释残留「阶段 N」与日期(2026-10-02 审计)** —— 规则(§五.10)禁止注释里出现迭代编号与日期,但雨相关代码仍违规:**19 处「阶段 N」**(`RainParticles.compute` 13、`.shader` 6)+ **13 处日期**(8 个文件);`.shader` / `.compute` 注释占比 **18.8%**(目标 ≤12%,`.cs` 7.6% 正常)。
  **处置** =「删沿革、留不变量」—— shader / compute 头部的**构轴铁律、坐标系铁律、compute 原子加约束必须保留**,只删轮次编号 / 日期 / "曾用 X 已废弃"的叙述,需要留的沿革改成一行 `docs/` 指针。**验收** = [`tools/strip-code-comments.ps1`](../tools/strip-code-comments.ps1) 退出码 0。
- **星环渲染顺序 / Craft 高轨道云 scale** —— 见 §四之二 #1 / #2。
- **雨 / 雷高开销语句(C# 侧已改,GPU 侧未排期)** —— 剩余项 / 改法见[性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) §3,真机回归判据见其 §4。
- **剩余高开销点(分组清单 + D 各方案代价)** —— [hotpath 清单](proposals/hotpath-optimization-backlog-2026-10-02.md):A 拖 UI 尖峰(已决定不改)/ B 每帧 CPU / C 一次性尖峰 / D GPU(D1 光样本数、D4 点光 range 各 1 行;D3 分叉合批半天;D2/D5 不建议)。
- **雷电"固定位置射灯"扇形(根因已定位,待真机验收)** —— 根因 = 分叉 `LineRenderer` 的**第 0 个点从未赋值**:`positionCount` 的默认 `(0,0,0)` 在 `useWorldSpace` 下就是**世界原点**,而浮动原点又把原点重定位到飞船处 → `(arcs-3)×(splits+1)`(默认 **85**)条分叉全部从**观测者**身上扇形射出。已修 `SetPosition(0, from)`;同轮补**暂停收光** + **重定位防护**(两处真实缺陷)。见[复核](proposals/lightning-fixed-position-diagnosis-verify-2026-10-02.md) §5。
- **死代码待清理(只登记未删)**:`Clouds/DepthCapture.cs`(全工程零调用)、`PlanetRing/PlanetRingsZWriteFix.cs` + `HarmonyPatches/PlanetRingsShaderPatch.cs`(`Postfix` 首行 `return`、`Apply` 在 `Mod.cs` 里被注释)—— 见[场景门控](monobehaviour-scene-gate-2026-10-02.md) §3。
- **二期可选项(未排期)**:落雷改用 `Physics.Raycast` 贴地形(现为行星正球面近似);联机行为核对;云层联动(字段占位仍在,但**面板分组 2026-10-01 已删** —— 要做必须是"可明确关掉且默认关"的显式选项,并走事件通道,见 [解耦](weather-cloud-decoupling-2026-10-01.md) §3.6)。

**已修复台账**(详细根因与验收见各主题文档,此处只留一行摘要)

| 日期 | 问题 | 根因(一句) | 详见 |
|---|---|---|---|
| 2026-10-02 | 雨视觉开关异常:换场景后**再也打不开**(雨声照旧) | 资产就绪标志 `AssetsReady` 误用 `static`(资产是实例字段)→ 新场景的新实例跳过 `EnsureAssets` 永久 NOT READY;叠加解耦后**无任何重挂点** | [雨开关](rain-toggle-scene-switch-2026-10-02.md) |
| 2026-10-02 | 主菜单 / 设计器 / 行星工坊里也在跑 Volken 的每帧回调 | 常驻(`DontDestroyOnLoad`)组件没按场景门控;另有一条 `yield return null` 等菜单的协程跨场景空转 | [场景门控](monobehaviour-scene-gate-2026-10-02.md) |
| 2026-10-01 | 切换至有大气星球时 config 卡住 | `CloudConfig.enabled` 被当"环境开关"在**三处**回写,而它是玩家预设里会落盘的字段 → 行星预设被静默写成"关闭" | [解耦](weather-cloud-decoupling-2026-10-01.md) §8 |
| 2026-09-28 | 闪电偶发**紫红色** | 运行时**材质被销毁**(分叉共享主干材质 + 主干 `Destroy`);顺带修掉"双 Pass 无 `LightMode`"的真 bug | [雷声](proposals/thunder-realism-2026-09-28.md) §8 |
| 2026-09-28 | 闪电**不消失、永久残留** | 协程在 inactive 时**静默不启动** + `Update` 首行早退 → 自毁链成单点故障 | [雷声](proposals/thunder-realism-2026-09-28.md) §7 |
| 2026-08-27 | TSS 边缘拖影(网格 2 快速拖动) | 运动自适应调优:阈值 120→200、无云分支 0.75→0.85 | — |
| 2026-08-27 | 坐标原点重置时 TSS 云偏移 | 浮动原点重置使世界坐标整体平移 → `prevViewProjMat` 失效;订阅 `ReferenceFrameRecentered` 清历史 + 冷启动 | [方案 C](archive/ksa-temporal-upscale-port-2026-08-24.md) §12 |
| — | JNO 联机 mod 冲突 | JNO `OnSceneLoaded` 对 null `inspectorPanel` 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过 | [archive](archive/jno-sceneloaded-nre-2026-08-27.md) |
| — | 水面覆盖云层 / 无大气 SOI 时 config 未同步 | 与「config 卡住」同源(环境开关与用户开关混用) | §四之二 #1 |

---

## 五、文档写入规则(维护约定)

> 适用 `docs/**` 下的 `.md`。**工具脚本在仓库根 `tools/`**(不在 `docs/` 内 —— `docs/` 只放文档)。仿 JNO 联机 mod `plans/` 约定适配。

### 0. 命名与位置
- 主题文档:`<topic>-YYYY-MM-DD.md` —— **英文 kebab-case + 日期**;**禁中文名 / 禁无日期名**。
- 不加日期的只有索引与参考:`README.md`、`AGENT_CONTEXT.md`;`docs/` 里不放脚本(工具在仓库根 `tools/`)。
- 文档正文用中文;**单一主题一个文件**。

### 1. 状态与单一事实源
- **文档头部 blockquote 的「状态:」是唯一事实源**;README 索引行、决策速查、AGENT_CONTEXT 里的进度都只是镜像。
- 受控词表(禁自造):📋 规划 / 评估中 → 🚧 实施中 / ⏸ 暂停 → ✅ 已实现 / 已归档。同一主题同一时刻只有一个状态。
- 改状态时**同一次改动**内同步:索引行(含分区移动)+ 决策速查 + 待定段 + AGENT_CONTEXT。

### 2. 结构、决策记录与"不复制正文"
- 头部:`状态:` / `日期:` / `关联:` / 一句话定位。正文顺序:动机 → 现状 → 方案·决策 → 实施记录(按日期)→ 剩余项·回归判据;结论先行。
- 决策写「【决策:YYYY-MM-DD】」并汇总进「决策速查」(出处 = 链接 + 小节号);翻案保留旧记录并链到新决策。
- **README 只写一句话 + 链接,不复制正文**:实施记录、方案论证、参数表一律只存在于主题文档(避免两处漂移)。

### 3. 交叉链接
- 同目录内用裸文件名;根 → `archive/x.md`、`proposals/x.md`;`archive/` → `../x.md`、`../proposals/x.md`;`proposals/` → `../x.md`、`../archive/x.md`。
- **移动文档后必须全仓库 grep 修链接** + 跑一次死链校验;站内不写本机绝对路径(令牌化,见 §9)。
- **指向文档内部时用「链接 + 小节名」,不要用行号**(行号会随精简 / 追加失效);只有指向**源码**才用 `文件:行号`。
- 引用姊妹文档的章节时写清归属(如「母计划 §10.5」):**不同文档的 `§10.x` 编号会撞车**,精简 / 重编号后必须回查所有引用。

### 4. 三区流转
1. 研究完成未拍板 / 暂停 → `git mv` 进 `proposals/`(标 📋 或 ⏸ + 注明原状态),README 从 §一 移到 §二;
2. 拍板动手 → 移回根目录,进 §一,状态改 🚧;
3. 完成 → 头部改「✅ 已归档(原状态:…)」,文档内写明未排期剩余项 → `git mv` 进 `archive/` → README 移到 §三 + 同步决策速查出处列;
4. 每一步都按 §3 修链接。**归档 / 提案 ≠ 删除**,仍被索引引用。

### 5. 已知问题
- 已核实未修 → 登记 §四之二(问题 / 证据 / 影响;证据给 `文件:行号` 或反编译出处);修复后标「✅ 已解决」并**保留该行**,不整行删除。

### 6. 与代码同改、同提交
- 文档与对应代码改动放同一次提交;禁止"改了一半不提交"。**注释改动属于代码改动**(见 §10)。
- 涉及游戏内文案、命令、路径时,同步核对 [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md) 的关键路径表。

### 7. 编码与校验
- 一律 **UTF-8 无 BOM + LF**。
- **不要用会加 BOM 或替换非 UTF-8 字节的工具**:尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8`(加 BOM)与 `-replace`(处理含 `[` / 反引号的 Markdown 链接会吃字符)。
- 改完自检(三者任一为 True 即有问题):

```powershell
$p = '<文件路径>'
$b = [IO.File]::ReadAllBytes($p); $t = [Text.Encoding]::UTF8.GetString($b)
$bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
"BOM=$bom CRLF=$($t.Contains("`r`n")) U+FFFD=$($t.Contains([char]0xFFFD))"
```

### 8. 改后自检清单
- [ ] **已跑 [`tools/check-docs.ps1`](../tools/check-docs.ps1) 且 9 项全过**(退出码 0)—— 它覆盖下面第 1~3、7 项与重复文件、孤儿表格、残留临时文件
- [ ] 站内 `](...)` 链接全可解析(含 `#锚点`、`.ps1` 等非 md 目标)
- [ ] 无 BOM / 无 CRLF / 无 U+FFFD
- [ ] 头部「状态:」与 README 索引行一致(§一/§二/§三 分区正确)
- [ ] 新决策已进「决策速查」(出处带链接)
- [ ] 归档 / 提案文档已移出 §一,且全仓库无指向旧位置的链接
- [ ] 改名 / 合并小节后,**旧路径已消失**(`git status` 显示 `R` 而不是 `A` + 遗留),旧编号已全仓迁移(§11 #1 / #4)
- [ ] 代码 / 文案改动已与文档同批提交
- [ ] 无本机绝对路径 / 用户名 / IP(令牌化,见 §9)
- [ ] 新增注释不含「阶段 N」/ 日期 / 踩坑史(§10);只动注释的改动已用脚本自证
- [ ] 代码里的 `TODO` 已登记 §四之三(待办清单)

### 9. 隐私红线(公开仓库强制)
- **本机绝对路径 / 用户名 / IP 一律不进被跟踪文档**;真实值只存在于本机被 `.gitignore` 排除的 [`LOCAL_PATHS.md`](LOCAL_PATHS.md)(**严禁提交**)。

| 令牌 | 含义 |
|---|---|
| `<PROJECT>` | 本工程目录 |
| `<USERPROFILE>` | 本机用户目录(日志用) |
| `<JNO_CODE>` | 反编译游戏源码根(只读) |
| `<JNO_D2>` | 解包工程(Unity 2022.3,水体分析用) |
| `<JNO_MP>` | JNO 联机 mod 工程 |
| `<VOLRE_REF>` / `<KSA_REF>` | VolRe(KSP EVE)/ KSA 体积云参考源码 |
| `<SP2_D4>` / `<SP2_CODE>` / `<SP2_GAME>` | SimplePlanes 2 解包工程 / 反编译源码 / 安装目录 |
| `<RVM_EA>` | RaymarchedVolumetrics 早期预览版(EVE 降水配置参照) |

- 引用反编译源码保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),不写本机路径形式的链接。

### 10. 代码注释(规范条文)

> 适用**手写代码** `.cs` / `.shader` / `.compute`(Unity 生成的 `.meta`、第三方源码不动)。
> [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md) §5.7 是改代码时的**操作摘要**,条文以本节为准。
> **唯一判据**:删掉这条注释,**下一个来改这里的人会不会犯错 / 多花时间?** 不会 → 删掉。

- **必须写**:类 / 成员的职责;**不变量与契约**(例:compute 原子加必须用带下标的 `RWStructuredBuffer<uint>` 2/3 参形式、`[ImageEffectOpaque]` 不能删、`CloudLayer.EnvironmentSuppressed` 不能回写成 `config.enabled`);单位 / 取值范围 / 默认值(压成行尾);失败降级语义;**反例**(「不要改成 X —— 会 Y」,这是防再犯,不是历史)。
- **禁止写进代码**:迭代编号(`阶段 N` / `第 N 轮`)、日期、踩坑史与复盘、方案对比与论证、SP2 / KSA / VolRe 对照、「曾经…后来改成…」的演变叙述、调试流水、打包清单。
- **边界**:「这里是什么 / 不能怎样」= 留;「它怎么变成这样」= 删。判据:**两年后改这里的人需不需要知道?** 删掉大段历史时留一行指针 `// 沿革与踩坑见 docs/<文档>.md §X`(先确认路径存在)。
- **体量**:类注释 ≤ 3 行;方法 1 行(非显然的坑 ≤ 3 行);`.shader` / `.compute` 头部不适用 3 行限制(不变量集中),但只放不变量与绑定语义。占比:`.cs` **5~8% 健康、>15% 算「在写文档」**;shader / compute **≤12%**。
- **三条硬约束**:① 清理注释**不得改动任何代码(含字符串字面量)**,改完必须自证;② **过期 / 与实现矛盾的注释 = 缺陷**,发现即修;③ 代码里的 `TODO` 必须登记 §四之三(待办清单)。

**自证脚本**:[`tools/strip-code-comments.ps1`](../tools/strip-code-comments.ps1) —— 退出码 0 = 代码逐行未变(只动了注释)/ 1 = 代码变了 / 2 = 用法或 IO 错。

```powershell
$script = 'tools/strip-code-comments.ps1'   # PS 5.1: powershell -NoProfile -ExecutionPolicy Bypass -File $script ...;PS 7 换成 pwsh
powershell -NoProfile -ExecutionPolicy Bypass -File $script -Old <改动前快照> -New <改动后>
```

- **基线(2026-10-02 实测)**:`.cs` **7.6%**(756 / 10013,健康);`.shader` + `.compute` **18.8%**(341 / 1818,**超标待清理**)。
- **已知残留**:注释里仍有 **19 处「阶段 N」**(13 在 `RainParticles.compute`、6 在 `RainParticles.shader`)+ **13 处日期** —— 已登记 §四之三;**新写的注释不得再带这两类标记**。

### 11. 反模式与预防(2026-10-02 那次"精简"的沉淀)

> 下表每一条都对应本仓库**真实发生过**的事故;前 9 类已可由脚本机械查出:
>
> ```powershell
> powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs   # 0 = 全过
> ```

| # | 反模式(都真的发生过) | 规则 |
|---|---|---|
| 1 | 改名后新旧文件**并存**:中文名雨文档与英文名雨文档同时在,内容 99% 相同 | 改名 / 移动后**立即复核旧路径已消失**(`git status` 应为 `R`,不是 `A` + 遗留);**同一主题只允许一个文件** |
| 2 | 索引里**抄正文**:README 决策速查粘贴主题文档整段;AGENT_CONTEXT 出现 2986~6598 字符的单行"进度流水账";原 `to-do.md` 每条 5~8 行(已并入 §四之三) | **索引只写一句话 + 链接**;禁止把逐轮进度 / 实施记录写进索引(只属于主题文档)。**硬预算:单行 ≤400 字符;README ≤300 行(它同时承载待办台账)、AGENT_CONTEXT ≤200 行** |
| 3 | **孤儿表格**:README 里出现过没有表头的 4 行 `| … |` | 表格必须有**表头 + 分隔行**;删行 / 搬段时连表头一起检查 |
| 4 | **重编号后引用失效**:母计划 §10.2b~§10.2h 合并后需迁移 91 处引用,其中**省略式写法**(`§10.2b/10.2c0`、`/10.2b`)与**错节名**(`§10.2c X` 实指 `§10.2d`)最易漏;**行号式引用**(如 `to-do.md L11-14`,该文件已并入 §四之三)一改就废 | 合并 / 重编号小节**必须**在文中留「旧 → 新」对照表并全仓 grep 迁移;**引用必须写全 `§` + 章节名**,禁止省略式;**禁止用行号引用文档内容**(只用 `文件:行号` 指源码) |
| 5 | **外部事实未实测就抄进多处**:4 份文档写 `%TEMP%\volken-rain-fog-stash` "可整体取回",实测该目录**不存在** | 涉及**本机临时文件 / 外部路径 / 打包产物是否存在**的陈述,写前必须实测(`Test-Path` / `git status`)并**带日期**;**同一事实只写一处**,其余链接过去 |
| 6 | **"命令没报错"被当成成功**:`git rm` 因 `.git` 不可写而失败,脚本却打印了 "removed.";`Remove-Item` 同样被拒 | 任何**删除 / 移动 / 批量改写**之后必须**复核结果本身**(文件真的没了?内容真的是预期?),不能只看命令没报错;脚本要检查 `$LASTEXITCODE` 并统计失败数 |
| 7 | **恢复文件后行尾变了**:`git checkout` 把 CRLF 带回来(8279 → 8485 B),差点当"内容一致"放过 | 恢复 / 重写后**重跑编码校验**;比对**字节数或哈希**,不要只靠"能打开" |
| 8 | **委派大产物丢失**:子代理回"全文已在回复中输出",实际只收到摘要;另一次把同一个标记 ㊿ 拆成三段;**采纳压缩产物时掉了 3 个标识符与 1 条隐式契约,标题却一个不少** | 让子代理**把大段产物写成文件**,回复只给 `行数 / 字节数`;任务必须给**可机械校验**的验收条件(标题集合、标记集合、行数上限),并**事后逐条核对**。**只比标题集合不够 —— 必须再比"事实集合"(数字 / `文件:行号` / 标识符):标题在而事实掉是最隐蔽的丢失**(脚本见下) |
| 9 | **"精简"没有可验证目标**:要求"压到 45~55%",结果只降 1.7% 且往返数轮 | 压缩 / 改写前先**定机器可查的验收**(行 / 字节上限 + 不变的标题、标记清单);**先测出可压空间再动手** —— 若字节主要由数字与 `文件:行号` 构成,就该明白"再压 = 删事实",到此为止 |

**委派压缩 / 改写后的验收脚本(可复制;`<=` 行 = 旧有新无)**

```powershell
function Facts($p) { $t = [IO.File]::ReadAllText($p)
  @([regex]::Matches($t, '[A-Za-z][\w/\.]*\.(cs|shader|compute|asset|xml)') | % { $_.Value }
    [regex]::Matches($t, '`[A-Za-z_][\w\.]{2,}`') | % { $_.Value }) | Sort-Object -Unique }
Compare-Object (Facts '旧版.md') (Facts '新版.md') | ? SideIndicator -eq '<='   # 期望只剩噪声词(如 grep / null)或已被缩写合并的同一引用
```

> 判读:真正要盯的是**标识符**与**`文件:行号`**;纯英文虚词(`grep` / `null`)、以及"同一句里文件名只写一次、后续用 `:123` 缩写"的引用会误报 —— 逐条看一眼再定。实测:一次真实压缩产物在标题零丢失的情况下掉了 3 个标识符 + 1 条隐式契约,靠这项才抓出来。

**流程上的三条硬约束**

1. **改完文档先跑 `check-docs.ps1`**,全过再提交(§9 清单已置顶这一项)。
2. **一次改动只做一件事**:重命名、合并章节、删内容不要混在同一次提交,否则出问题无法二分。
3. **规则与脚本同批提交**:脚本是规则的可执行部分 —— 规则改了脚本跟着改,反之亦然。
