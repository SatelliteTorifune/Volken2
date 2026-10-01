# Volken2 文档索引(docs/)

> 项目:Volken(SimpleRockets 2 / JNO 体积云 mod,Unity BIRP)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 关键文件 / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `docs/` 的导航页。**三区**:根目录 = 活跃(已动手) → [`proposals/`](proposals/) = 已论证 / 待拍板(未在动手) → [`archive/`](archive/) = 已完成 / 历史。
> **当前活跃两件:[`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md)(SP2 雨系统移植)、[`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md)(天气↔云解耦重构,主体已落地、A2/D/F 未排期)**;另有 [`to-do.md`](to-do.md)(待办台账)。其余全部在 [`proposals/`](proposals/)(天气母计划 / 雷声真实化 / 体积云优化路线图 / 水体大修)与 [`archive/`](archive/)。
> 约定:方案/排查/分析单一主题一个文件,写清「状态 + 决策记录」,未拍板移入 `proposals/`、完成移入 `archive/`,并在此更新索引;**完整文档写入规则见 [§五](#五文档写入规则维护约定)**。
> **调试日志路径**:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志)。

---

## 一、当前活跃(已动手)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md) | **SP2 雨系统 `ParticleDomain` 移植难点 + 分步计划** | 🚧 **实施中(唯一在动手的方案)** | 雨重做的执行细案 + **动手前必读**。**4 条真难点**:①SP2 的 `AlignStreaks` 是**世界空间长度轴**(正是本项目已判死刑的路线),会以**同源症状**复发(上次是"横块",SP2 在 `fwd∥视线` 时同样"横块"、另多一种"细线")→ 必须改用屏幕平面构轴;②URP 遮挡专有物在 BIRP 不存在 → 方案 A(无遮挡起步)/B(二期),并带**空深度图会让雨整体消失且无报错**的护栏;③**6 个 kernel 的 HLSL 源码全安装不存在**(只有 DXBC),且 **sp2d4 是 Unity 6000.2 而本工程 2022.3** → **必须手写 `.compute`**;④数量级必须重标定(域半径随相机速度自适应 + 密度补偿 r^2.5)。**8 阶段**各带准出判据与停止条件;**§10 为实施记录**(详见文档) |
| [`to-do.md`](to-do.md) | **待办清单**(高/低优先度 + 已修复台账) | 📋 活跃 backlog | config 卡住(**已修,见 §四之二 #1**)、星环渲染顺序/Craft 高轨道云 scale(低);已修复项附根因简述(水面覆盖云、TSS 拖影、原点重置偏移、JNO 冲突、闪电残留/紫红) |
| [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md) | **天气 ↔ 云 解耦审计与重构** | 🚧 部分落地(2026-10-01:改名 + 拆生命周期 + `Weather/` 分子目录;**抽象层已按用户要求回撤**;A2 / D / F 未排期) | **反直觉结论:云对天气是零依赖**(`Clouds/` grep `weather` 零命中),问题在**依赖方向反了** —— 天气经 `VolkenMod.Instance.MainLayer.config.layerHeights` 四级穿透进云的内部对象图,且该契约被 `VolkenWeather`/`LightningModule` **复制两份无人拥有**(C1/C2);另有相机海拔公式三份(C4)、共享可变清单双写者(C5)、两份 `SceneLoaded` 编排器 + UI 里第四份(C8)、**该耦的没耦**:`IsRaining` 无消费者(C9)。**已落地**:`VolkenMod`→`Clouds/VolkenClouds`、`VolkenWeatherConfig`→`Weather/VolkenWeatherSettings`、**行星生命周期并入 `VolkenClouds`**(删掉 UI 里 ~50 行重复行星逻辑)、`Weather/` 按 Rain/Lightning 分子目录、`CloudConfig.TryGetBand` 成为云层带的唯一实现;顺带修掉 README §四之二 #1(`config.enabled` 被当环境开关回写 → 改 `CloudLayer.EnvironmentSuppressed`)。**已回撤**:`SceneOrchestrator`/`ICloudBandSource`/`CloudBandRegistry`/`CloudBand`/`SceneDepthRegistry` —— 判定为过度抽象,**两边需要对方数据时直接读对方的 config / 直接 `GetComponent`**,行星解析由 `VolkenClouds` 自己承担。**剩余**:A2(相机海拔 3 份)、D(清单双写者)、F(天气值→降水通道,需玩家提出)、Unity 侧真机验收。**分支现状(`origin/main`=`7690fd7`)**:main 上**完全没有天气系统**,全部天气代码只在 dev 的未合并提交里 |

---

## 二、提案 · 待拍板(`proposals/`,未在动手)

> 已论证 / 已评估,但**当前不排期**;要动手时按 §五「三区流转」第 2 条移回根目录。

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md) | **SP2 天气系统移植(原生 BIRP)母计划** | ⏸ 暂停 / 转参考(原状态:🚧 实施中,范围缩减为只做雷电) | 骨架(天气状态机)+ 雷电 + **天气参数按预设名独立存**(与云预设互不干扰,`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`)+ 五分组面板已落地;**雨/雾实现已整体移除**。**仍是最有价值的决策沿革与素材/API 底账**:§10.2/10.2b/10.2c0/10.2c2/10.2d 记录 30+ 处实测更正、§10.2e 移除记录、§10.2f 勿照做、§10.2g 配置合并、§10.2h 天气标度路线修正。活跃部分已拆出 → 雨见 §一、雷声收尾见本表下一行 |
| [`proposals/thunder-realism-2026-09-28.md`](proposals/thunder-realism-2026-09-28.md) | **雷声真实化**(雷击↔craft 距离 → 按声速定延迟 → 阈值选 near/far)+ **闪电残留/紫红修复** | ⏸ 暂停(**代码已落地,C# 0 错误;Unity 侧打包未做 → 雷声仍静音**) | 三步都可行,游戏 API 直接提供 `AtmosphereSample.SpeedOfSound`(实测 Droo 340 / Cylero 233 / Tydos 931 m/s,硬编码 343 在 Tydos 上差 2.7 倍)。**修掉的坑**:① `OnBoltLanded` 回传的是"bolt 自身长度"而非到玩家距离;② 距离衰减算两次;③ 单 AudioSource 互相打断;④ 远雷 `spread` 60°→160°;⑤ 水平/垂直分解须沿地表法线。**§7** 闪电永久残留(协程在 inactive 静默失败)→ `Update` 时间状态机 + 三条销毁路径; **§8** 紫红 = 运行时材质被销毁 → 拆 `LightningBolt.shader` + `LightningFlash.shader`。**收尾 = 打包清单**(9 个 WAV + 新 shader 的 GUID 进 `_otherAssets`、删 5 条悬空) |
| [`proposals/cloud-optimization-roadmap-2026-08-28.md`](proposals/cloud-optimization-roadmap-2026-08-28.md) | **体积云优化路线图**(借鉴 VolRe/KSA) | 📋 分析完成,未排期(T0 已部分被方案 C/轨道云消化) | 13 项优化点按收益排名:T0 光照解耦(50→6 样本)/距离淡出;**Light Volume、PlaceRays 为长期项** |
| [`proposals/water-system-overhaul-2026-09-02.md`](proposals/water-system-overhaul-2026-09-02.md) | **水体大修可行性**(路线 A~E) | 📋 评估完成,未排期 | 建议先 A/B 零风险调参(运行时改参/水下观感),E 整换 shader 为数周级终局;反射云(方案 A)已落地 |

---

## 三、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/stock-density-scale-2026-09-06.md`](archive/stock-density-scale-2026-09-06.md) | **方案 A:自带云区域内密度缩放** | ✅ 已实现归档(2026-09-06) | `stockDensityScale`(默认 1)只压区域内附加密度地板、`dist` 保足迹边缘;`scale<1` 把实心云拆成蓬松结构;翻案自阈值重映射(见 §9 历史) |
| [`archive/stock-cloud-distribution-2026-08-23.md`](archive/stock-cloud-distribution-2026-08-23.md) | **方案 B:游戏自带云 cubemap 作全球分布形状** | ✅ 已实现归档(2026-08-23) | `useStockCloudMap` 把 Clouds cubemap 接入 layers/shape 两处分布源;`stockMapStrength=0` 逐字节回退;缺层/无云星球逐带回退 |
| [`archive/ksa-temporal-upscale-port-2026-08-24.md`](archive/ksa-temporal-upscale-port-2026-08-24.md) | **方案 C:KSA 体积云技术移植(BIRP,时序超采样核心)** | ✅ 已实现归档(2026-08-24,2026-08-27 收工) | 低清全量 raymarch + 全清时序上采样 + 运动自适应;重投影 Y 镜像已修;坐标原点重置已修;JNO 冲突已定位 |
| [`archive/coverage-decomposition-2026-08-27.md`](archive/coverage-decomposition-2026-08-27.md) | **覆盖分解:biome × 旋转分布 × tiled detail** | ✅ 已实现归档(2026-08-27) | `coverage = cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 = 逐字节一致 |
| [`archive/orbit-clouds-crossfade-2026-08-27.md`](archive/orbit-clouds-crossfade-2026-08-27.md) | **轨道 2D 云 + 过渡带交叉淡入** | ✅ 已实现归档(M0~M3,2026-08-27) | `OrbitClouds` 壳求交 + 同源密度采样,`orbitFade` 交叉淡入;海拔分派:低空体积云 / 高空 2D 云;夜间晨昏线已修 |
| [`archive/seamline-reprojection-2026-08-25.md`](archive/seamline-reprojection-2026-08-25.md) | **割裂线排查(TSS 关 + 运动残影)** | ✅ 已修复归档(2026-08-25) | 根因 = 重投影矩阵用逻辑投影,与射线重建 clip 约定差 Y 翻转 → 历史镜像采样;修复:`GL.GetGPUProjectionMatrix` |
| [`archive/jno-sceneloaded-nre-2026-08-27.md`](archive/jno-sceneloaded-nre-2026-08-27.md) | **JNO 联机 mod 冲突(NRE 中断 SceneLoaded 事件链)** | ✅ 已定位并修复归档(2026-08-27) | JNO `MultiPlayerUI.OnSceneLoaded` 对 null `inspectorPanel` 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用) |
| [`archive/reflection-adaptation-2026-09-02.md`](archive/reflection-adaptation-2026-09-02.md) | **实时反射适配分析**(水面平面反射 / 机体探头) | ✅ 分析完成,方案 A 已落地归档(2026-09-02) | 云只渲染在主相机 OnRenderImage;方案 A(水面反射合入云)已落地,方案 B(机体 cubemap 探头)后置未做 |
| [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) | **雨/雾移植失败复盘(根因 + 铁律 + 诊断方法)** | ✅ 已归档(原状态:移植失败并移除) | 直接原因 = **雨丝长度轴表达错了空间**(世界空间长度轴 + 任意宽度轴 → 视角相关的"横块");真正原因 = **诊断量错对象**(用世界空间夹角当屏幕倾角)。**4 层根因 / 27 条铁律 / 8 条已证伪思路 / 量化基线**;重做雨/雾**前必读** |

> ✅ 归档文档头部「状态:」为最终结论;文档内勾选项标记实际落地情况,未勾选项 = 待复跑/未排期项,按需复跑,勿当作当前待办执行。

---

## 四、决策速查(最新决策)

| 决策 | 结论 | 出处 |
|---|---|---|
| 方案编号体系 | 方案 A/B/C 同一套:方案 B = 自带云分布接入;方案 A = 自带云区域内密度缩放(前置 B);方案 C = KSA 时序超采样移植 | 各方案文档 |
| 自带云分布接入 | **✅ 方案 B 已实现**:游戏 Clouds cubemap 作全球分布形状,`stockMapStrength=0` 纯回退;缺层逐带回退 | [archive/stock-cloud-distribution-2026-08-23.md](archive/stock-cloud-distribution-2026-08-23.md) |
| 自带云区域内密度 | **✅ 方案 A 已实现**:`stockDensityScale` 区域内密度缩放(默认 1 恒等);**翻案说明**:初版阈值重映射 `stockCoverage/stockSoft` 会收缩足迹边缘,弃用改为密度缩放(§9 历史,勿复用) | [archive/stock-density-scale-2026-09-06.md](archive/stock-density-scale-2026-09-06.md) |
| 时序超采样 | **✅ 方案 C 已实现**:低清每帧全量 raymarch + 全清时序上采样(Upscale),运动自适应 `tssBlend`;单 `historyTex`(无 flip/flop),TSS 关走运动残影路径 | [archive/ksa-temporal-upscale-port-2026-08-24.md](archive/ksa-temporal-upscale-port-2026-08-24.md) |
| 重投影修复 | **✅ 已修复**:`prevViewProjMat` 用 `GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)` 与射线重建 clip 约定对齐(修 fresh + 时序两路径) | [archive/seamline-reprojection-2026-08-25.md](archive/seamline-reprojection-2026-08-25.md) §4.5 |
| 坐标原点重置 | **✅ 已修复**:订阅 `IGameView.ReferenceFrameRecentered`,清空时序历史 + `frameNumber=0` 冷启动 | [archive/ksa-temporal-upscale-port-2026-08-24.md](archive/ksa-temporal-upscale-port-2026-08-24.md) §12 |
| 覆盖分解 | **✅ 已实现**:覆盖 = `cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 | [archive/coverage-decomposition-2026-08-27.md](archive/coverage-decomposition-2026-08-27.md) |
| 轨道云 | **✅ 已实现**:海拔分派(低空体积云 / 高空 2D 壳着色)+ `orbitFade` 过渡带交叉淡入;2D 与体积云**同源密度采样**避免云形突变;夜晚侧晨昏线门控已修 | [archive/orbit-clouds-crossfade-2026-08-27.md](archive/orbit-clouds-crossfade-2026-08-27.md) |
| 实时反射 | **✅ 方案 A(水面反射合入云)已落地**,方案 B(机体 cubemap 探头)后置;反射场景关 TSS、粗步长、低光样本 | [archive/reflection-adaptation-2026-09-02.md](archive/reflection-adaptation-2026-09-02.md) §4 |
| JNO 冲突 | **✅ 根因定位**:JNO `OnSceneLoaded` NRE 中断事件链 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用);Volken 侧自愈曾加后撤除,回纯事件驱动 | [archive/jno-sceneloaded-nre-2026-08-27.md](archive/jno-sceneloaded-nre-2026-08-27.md) |
| **跨界移植方法论** | **⚠️ 通用铁律(雨/雾移植失败的沉淀)**:①细长 billboard 的**长度轴必须在屏幕平面内表达**,长度轴只由物理量决定、宽度轴由视线决定;②诊断必须量**你关心的那个量所在的轴**(屏幕问题量屏幕空间),并输出**当前值 + 内部状态**;③**每帧路径里禁止重置动画进度**,守卫只能读状态、只能比较目标值;④先标定数量级(密度 / 域半径 / 停留时长)再调观感;⑤同一问题连续 3 轮无可信改善信号 → 换方案,不许原地修 | [archive/weather-rain-fog-postmortem-2026-09-27.md](archive/weather-rain-fog-postmortem-2026-09-27.md) §5 |
| SP2 天气移植 | **⏸ 暂停 / 转参考(母计划)**:范围已缩减为「只做雷电」,雨与雾实现曾整体移除;现**雨按 8 阶段计划重做中**(见下条,唯一活跃项),母计划退为决策沿革与素材底账。原生 BIRP,不引入 Enviro3;天气状态由 mod 持有 | [proposals/sp2-weather-port-2026-09-27.md](proposals/sp2-weather-port-2026-09-27.md) §10.2e |
| 雨重做(8 阶段) | **🚧 阶段 0/1/2 已落地并真机证实,阶段 3 已落地(四轮真机根因 + 一轮自查已修,2026-09-29)**:`RainAxisProbe.cs`(双算法对照)+ `RainParticles.compute/.shader/.cs`(最小 compute 管线:下标式 RWStructuredBuffer 原子加 + RenderMeshIndirect + SV_InstanceID);径向 `GravityNormal`;阶段 3 正式雨滴 shader(SP2 属性名 + 随速度拉伸 + 软粒子);"竖线贴相机"(§10.10)、"一团不动"(§10.11→§10.12 证伪:实为暂停)、"朝向随角速度摆"(§10.12);**第四轮(§10.13)按 `sp2d4` 第一手实锤整体重做**:废弃屏幕平面构轴 → **SP2 世界空间旋转矩阵** `_RotationMatrix`(朝向锁世界系)+ **粒子世界系下落**(去 camVel)+ **`TranslateFixed` 换帧重定位**(ModApi `IGameView.ReferenceFrameRecentered`)+ 去随机滚转角 + UV 亮头拖尾;**第五轮自查(§10.14)**:球内分布改体积均匀 `R*u^(1/3)`(修中心堆积)、加 `_DiagBuffer` 重生计数、加 `_ShaderVer` 部署哨兵与全量诊断日志(SELFCHECK/axis·fwd/dist[64]/time/RECENTER);**第六轮再挖 SP2(§10.15)**:拿到 SP2 雨**出厂参数表**(`R=50`/**`particleAmount=100000`**/`fallSpeed=15`/`streakLength=2.5`/`thickness=0.1`/`stretchAmount=0.045`/`limit=3.5`),**实锤 down 用哪个**(`GravityNormal`=`GravityFrameNormalized` 帧空间 + `ReferenceFrame` yaw-only + SR2 根本没有这套雨 → SR2 必须帧空间径向),并按 SP2 加**域边界淡出**(`volkenRainP2Edge`)+**程序化柔边雨丝贴图**(替代硬边四边形);**第七轮(§10.16)真机全绿 + UI 接入**:`shaderVer=3`/`TranslateFixed=2`/`down·camUp=-0.98`/`·fwd` 随视角变/`dist mean/R≈0.75` 全部达标,并修掉"换帧事件不触发 → 雨留原地 → respawn 2.2~5 万/s"(事件只记录不应用 + `ICraftScript.FramePosition` 跳变兜底判定);**天气面板的雨组已由禁用占位改为实时控制**(启用/实时状态行/立即切换/11 个滑块,含密度、域半径、下落速度、雨丝长宽、拉伸、边界淡出、软粒子、尾淡、亮度、朝向模式),`RainSection` 新增 6 字段(默认=SP2 出厂值),中/英/俄各 11 个新词条。**第八轮(§10.17)高度闸门**:SP2 最大缩放只到半个岛(不涉及太空)、JNO 能缩到整颗星球 → 用**雨自己的配置项**(`ceilingAltitude`,默认 12000m;0=关闭闸门,**不与云层联动**)+ 相机海拔算 `altFade` 乘进 `_FadeAmount`,超上限直接不 dispatch/不绘制(**太空零成本零雨**),面板加「海拔上限(0=自动)」「淡出带宽」;抑制期间同步换帧状态。**第九轮(§10.18)诊断"怪异感"**:主嫌疑 = 出域处置(球内随机 → 粒子在紧贴镜头处爆闪 = 跟随相机的"沸涌");证据 = SP2 的 Positioning `inBuffers: []`+`_Positions` stride 12 → **无每粒子随机数**、只能确定性处置 → 改为**穿过球心镜像重生**(`volkenRainP2Respawn` 可 A/B);次要:`volkenRainP2Soft 0`(软粒子是我加的,SP2 无深度纹理)、`volkenRainP2Edge 0`/拉远看"雨球"。**第十轮(§10.19)**:删除全部 9 条 `volkenRainP2*` 控制台指令(含静态 setter),功能 100% 集成到天气面板「雨」分组(22 项,含「把雨状态写入日志」与「开发:等距排自检」);**约定:以后可调项一律进面板**。**第十二轮(§10.21)修"集中一股脑下降"**:元凶 = §10.18 的**确定性镜像重生**(零混合 → 初始团块周期性一起落下,静止 6.7s/飞行 0.6s 周期)→ 默认改回**随机重生** + 壳层 [0.15R,R] 体积均匀(不紧贴镜头冒出);**第十一轮(§10.20)修"像面条"**:配置默认值对齐 SP2(宽度 0.1/数量 100000/新增拉伸上限 3.5)+ `UpgradeUneditedDefaults()` 只升级"仍是旧默认值"的存档字段(否则老 XML 永远停在旧值)+ 贴图改"满宽软板条 + 纵向快速收尾"。**第十三轮(§10.22)把雨搬进 Unity 编辑器**:新增 `Assets/Scripts/Volken/Debug/RainPreview.cs` 预览台(自动建相机/环境、右键拖拽+WASD 自由飞、IMGUI 面板与游戏内同批字段**即时生效**、标 ◈ 的容量/长丝宽松手才重建、`PlayerPrefs` 一键"下次 Play 自动启动"仅编辑器)+ `Mod.LoadVolkenAsset` **编辑器回退 AssetDatabase** + `RainParticles.DebugAltitudeOverride` → **观感迭代从"打包 5 分钟"变"按 Play 5 秒"**(跑的是同一份 compute/shader/驱动代码);预览测不到软粒子/换帧/真实海拔 → 定稿后仍需打包验收。**第十四轮(§10.23)测试脚本与正式代码解耦**:8 个测试/开发脚本(`RainPreview`/`RainAxisProbe`/噪声可视化/`Profiler`)统一收进 **`Assets/Scripts/VolkenTests/`**,正式代码对它们**零引用**(原 7 处引用改为由 `TestsBootstrap.cs` 自发注册)→ **删掉该文件夹 mod 仍能编译运行**;契约见该文件夹 `README.md`。**下一步:用户 SP2 实机对比 → 阶段 4 密度标定/自适应域半径/水下门控** | [sp2-rain-particledomain-port-2026-09-28.md](sp2-rain-particledomain-port-2026-09-28.md) §5 / §10.9~§10.22 |
| ~~雨/雾实现~~ | **❌ 已移除(2026-09-27)**:`Rain.cs`/`RainParticles.compute(.shader)`/`FogRenderer.cs`/`HeightFog.shader`/`enviro_rain_1~3.ogg` 已 stash 到 `%TEMP%\volken-rain-fog-stash`,可整体取回。移除原因:雨丝朝向多轮未收敛。**重做前必读复盘** | [教训](archive/weather-rain-fog-postmortem-2026-09-27.md) / 同计划 §10.2e |
| 天气系统命名空间 | **✅ `VolkenMod.Weather`**(不能用 `Volken.Weather`:与全局类 `Volken` 冲突 → CS0101);新增天气文件必须沿用 | 计划 §10.2 ① |
| mod 资源加载 | **✅ 统一用 `Mod.LoadVolkenAsset<T>(path, required)`**(内部 `IModResourceLoader.LoadAsset<T>`);`Load<T>`/`LoadAudio` 属游戏 `IResourceLoader`,读不到 mod bundle | 计划 §10.2 ⑦ |
| 雾/雨的深度来源 | **✅ `CloudRenderer.LinearSceneDepth` 已有消费者(2026-09-29)**:雨(阶段 3)软粒子采样它(`combinedDepthTex`,RFloat,LinearEyeDepth 米);雾重做时同样可用。`DepthCapture.cs` 仍是死代码(其 `Hidden/DepthLinear` shader 在工程里不存在) | 计划 §10.2 ② / §10.3 / 雨计划 §10.9 |
| **【决策:2026-10-01】移除"天气值"整套** | **JNO 的天气是整颗行星尺度的,单个连续标量状态机不匹配** —— 删掉 `WeatherValue`/`TargetWeatherValue`/`WeatherName`/`DescribeWeatherValue`/`IsRaining`/`ForceWeatherValue`/`SetTargetWeatherValue`/`PickRandomWeather`/`WeatherValueChanged` 事件与 `Tick` 里的随机/淡变;删掉 `overall` 的 `dynamicWeather`/`fixedWeatherValue`/`initialWeatherValue`/`maxWeatherValue`/`minDuration`/`maxDuration`/`fadeSpeed`/`updateInterval`/`timeScaleFollow`/`foggyDawn`;删掉 `rain.triggerValue`(与面板滑块 + `Volken.UI.RainTriggerValue`)、`lightning.stormValue`(与滑块 + `Volken.UI.LightningStormValue`);**删掉整个 `CloudLinkage` Section**(它按定义是"天气值 → 云"的联动,已无意义)。**触发方式改为:雷电 = `lightning.enabled` 开着就按 `minDelay/maxDelay` 持续落雷;雨 = `rain.enabled` 开着就下。** `LocalSolarHour` / `CameraCloudFade` 保留(不依赖天气值);**黎明起雾挪进 `fog.dawnFog`**(不再是总体天气的一部分)。旧的 weather.xml 里那些节点由 `XmlSerializer` 静默忽略(已验证不会失败) | [weather-cloud-decoupling-2026-10-01.md](weather-cloud-decoupling-2026-10-01.md) §8 |
| **【决策:2026-10-01】天气面板挂检查器内** | **✅ 不另开浮动窗口**(`WeatherPanel.cs`):与云在同一处调才是"一套观感参数",也避免两个面板互相遮挡 | 计划 §10.2b ⑯ |
| **天气设置项** | **✅ Mod 设置里已无天气项** —— 两个「总闸」(`WeatherEnabled` / `ThunderEnabled`)已按用户要求移除,开关交给**逐行星的天气预设**(见下条) | 计划 §10.1 / §10.2e |
| **天气配置序列化** | **✅ 按预设名独立存,与云层同构但互不干扰** —— `<PlanetConfig>` 分别记**云预设名**与**天气预设名**(`CloudConfigName` / `WeatherConfigName`);参数本体在各自文件:`UserData/VolkenConfig/{行星}/{预设}.xml`(云)、`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`(天气)。天气面板可**独立新建 / 保存 / 读取**,换天气预设不动云,反之亦然。类:`Core/PlanetConfig.cs` + `Weather/VolkenWeatherConfig.cs` | 计划 §10.2g ㊹ |
| ~~不要 SP2 的全局天气预设~~ → **连带天气值一起删除** | **✅ 【决策】`WeatherTypes` 早已删除;2026-10-01 进一步删掉"天气值"本身** —— 不映射档位、也不再有连续标度。各子系统只看自己的 `enabled` + 参数 | 计划 §10.2h → 本次取代 |
| ~~档位标签 `DescribeWeatherValue`~~ | **❌ 已随天气值删除** —— 没有标度就没有标签 | 本次 |
| **天气配置结构** | **✅ 按「4 个 Section」组织,XML 节点 = 面板分组 = 数据块,三者同名同序**:`Overall`(总体:仅 `enabled`)/ `Rain`(雨,占位)/ `Fog`(雾,占位,含 `dawnFog`)/ `Lightning`(雷)。每块是嵌套 `[Serializable]` 类 + 自带 `CopyFrom`;`CopyFrom` **逐块委派**而不是浅拷贝(嵌套实例是引用,浅拷贝会让两份配置共享同一个 Section 对象) | 计划 §10.2f ㊵ → 本次缩为 4 块 |
| **占位参数的处理** | **✅ 雨/雾保留"完整占位"**:字段 + XML 节点 + 面板分组都在,但**面板整组禁用** + 灰字说明"已移除待重做"。**为什么禁用而不是隐藏**:结构完整可读,且玩家不会以为功能丢了。**改这些字段不会有任何效果** | 计划 §10.2f ㊶ |
| **`VolkenWeather.Config` 的语义** | **⚠️ 它是清单里那条记录上的实例本身(引用,不是副本)** —— 面板一改就立刻改了清单内存对象,所以「重置为默认」必须显式实现(`CopyFrom(CreateDefault())` 就地写而**不是换引用**,换引用会让 `Config` 与清单脱钩)。落盘只发生在"点保存"或云层 `AddConfig/SetConfig` 时 | 计划 §10.2g ㊼ |
| **`AddConfig` 的隐藏 bug** | **✅ 已修**:原实现无条件 `configList.Add(new PlanetConfig(...))`,对已存在的行星会**追加第二条同行星记录**。云层调用点都被 `ExistsInConfig` 挡着所以没暴露;**天气内联进来之后这会让天气参数分叉成两份**(症状:"设置时不时自己变回去")。改为已有记录只更新层名 | 计划 §10.2g ㊽ |
| **旧 `PlanetConfigList.xml` 兼容** | **✅ 有显式兜底,而且会自愈**:老记录的 `<PlanetConfig … />` 是**自闭合标签**(属性-only)没有 `<Weather>` 节点 → `LoadFromFile` 里补一份默认(全关)+ `EnsureSections` + `ClampAll`(**不依赖"字段初始化器不被反序列化器重置"这个行为细节**)。而且 `Volken.OnSceneLoaded` 会调 `AddConfig` → 结尾 `SaveToFile`,所以**进一次场景文件就被重写成带 `<Weather>` 的形态** | 计划 §10.2g ㊾ |
| ~~天气独立配置目录~~ | **❌ 已废弃(仅存活一次改动)**:曾短暂改为 `UserData/VolkenWeatherConfig/` + `PlanetWeatherConfigList.xml` + 多预设。现**不创建、不读取**;若磁盘上有该目录可手删。**别再照 §10.2f ㊴ 实现** | 计划 §10.2g ㊺㊻ |
| 淡变/重入守恒 | **⚠️ 通用教训(雨已删,结论留用)**:任何"每帧都会调到的路径"里禁止出现重置动画进度的副作用;重入守卫只能比较**目标值**,不能比较当前值 —— 否则淡入每帧被重置,值恒 0。**守卫只能读状态,不能挡在"改状态的逻辑"前面** | [教训](archive/weather-rain-fog-postmortem-2026-09-27.md) §5.2 / 计划 §10.2b ㉑㉖㉚ |
| 天气与云的关系 | **✅ 【决策】天气系统不联动云层** —— 云厚度/覆盖度/浓度/颜色/风速全部由云自己的配置决定;曾有 `cloudCoverageGain` 等三个联动项,已连同 UI/本地化/配置字段一起移除 | 计划 §10.2b ㉔ |
| **【决策:2026-10-01】模块命名与职责** | **`VolkenMod` → `Volken.Clouds.VolkenClouds`**(移入 `Clouds/`;它一直是"云系统",不是 mod 入口 —— 入口是 `Mod.cs`)、**`VolkenWeatherConfig` → `Volken.Weather.VolkenWeatherSettings`**(移入 `Weather/`)、`VolkenWeather` **名字不变**。规则:**命名空间表明域,类名不再制造"这是不是整个 mod"的歧义** | [weather-cloud-decoupling-2026-10-01.md](weather-cloud-decoupling-2026-10-01.md) §8 |
| **【决策:2026-10-01】行星生命周期归属于 `VolkenClouds`** | **行星环境只有 `VolkenClouds` 解析**(它本来就按行星装云预设、挂渲染器),`SceneLoaded` / `PlayerChangedSoi` 由它订阅,解析出 `PlanetEnvironment` 后广播 `PlanetChanged` 给天气。初始化顺序:`VolkenClouds` → `VolkenWeather`。UI 里那份重复的行星解析(~50 行)已删除(UI 仍订阅 `SceneLoaded`,但只为建面板)。另:`CloudRenderer` 原先订阅了 `PlayerChangedSoi` 却从不退订 → 改为订阅 `PlanetChanged` 并在 `OnDestroy` 退订 | 同上 §8 |
| **【决策:2026-10-01】不要额外的编排器类** | 曾抽出的 `Core.SceneOrchestrator`(+ `PlanetEnvironment` 在 Core)**当日撤销** —— 行星解析搬回 `VolkenClouds`,`PlanetEnvironment` 落在 `Volken.Clouds`。少一个类、少一跳;代价是天气与 UI 通过 `VolkenClouds` 拿行星信息(接受) | 同上 §8 |
| **【决策:2026-10-01】云层高度带的所有权** | 遍历逻辑**只剩 `CloudConfig.TryGetBand(out bottom, out top)` 一处**;天气**直接读云的 config**(`VolkenClouds.Instance?.MainLayer?.config?.TryGetBand(...)`),云要天气参数就直接读 `VolkenWeather.Instance.Config`。**读 ≠ 联动**:天气不修改云的任何参数 | 同上 §8 |
| **【决策:2026-10-01】不搞过度抽象** | 撤掉的全部间接层:`ICloudBandSource` + `CloudBandRegistry` + `CloudBand`(云层带接口)、`Core.SceneDepthRegistry`(相机深度注册表)、`Core.SceneOrchestrator`(编排器类)。现在:**两边需要对方数据就直接读对方的 config / 直接 `GetComponent`**;**行星生命周期由 `VolkenClouds` 自己承担**(它本来就需要)。保留的只有两条 —— **实现去重**(一份 `TryGetBand`)、**不重复解析行星**(原先四处),它们消除的是重复而非加间接 | [weather-cloud-decoupling-2026-10-01.md](weather-cloud-decoupling-2026-10-01.md) §8 |
| **【决策:2026-10-01】目录按子系统分,shader 进 `Shader/`** | 代码:`Weather/Rain/`、`Weather/Lightning/`(+ `Audio/`)、`Weather/Fog/`(待实现);域级文件(`VolkenWeather` / `VolkenWeatherSettings` / `WeatherPanel`)留在 `Weather/` 根。**资产:每个子系统的 shader/compute 放自己的 `Shader/`** —— `Clouds/Shader/`、`Weather/Rain/Shader/`、`Weather/Lightning/Shader/`。⚠️ **移动资产必须同步改路径字符串**(`LoadVolkenAsset<T>` 按工程路径读,不是 GUID;共 6 处) | 同上 §8 |
| **【决策:2026-10-01】雨与云零行为联动** | 雨**不读** `WeatherValue`/`IsRaining`/`CameraCloudFade`;软粒子深度直接 `_cam.GetComponent<CloudRenderer>().LinearSceneDepth`,没有云渲染器就自动降级。**数据上可互相读取,行为上互不驱动** | 同上 §8 |
| JNO 无风系统 | **✅ 已确认**:`WindManager`/`WindVelocity` 在 jnoCode 全仓库**零命中**;`IPlanetAtmosphereData` 只有气压/温度/成分,**无气流速度接口** → 雨若重做,风只能来自 `CloudConfig.windSpeed/windDirection` | 计划 §10.2c A |
| 雨重做的密度标定 | **📦 历史结论(雨已删除,重做时直接用)**:密度必须与域大小一起标定 —— EVE 的 `rain-Kerbin` 是 20 万粒子 / 半径 70m ≈ **0.139 个/m³**;域随相机速度自适应放大时必须补偿密度(指数 r^2.5)。"雨时有时无"的主因是**密度不足**,不是闪断。**密度与停留时长要一起算** | [教训](archive/weather-rain-fog-postmortem-2026-09-27.md) §5.1 / 计划 §10.2c2 |
| 雨重做的方向铁律 | **📦 历史结论(雨已删除,重做时直接用)**:①细长 billboard 的**长度轴必须在屏幕平面内表达**(世界空间长度轴 + 任意宽度轴 → 视线接近长度轴时透视压成 0 → 屏幕上是"横块");②**长度轴只由物理量决定,宽度轴由视线决定**,混用必错;③沿视线的方向分量对屏幕方向**贡献恒为 0**(会造成径向爆散);④诊断必须量**屏幕空间**角度,世界空间夹角会误导多轮。**可行算法骨架见教训 §3.1 ㈢** | [教训](archive/weather-rain-fog-postmortem-2026-09-27.md) §3.1 / 计划 §10.2d F~I |
| 两个速度不能混 | **📦 历史结论(雨已删除)**:雨丝朝向用"减玩家速度",粒子在域内平移用"减相机速度" —— 混成一个量是多次返工的根源;`Camera.velocity` 不可当玩家速度用 | [教训](archive/weather-rain-fog-postmortem-2026-09-27.md) §5.1 ⑥ / 计划 §10.2c D/I |
| 雨重做的域尺寸 | **📦 历史结论(雨已删除)**:相机速度必须 < 域半径,否则粒子每帧穿过全域 → 雨幕闪断;位移需硬钳到半域/帧 | 计划 §10.2c E |
| 雨重做的坐标系 | **📦 历史结论(雨已删除)**:用**相机相对空间 + 手工构造视锥平面**;**不要**混用 `GeometryUtility.CalculateFrustumPlanes`(它给参考系空间 → 剔除整体错位) | 计划 §10.2b ⑨ |
| GPU 绘制自检手段 | **✅ 用 GPU 回读日志判定"到底画没画"**(雨当年是 `VolkenRain:GPU`,每 10s 回读间接绘制参数 buffer 的 instanceCount),可把"剔除/参数环节"与"绘制/着色环节"一次分开,不必靠肉眼猜 | 计划 §10.4 ④ |
| SP2 雨滴 shader | **⚠️ 提取工程里的文件是 `DummyShaderTextExporter` 占位模板**,只有 Properties 块可信(`_MainTex/_Emission/_MainColor/_InvFade`);不能反推实现 | 计划 §10.2b ⑳ |
| 风的坐标系 | **📦 历史结论(雨已删除)**:不能用 `FlightData.North/East`(飞船局部,随姿态转) —— 必须在行星位置坐标系里解析构造地理北/东;风量级钳到 25 m/s | 计划 §10.2c B/C |
| 调试开关 | **⚠️ 全局调试开关必须是 `static`** —— 实例字段时 dev 命令写的实例可能与实际绘制的实例不同 → "开了等于没开"(`_activeInstance` 同源坑) | 计划 §10.2c J |
| 多相机共享材质 | **⚠️ 材质 uniform 是全局的,多相机共享同一个 Material 实例** → 每个相机绘制前必须重设基向量/相机相关 uniform,否则后一个相机覆盖前一个 | 计划 §10.2c G |
| mod 打包清单 | **⚠️ 资产清单是 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表)**,ModTools 构建时据此写 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`;新资产不会自动入包,**删掉资产文件也不会让 GUID 自动消失**(实测删除后重建的 manifest 仍带旧路径)。**增删资产都要改 `_otherAssets` 并核对无悬空 GUID**。玩家侧看资源台账用 dev 命令 `volkenAssets` | 计划 §10.2e / §10.3 1 |
| shader 验证手段 | **✅ `Editor.log` 里的 `Shader error in '<名>': … (on d3d11)`** 是唯一能离线抓到 shader 错误的途径;**Unity 的 HLSL 没有 `expm1`**(本轮实测踩到);未被场景引用的 shader 不会被编译(Rain 当年即如此,只能等游戏内验证)。**错误行按消息去重后再统计次数**,同一错误会 print 上千遍 | 计划 §10.2b ⑭ / §10.4 ② |

| **SP2 雨系统移植难点** | **🚧 已核实,阶段 0/1 已落地,核心结论已实测证实(2026-09-29)**:4 条真难点 —— ①SP2 的 `AlignStreaks` 是**世界空间长度轴 + 任意宽度轴**(正是本项目已判死刑的路线),会以**同源症状**复发(上次"横块";SP2 在 `fwd∥视线` 时同样"横块",另多一种 `side∥视线` → "细线")→ **不照抄**,改屏幕平面构轴(教训 §3.1㈢ 骨架)—— **实测:模式 2 退化视角下 A 竖条不退化、B 复现横块**;②URP 遮挡专有物在 BIRP 不存在 → **方案 A(无遮挡)起步**、方案 B 二期;③**6 kernel 的 HLSL 源码全安装不存在**(只有 DXBC)→ 必须手写 `.compute`;④数量级必须重标定(域半径随相机速度自适应 + 密度补偿 r^2.5)。**阶段 1 探针 `RainAxisProbe.cs` 已落地**(双算法对照,只量屏幕空间;dev 命令 `volkenRainAxis*`) | [计划](sp2-rain-particledomain-port-2026-09-28.md) §3 / §10 |
| **禁用导入 sp2d4 的 `.asset`** | **⚠️ 【更正母计划 §8 第 2 条】**母计划曾写"SP2 与 Volken 同为 2022.3,`BillboardParticles.asset` 大概率直接可用"—— **错**:sp2d4 实测为 **Unity `6000.2.14f1`**,本工程 **`2022.3.62f3`** → 该 `.asset` 内联的 DXBC 是按 6000.2 目标编的,跨版本重编**大概率失败**,且它在工程里是「带 GUID 的资产引用」而非可读文本,导入失败 → `FindKernel` 拿不到 kernel → 全线崩。**结论:按已确定的接口手写 `.compute`**(6 kernel + `RWByteAddressBuffer` 语义 + 自加 `_time`);`Custom/DropletInstancing` 同样只有 DXBC 且 prefab 引用为 **null**,必须自写 | [计划](sp2-rain-particledomain-port-2026-09-28.md) §3.3 |
| **雨重做的数量级基线** | **📋 不能照抄 SP2 的「50 m / 10 万」**:域半径必须**随相机速度自适应**(`needed = camSpeed × 0.6`,`radius = clamp(max(cfg, needed), cfg, 400)` —— 实测 348 m/s + 50 m → 停留仅 **0.29 s** → 闪断);密度补偿 **r^2.5**;参照密度取 EVE `rain-Kerbin` = **0.139 个/m³**(上次实测 0.0382,差 3.6 倍 → "时有时无"的真因是**密度不足**,不是闪断);**"2 万与 10 万实例肉眼几乎无差"**;上限 400000 × 12 顶点 = 480 万顶点/帧是本机可承受边界 | [计划](sp2-rain-particledomain-port-2026-09-28.md) §3.5 / 母计划 §10.2c0/10.2c2 |

> 上表「计划」= [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md)(母计划,已转 proposals);「教训」= [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md)。

**当前待定(尚未拍板/未调研)**:
- **水体大修实施顺序**:评估完成(A/B 先行),未排期([proposals/water-system-overhaul-2026-09-02.md](proposals/water-system-overhaul-2026-09-02.md))。
- **优化点剩余项**:Light Volume(#11,长期)、PlaceRays(#10,长期)、噪声 mipmap/密度 LUT(#4/#5)、HDR RT(#6)、MV 4 次膨胀(#9)等,见 [proposals/cloud-optimization-roadmap-2026-08-28.md](proposals/cloud-optimization-roadmap-2026-08-28.md)。
- **方案 C 已知缺口**:N/S 风(非刚体 Y 旋转,云空间重投影近似未覆盖)、flip/flop 双缓冲(单缓冲当前可用)、TSS 开时 !isFresh 硬回退闪烁待用户实测确认。
- ~~**to-do 高优先**:切换至有大气星球时 config 卡住~~ → **✅ 已解决(2026-10-01)**,见 §四之二 #1。
- **天气↔云解耦剩余项**:A2(相机海拔公式仍 3 份)、D(`PlanetConfigList.xml` 双写者)、F(天气值→降水/云量通道,需玩家先提出需求);以及 Unity 侧真机验收 —— 见 [weather-cloud-decoupling-2026-10-01.md](weather-cloud-decoupling-2026-10-01.md) §8「剩余」。

---

## 四之二、当前代码里的已知问题(复核)

> 这一节记录**已核实、但还没动手修**的问题,供下次开工直接取用。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | ~~**切换至有大气星球时 config 卡住**(高优先)~~ **✅ 已解决(2026-10-01)** | 根因:`config.enabled` 被当成"环境开关"在**三处**回写(`VolkenClouds` / `CloudRenderer` / `VolkenUserInterface`),而它是**玩家预设本体里的字段** → 绕无大气天体时把该行星预设静默写成"关闭",回来后 `hasAtmo && enabled` 恒 false。改法:新增 `CloudLayer.EnvironmentSuppressed`(运行时,不落盘),渲染只认 `enabled && !EnvironmentSuppressed` | [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md) §8;待 Unity 真机确认 |
| 2 | **星环渲染顺序错误**(低优先) | `to-do.md` | 星环相对云/水体层级错误 |
| 3 | **Craft 在高轨道时云层 scale 错误**(低优先) | `to-do.md` | 高轨道下云的缩放不符合预期 |
| 4 | **N/S 风重投影缺口**:云空间重投影只覆盖绕 Y 自转 + 东西风平移 | 方案 C §5 已知局限 / 割裂线 §7 遗留 | 强南北风时历史重投影失效 → 残影/鬼影;需完整 worldToCloud 矩阵 |
| 5 | ~~**`CloudConfig.low/mid/highAltitudeThreshold` 死配置**~~ **✅ 已解决(2026-10-01 复核)** | 这三个字段**早已在 `fe87e59` 随"deprecated distance/sample fields removed"删除**,全仓库已 0 命中。**并且并没有发生"反序列化失败回退默认配置"** —— `XmlSerializer` 对未知 XML 节点默认是**忽略**,不抛异常。原文那句"不可删除"的告警是**错的**(源自当时的推测,未被验证) | 无需处理;归档文档里的旧结论保留作决策记录,勿再引用为"必须保留死字段"的依据 |

---

## 五、文档写入规则(维护约定)

> 适用:所有 `docs/**` 下的 `.md`;`README.md` / `AGENT_CONTEXT.md` 本身也遵守 0 命名与 8 编码规则。
> 本规则仿照 JNO 联机 mod 的 `plans/` 目录约定(见 JNO `plans/README.md` §四)适配而来。

### 0. 命名规范
- 方案/排查/分析文档:`<topic>-YYYY-MM-DD.md` —— **英文 kebab-case 主题 + 日期后缀**(全小写、`-` 分词;日期 = 创建/事件日期,补零),例:`ksa-temporal-upscale-port-2026-08-24.md`、`weather-rain-fog-postmortem-2026-09-27.md`。
- **禁止中文文件名**(JNO 同规;历史教训:`Volken-冲突排查-…NRE.md` 这类中文名在工具链/终端里易乱码、易游离在索引外);**禁止无日期名**。
- 索引/参考文档(`README.md`、`AGENT_CONTEXT.md`)不加日期;`to-do.md` 作为活跃 backlog 也不加日期。
- 单一主题一个文件;已完成文档移入 `archive/` 子目录,文件名规则不变。
- **文档正文仍用中文**(与 JNO 一致:文件名英文、内容中文)。

### 1. 状态与单一事实源
- **文档头部 blockquote 的「状态:」是唯一事实源**;README 索引行、决策速查表、待定段、AGENT_CONTEXT 里的进度都只是它的镜像。
- 状态词汇受控,禁止自造:📋 规划中 / 研究 / 评估中 → ✅ 已实现 / 已落地 → ✅ 已归档。同一主题同一时刻只能有一个状态。
- **改文档头部状态时,必须同一次改动里同步更新**:README 索引行(§一↔§二↔§三 三区分区移动)、决策速查表、待定段,以及 AGENT_CONTEXT 相关行。

### 2. 结构模板(新建文档建议)
- 头部 blockquote:`状态:` / `日期:`(或 `创建日期:`) / `关联:`(相关文档链接 + 一句关系说明) / 一句话主题定位。
- 正文顺序:动机 → 现状 → 方案 / 决策 → 实施记录(按日期分小节)→ 剩余项 / 回归判据。
- 结论先行;被取代的方案要标注「已被取代,保留作决策记录」(如方案 A §9 历史设计)。

### 3. 决策记录
- 格式:「【决策:YYYY-MM-DD】」;明确结论写进对应文档,并汇总到 README「决策速查」表(出处列 = 文档链接 + 小节号)。
- 翻案决策:保留旧记录,加「翻案说明」链接到新决策(如方案 A:阈值重映射 → 密度缩放)。

### 4. 交叉链接
- 一律相对路径;同目录内互链用裸文件名 `x.md`。
- **路径矩阵**(照 JNO 同规):
  - 根 → `archive/x.md`、`proposals/x.md`
  - `archive/` → `../x.md`(根)、`../proposals/x.md`
  - `proposals/` → `../x.md`(根)、`../archive/x.md`
  - **同区内互链:裸文件名(不要加 `../`)**
- **移动文档后必须全仓库 grep 修正所有指向它的链接**(含文档正文、README、AGENT_CONTEXT),并跑一次死链校验。
- 站内不写本机绝对路径(一律用令牌,见 §10「隐私红线」);源码引用用工程相对路径 `Assets/Scripts/...`。

### 5. 三区流转与归档流程
> **三区**:根目录 = **活跃**(已动手);`proposals/` = **已论证可行 / 待拍板**(未在动手);`archive/` = **已完成 / 历史**。三者命名规则相同。
1. **研究完成但未拍板 / 暂停** → `git mv` 进 `proposals/`(状态保持 📋,或标 ⏸ 暂停并注明原状态),README 从 §一 移到 §二;
2. **拍板动手** → 移回根目录,进 README §一,状态改 🚧;
3. **完成一个主题** → 头部状态改「✅ 已归档(原状态:…)」;未排期剩余项 / 待复跑项在文档内写明并同步 README「当前待定」段;`git mv` 进 `archive/`(保留历史);README 从 §一/§二 移到 §三,同步决策速查表出处列;
4. 每一步都按 §4 修正全部链接;
5. 归档/提案 ≠ 删除:仍被索引引用,按摘要可随时找回。

### 6. 已知问题与修复记录
- 已核实未修的问题 → 登记进 README「四之二」表(问题 / 证据 / 影响;证据给 `文件:行号` + 反编译出处)。
- 修复后 → 该行标记「✅ 已解决」并保留记录,不整行删除。

### 7. 与代码同改、同提交
- 文档与对应代码改动放同一次提交;禁止"改了一半不提交"。
- 涉及游戏内文案、命令、路径时,同步核对 `AGENT_CONTEXT.md` 关键路径表。

### 8. 编码与校验
- 一律 **UTF-8 无 BOM、行尾 LF**。
- **不要用会把非 UTF-8 字节替换成 `U+FFFD` 或加 BOM 的工具**:尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8`(加 BOM)与 `-replace`(处理含 `[`/反引号的 Markdown 链接会吃字符)。
- 改完用脚本自检(三者任一为 True 即有问题):

```powershell
$p = '<文件路径>'
$b = [IO.File]::ReadAllBytes($p); $t = [Text.Encoding]::UTF8.GetString($b)
$bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
"BOM=$bom CRLF=$($t.Contains("`r`n")) U+FFFD=$($t.Contains([char]0xFFFD))"
```

### 9. 改后自检清单
- [ ] 所有 `](...)` 站内链接可解析(相对路径无死链)
- [ ] 无 BOM / 无 CRLF / 无 U+FFFD,行尾 LF
- [ ] 头部「状态:」与 README 索引行一致(§一/§二/§三 三区分区正确)
- [ ] 新决策已进 README「决策速查」表(出处带链接)
- [ ] 归档/提案文档已从 §一 移入 §二/§三,且全仓库无指向旧位置的链接
- [ ] 涉及代码/文案的改动已与文档同批提交
- [ ] 无本机绝对路径 / 用户名 / IP(一律令牌化,见 §10)

### 10. 隐私红线(公开仓库强制)
- **本机绝对路径 / 用户名 / IP 一律不进被跟踪文档**:本机路径统一用 `<TOKEN>` 引用。令牌→真实路径的映射**只存在本机被 `.gitignore` 排除的 `docs/LOCAL_PATHS.md`**(严禁提交;公开仓库不含该文件)。
- **令牌速查**(真实值见本机 `docs/LOCAL_PATHS.md`,此处只给含义,不给真实路径):

| 令牌 | 含义 |
|---|---|
| `<PROJECT>` | 本工程目录 |
| `<USERPROFILE>` | 本机用户目录(日志等用,如 `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`) |
| `<JNO_CODE>` | 反编译游戏源码根(只读) |
| `<JNO_D2>` | 解包工程(Unity 2022.3,水体分析用) |
| `<JNO_MP>` | JNO 联机 mod 工程(JNOmultiplayerTest) |
| `<VOLRE_REF>` | VolRe(KSP 体积云)参考源码 |
| `<KSA_REF>` | KSA 体积云参考源码 |
| `<SP2_D4>` | SimplePlanes 2 解包工程(RAIN 研究用) |
| `<SP2_CODE>` | SimplePlanes 2 反编译源码 / 分析产物 |
| `<SP2_GAME>` | SimplePlanes 2 游戏安装目录 |
| `<RVM_EA>` | RaymarchedVolumetrics 早期预览版(EVE 降水配置参照) |

- 引用反编译源码时,保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),**不要**写本机路径形式的站外链接。
- **上传前**:把文档内 `<TOKEN>` 之外的本机路径全部替换为令牌;`LOCAL_PATHS.md` 已被 `.gitignore` 排除,不会误提交。
