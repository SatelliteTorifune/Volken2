# Volken 待办清单(进行中)

> 状态:📋 活跃 backlog(未修复项登记于此;已修复项同步归档索引,详见 [README.md](README.md))
> 维护:新增/修复项请保持本节结构;修复后把条目移到「已修复」并附日期与根因简述。

## 高优先度

### [新功能]SP2天气系统移植(当前范围:**只做雷电**,雨/雾待重做)
> 方案与实施记录详见 [Volken-SP2天气系统移植BIRP计划-2026-09-27.md](Volken-SP2天气系统移植BIRP计划-2026-09-27.md)。
> **进度**:阶段 0 骨架 / 1 闪电已落地;**天气参数按预设名独立存**(预设名与云层**互不干扰**,落在 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`,旧的内联 `<Weather>` 会自动迁移),C# 全量重编 0 错误。
> **⚠️ 2026-09-27 范围缩减**:阶段 2(雨)与阶段 3(雾)的**实现已整体移除**(`Rain.cs` / `RainParticles.compute` /
> `RainParticles.shader` / `FogRenderer.cs` / `HeightFog.shader` / `enviro_rain_1~3.ogg` 已 stash 到
> `%TEMP%\volken-rain-fog-stash`,可整体取回)。原因:雨丝朝向问题多轮未收敛(横向 / 径向爆散 / 随视角漂移)。
> 移除详情与清理到的引用见计划 **§10.2e**;配置合并见 **§10.2g**(§10.2f 是被取代的方案,勿照做);**天气标度路线修正见 §10.2h**(`WeatherTypes` 已删除);**完整复盘(根因 / 铁律 / 诊断方法 / 已证伪思路 / 量化基线)见 [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)** —— **重做雨/雾前必须先读这份复盘**。
> **配置与 UI 现状**:`PlanetConfigList.xml` 里一条 `<PlanetConfig>` = **云预设名**(`CloudConfigName`)+ **天气预设名**(`WeatherConfigName`)—— 两者**独立**,不配对、不联动。参数本体在各自的预设文件里(云 `VolkenConfig/{行星}/{预设}.xml`、天气 `VolkenWeatherConfig/{行星}/{预设}.xml`,天气含 5 个 Section)。面板分组(**标题无序号,已去掉 ①②③**):总体(活,含 天气值上限)/ 配置管理(活:**当前配置 / 保存 / 另存为新配置 / 加载配置(下拉) / 重置**;下拉列表来自**天气自己的目录**,换它不动云)/ 云层联动(**禁用占位**)/ 雨(**禁用占位**,含 触发天气值)/ 雾(**禁用占位**)/ 雷(活,含 触发天气值)。**没有全局天气档位**:各子系统阈值都是自己的配置字段。
> **待办(按优先级)**:
> 1. **验证天气预设的 XML 往返** —— `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`:面板改 → 点「保存当前配置」→ 文件跟着变;**读**方向(手改 XML → 重进场景/换预设 → 面板跟着变)也要试;**「另存为新配置」**要能新建出文件并出现在下拉里;**并且回归云的「另存为 / 加载配置」不受影响**(云与天气的预设已解耦,互不干扰)。⚠️ 失败是**静默**的(回落到默认全关,不报错)。
> 2. **重新打包 `Volken.sr2-mod` 并核对资产清单的两层** —— 权威来源是 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表),生成物是 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`。**删资产不会自动清 GUID**,已手工清掉雨/雾的 6 条并复核 0 悬空;打包后确认 manifest 含 `LightningBolt.shader` + 5 条雷声、**不含**任何 rain/fog 路径。进游戏后用 dev 命令 `volkenAssets` 看资源台账。
> 3. **真机验证雷电**(唯一保留的渲染特性,尚未在游戏内确认):`LightningBolt.shader` 是否被 Unity 编译过、`loaded 5/5 thunder clips`、`volkenBolt` 手动劈雷、`volkenSetWeather` 推高天气值触发雷暴。
> 4. 二期可选项:落雷改用 `Physics.Raycast` 贴地形(现为行星正球面近似)、联机行为核对、`CloudRenderer.LinearSceneDepth` 决定去留、**云层联动机制实现**(字段与面板占位已就位)。
> 5. 雨/雾重做(未排期):**先读复盘** [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)(照 §7「重做起步清单」的前 3 步走),再直接采用 §10.2c2 的 EVE 密度标定(≈0.139 个/m³);**配置字段与面板分组已就位,不用再动配置层**。


### 切换至有大气星球时config卡住
> 未知原因?等待更多复现和log

## 低优先度

### 星环渲染顺序错误
### Craft在高轨道时云层scale错误

## 已修复
### 水面覆盖云层
### 切换至无大气SOI时config未同步
### TSS 边缘拖影(网格2 快速拖动)
> 运动自适应调优:阈值 120→200、无云分支 0.75→0.85,残影降至可接受(轻微但可接受,2026-08-27)。
### 坐标原点重置时 TSS 云偏移
> 根因:SR2 浮动原点重置(RecenterReferenceFrame)使世界坐标两帧间整体平移,prevViewProjMat 失效 → 时序重投影错位。
> 处理:订阅 ModApi IGameView.ReferenceFrameRecentered,清空时序历史 + frameNumber=0 冷启动(CloudRenderer.cs,2026-08-27)。
### JNO 联机 mod 冲突(NRE 中断 SceneLoaded 事件链)
> 根因:JNO 的 MultiPlayerUI.OnSceneLoaded 对 null inspectorPanel 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过(看不到云、自带云开关锁死)。
> 处理:JNO 侧 OnSceneLoaded 加 null 保护(手动应用);Volken 侧自愈曾加后撤除。详见 [archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md)。