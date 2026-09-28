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
> 1. **🔴 雷声素材 + 闪光 shader 打包(阻塞:现在雷声完全不响)** —— 代码已改为读 `volkenThrunder-near-1~4.wav` / `volkenThrunder-far-1~5.wav`(共 9 条,2026-09-28),但这 9 个 WAV **还没有 `.meta`/GUID、没进 `_otherAssets`**,而 `_otherAssets` 里 5 条旧 `enviro_thunder_*.ogg` 的 GUID **已全部悬空**(文件已删)。**另外 2026-09-28 新增了 `Assets/Scripts/Volken/Weather/LightningFlash.shader`(落点闪光,从原双 Pass shader 拆出来的),它也必须进 `_otherAssets`**。必须在 Unity 里:导入 9 个 WAV + 认到新 shader → 逐条设导入设置(`forceToMono` / `CompressedInMemory` / Vorbis / `preloadAudioData`)→ 9 条 WAV GUID + `LightningFlash.shader` 加进 `_otherAssets`、删掉 5 条悬空 → 重建 → `volkenAssets` 复核(详见 [Volken-雷声真实化可行性分析-2026-09-28.md](Volken-雷声真实化可行性分析-2026-09-28.md) §5.3 与 §8.5)。
> 2. **验证天气预设的 XML 往返** —— `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`:面板改 → 点「保存当前配置」→ 文件跟着变;**读**方向(手改 XML → 重进场景/换预设 → 面板跟着变)也要试;**「另存为新配置」**要能新建出文件并出现在下拉里;**并且回归云的「另存为 / 加载配置」不受影响**(云与天气的预设已解耦,互不干扰)。⚠️ 失败是**静默**的(回落到默认全关,不报错)。
> 3. **重新打包 `Volken.sr2-mod` 并核对资产清单的两层** —— 权威来源是 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表),生成物是 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`。**删资产不会自动清 GUID**;打包后确认 manifest 含 `LightningBolt.shader` + **9 条 `volkenThrunder-*.wav`**、**不含**任何 rain/fog/旧 ogg 路径。进游戏后用 dev 命令 `volkenAssets` 看资源台账。
> 4. **真机验证雷电**(唯一保留的渲染特性,尚未在游戏内确认):`LightningBolt.shader` 是否被 Unity 编译过、`loaded thunder clips: near=4/4 far=5/5`、`volkenBolt` 手动劈雷、`volkenSetWeather` 推高天气值触发雷暴。
> 5. **真机试听雷声真实化(2026-09-28 实施)** —— 日志应逐条打印 `thunder [near|far] dist=… path=… c=… delay=… vol=…`;验收判据:① `thunderDistanceAttenuation=0` 时听感回到原版 0.05s;② Droo(340 m/s)与 Tydos(931 m/s)同样落点距离下**延迟差约 2.7 倍**;③ 手动连劈多次雷声**不互相打断**;④ 短素材被归为 far / 长素材被归为 near 的条目(**素材分类需人工确认**,见可行性分析 §3.1)。
> 6. 二期可选项:落雷改用 `Physics.Raycast` 贴地形(现为行星正球面近似)、联机行为核对、`CloudRenderer.LinearSceneDepth` 决定去留、**云层联动机制实现**(字段与面板占位已就位)。
> 7. 雨/雾重做(未排期):**先读复盘** [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)(照 §7「重做起步清单」的前 3 步走),再直接采用 §10.2c2 的 EVE 密度标定(≈0.139 个/m³);**配置字段与面板分组已就位,不用再动配置层**。


### 切换至有大气星球时config卡住
> 未知原因?等待更多复现和log

## 低优先度

### 星环渲染顺序错误
### Craft在高轨道时云层scale错误

## 已修复
### 闪电偶发紫红色(Error shader)(2026-09-28)
> 症状:雷的 shader 有时是紫色的。
> **先排除**:Player.log 里**没有** shader 编译错误,也没有 `bolt shader NOT FOUND` —— 所以不是编译失败、也不是没打进包,而是**运行时材质被销毁**后 Unity 回退 Error shader(淡紫/品红)。
> **根因链**:分叉线**共享**主干材质实例(`sr.material = _boltMat`)→ 主干收尾 `Destroy(_boltMat)` → 分叉的计时用 `realtimeSinceStartup`(`Update` 在被隐藏时不跑,所以分叉能比主干活得久)→ 渲染时材质已销毁。
> **处理**:① 分叉改持自己的材质实例;② 主干 `Finish()` 里连分叉一起销毁(`DestroySplits()`,同时回收材质);③ 分叉计时改 `Time.unscaledTime`。**另修一个真 bug**:两个 Material 共用一个双 Pass shader,而"一个 Material 只用第一个匹配 Pass" + 两 Pass 都无 `LightMode` 标签 → 落点闪光球一直在跑主干画法(`_CoreWidth`/halo 从未生效);现拆为单 Pass 的 `LightningBolt.shader` + 新增 `LightningFlash.shader`。并把 `Fallback Off` 改为 `Fallback "Hidden/Internal-Colored"`(原先 shader 一旦不可用就直接紫红,掩盖真因)。C# 0 错误。
### 闪电不消失、永久残留在天上(2026-09-28)
> 症状:某些情况下生成的闪电不消失,一直存在。
> **根因(两层,叠在一起才致命)**:① 旧实现把 bolt 挂在 `NearCamera` 下,并在**自己身上** `StartCoroutine` 跑主协程与分叉协程;相机一旦 `activeInHierarchy == false`,Unity **静默拒绝启动协程**并只打一行警告 —— Player.log 实证 `Coroutine couldn't be started because the the game object 'VolkenLightningBolt' is inactive!`。② `Update()` 第一行是 `if (!_fadeOut) return;`,而 `_fadeOut = true` 只在主协程**跑完最后一句**时赋值 → 协程没跑完就**没有任何代码路径能销毁这个对象**(Update 在 inactive 时也不运行),相机恢复激活后残留物永远停在全亮状态。
> **处理**:取消对"活着的协程"的全部依赖。动画改由 `Update` 驱动的时间状态机(`Grow`→`Flash`→`Fade`),`Update` 天然只在 active 时跑 → 冻结可见时动画自己暂停、恢复时接着播,不可能出现"协程死了但对象还活着"。bolt 改为**根物体**(不再挂相机下);分叉改由独立的 `SelfDestruct` 组件计时(同样不用协程);加**三条独立销毁路径** —— 正常播完 / 连续不可见超 1.5s 的僵死判定 / 3.5s 硬性寿命兜底;`LightningModule.SetActive(false)` 时调 `LightningBolt.DestroyAll()` 整批清理(离开场景不留雷)。C# 0 错误。
> 详见 `LightningBolt.cs` 类注释与 [`Volken-雷声真实化可行性分析-2026-09-28.md`](Volken-雷声真实化可行性分析-2026-09-28.md) §7。
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