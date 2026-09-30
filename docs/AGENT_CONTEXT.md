# Volken2 项目 —— 会话启动上下文(通用提示词)

> 用法:每次开新会话做本项目前,把本文档(或下面"0. 一句话定位 + 1. 关键路径"起的内容)作为首条上下文交给 AI,可省去大量重复调研。
> 本文件是**只读参考**,不是 plan;方案/决策类内容一律写进对应主题文档(索引见 [`README.md`](README.md))并同步更新索引与决策速查表。
> 本文档内容基于 `docs/` 内归档文档与代码现状整理;引用时保留「文件名 + 行号」,不写本机绝对路径(令牌见 [`LOCAL_PATHS.md`](LOCAL_PATHS.md),本机路径映射,**严禁提交**)。

---

## 0. 一句话定位

给 **SimpleRockets 2 / JNO**(Steam AppID 870200)写**体积云 mod `Volken`**(Unity 工程,跑在 **Built-in Render Pipeline (BIRP)** 的屏幕后处理链上)。核心是 `Clouds.shader` 的 raymarch 体积云 + 时序超采样(TSS/KSA 结构)+ 游戏自带云分布接入。

**当前活跃方案**:只有 **雨重做**([`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md));天气母计划 / 雷声真实化 / 体积云优化路线图 / 水体大修均已转 `docs/proposals/`(已论证未排期,不在动手)。
**已完成进度**:方案 A/B/C 全部已实现并归档(自带云区域内密度缩放、自带云全球分布、KSA 时序超采样移植);轨道 2D 云 + 过渡带交叉淡入已实现;割裂线根因(重投影 Y 镜像)已修复;JNO 冲突已定位修复;水面反射云(方案 A)已落地。**天气系统(SP2 移植)范围已缩减为「只做雷电」** —— 雨(阶段 2)与雾(阶段 3)的实现已整体移除、待重做(复盘:[`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md);**雨重做的难点与 8 阶段分步计划见 [`sp2-rain-particledomain-port-2026-09-28.md`](sp2-rain-particledomain-port-2026-09-28.md)**,动手前必读;**阶段 0/1 已落地(2026-09-28)**:`RainAxisProbe.cs` 双算法对照探针 + 5 条 dev 命令 `volkenRainAxis*`,dotnet 编译 0 错误;**2026-09-29 真机验证核心结论已实测证实**(模式 2 退化视角:A=竖条不退化 / B=SP2 复现"横块";①②③⑤ 通过,④⑥ 待补,详见该文档 §10.4);**阶段 2 最小 compute 管线已落地并真机全绿(2026-09-29)**:`RainParticles.compute/.shader/.cs` + dev 命令 `volkenRainP2*`,`_otherAssets` 已登记两资产;真机日志管线全绿(instanceCount=10000/20 回读正确、SV_InstanceID 等距排 pos 回读 +3m 实锤、径向 GravityNormal + 相机速度补偿 + 屏幕平面构轴三处修复落地,§10.5~10.8);**阶段 3 正式 BIRP 雨滴 shader 已落地(2026-09-29,§10.9)**:SP2 第一手属性名(`_MainTex/_Emission/_MainColor/_InvFade`)+ 屏幕平面构轴 + 随机滚转角 + 随速度拉伸 + 软粒子(采样 `CloudRenderer.LinearSceneDepth`),新增命令 `volkenRainP2Stretch` / `volkenRainP2Soft`,dotnet 0 错误,**待重打包+真机验证**;**阶段 3 第一轮真机"竖线贴相机"根因已定位并修复(§10.10,2026-09-29)**:pos 回读证明云刚性锁相机+相对位置冻结 = `frac` 回卷把粒子坍缩到相机 + 雨丝轴不编码相对运动;修复 = 回卷改随机重生 + 相对速度流线轴(`_RelVel`,`volkenRainP2Stream 1|0`)+ 诊断 `dt/down/pos0Δ`;**阶段 3 第二轮真机"雨一团+不动"根因已定位并修复(§10.11)**:`_RandomData` 只绑 Randomize kernel,Positioning 读未绑定 buffer → 整段空转(pos 全程逐字节冻结、camVel 锁也死);修复 = Positioning 不再读 `_RandomData`(seed 用 id.x 派生)+ 防御性绑定 + `p.w=_time` 执行探针(`w0=` 回读)+ `_lastCamVel` 锁存修暂停变竖直;**阶段 3 第三轮真机(§10.12,2026-09-29)w0 探针证伪 §10.11**:§10.11"冻死"实为游戏暂停(`Time.time` 冻结在 164.5,前 3 次回读 kernel 活着、`pos0Δ=60.7/27.0` 位置在动);真根因 = 流线轴屏幕投影随视角摆 → **默认改回恒屏幕竖直**(StreamMode=false)+ **流线轴/拉伸速度源改 `CraftScript.FrameVelocity`**(纯平移,无旋转切向分量,心跳加 `craftSpd=`);**阶段 3 第四轮真机(§10.13,2026-09-29)按 sp2d4 第一手实锤整体重做**:用户反馈"垂直往下看糊屏 + 雨贴相机随转动/zoom 变" → 查 SP2 真实现(`sp2d4/rain_analysis`):**`AlignStreaks` 用世界空间旋转矩阵 `_RotationMatrix`(列0=侧向1、列1=相对速度×拉伸、列2=侧向2),零屏幕投影**;粒子**世界系下落**(`_particleVel=dt×(风+下落)`,**无 camVel 补偿**),域中心=相机位置+出球重生;**换帧用 `TranslateParticles`**;常规视角速度源=`playerVelocity` 且 **y 取绝对值** → 本轮**废弃屏幕平面构轴**,改 shader `_RotationMatrix` + compute 去 camVel + 新增 `TranslateFixed` kernel + ModApi `IGameView.ReferenceFrameRecentered` 挂钩(`GameWorld.FloatingOriginChanged` 在 mod 的剥版 API 程序集里不可用)+ 去随机滚转角 + UV 亮头拖尾;**阶段 3 第五轮自查(§10.14,2026-09-29)**:修两个真 bug —— ① 球内半径分布用 `R*u` → 体密度 ∝1/r²(粒子挤在球心,是"一团"+垂直看糊屏的帮凶)改为 `R*u^(1/3)`(体积均匀);② 出球重生不可观测 → 加 `_DiagBuffer` 原子计数(回读得 respawns/s);另修一个**日志陷阱**(`Material.HasProperty` 看不到只在 HLSL 声明的 uniform,会把新 shader 误报成旧版 → 加 `_ShaderVer` 部署哨兵),并写下矩阵列约定的证明;新增全量诊断日志(SELFCHECK 块 / axis 行含 `·fwd` 指纹 / dist[64] 分布统计 / time 探针 / recenter 事件),**待重打包再验**;**阶段 3 第七轮真机 + UI 接入(§10.16,2026-09-29)**:真机判读全绿 —— `SELFCHECK shaderVer=3 新版✓` + `TranslateFixed=2`(compute/shader 都是新版)+ `down·camUp=-0.98`(**`GravityNormal` 实测就是画面下方**,§10.15 结论成立)+ `·fwd=0.12~0.55` 随视角变(**不是**屏幕平面构轴)+ `dist mean/R=0.65~0.76`(体积均匀生效);从日志抓到**换帧事件不触发的真 bug**(`rec=0` 但相机帧坐标跳 0.5~1.6km、`craftSpd=0`/`dt=0` → 雨被留在原地 → respawn 飙到 2.2~5 万/s)→ 修:事件回调**只记录不应用**(避免双重平移)+ **`ICraftScript.FramePosition` 跳变兜底判定**(`> max(100m, 自身运动×3+30m)`)+ 每 Tick 只应用一次 + `rec=N(ev/jp)`/`craftJump` 诊断;**接入 UI 实时控制**:`RainSection` 新增 `streamMode/stretchAmount/edgeFade/softParticles/tailFalloff/brightness`(默认=SP2 出厂值,`CopyFrom`/`Clamp` 同步,老 XML 不受影响)+ `RainParticles.ApplyConfig/StatsLine/DensityPerM3/RebuildMesh`(`FallSpeed/StreakLength/StreakThickness/Falloff` 由 const 改 static;容量→重建 buffer、长宽→重建网格;`AttachToCurrentView` 末尾按配置自动开雨)+ `WeatherPanel` 雨组由"禁用占位"改为**实时控制**(启用开关/实时状态行/立即切换按钮/11 滑块)+ ZH-CN/EN-US/RU-RU 各 11 个新键;**阶段 3 第八轮高度闸门(§10.17,2026-09-29)**:用户指出 **SP2 与 JNO 的相机缩放尺幅不同**(SP2 最大只到"半个岛"远低于云层、从不需要处理太空;JNO 能缩到整颗星球 → 不加限制会"太空里还下雨");SP2 机制 = `CloudHeightFade = Environment.CameraCloudFadeVal` + 只在地面以上才 Update → 本实现:**上限 = 雨自己的独立配置项** `ceilingAltitude`(默认 12000m;0=关闭闸门;**用户决定不与云层联动**,不读 `CloudConfig.maxCloudHeight`)+ 相机海拔同 `CloudRenderer.ComputeCameraAltitude` 公式 + `altFade = saturate((ceil−alt)/(ceil×band))`(band 0.4)乘进 `_FadeAmount`,**`altFade≤0.001` 直接不 dispatch/不绘制**(太空零成本、零雨);抑制期间同步换帧状态(否则恢复时会把累积跳变误判成换帧);配置 `ceilingAltitude`/`ceilingBand` + 面板两滑块 + 状态行/心跳 `alt/ceil/altFade`;遗留:水下门控(阶段 4)。**阶段 3 第九轮(§10.18,2026-09-29)诊断"说不出的怪异感"(转镜头/缩放时最明显)**:主嫌疑 = **出域处置方式** —— 证据:`BillboardParticles.asset` 的 Positioning **`inBuffers: []`、`outBuffers` 只有 `_Positions`(stride 12 = float3,无 w)**、常数只有 `_domainRadius/_domainPos/_frameVel/_particleVel/_InstanceCount` → SP2 **没有每粒子随机数**,出域处置只能是位置式确定性;而我 §10.10 改成"球内均匀随机"→ 粒子在**紧贴镜头处**凭空冒出 → 整场跟随相机**沸涌**(转镜头/缩放最刺眼)→ 改为**穿过球心镜像重生**(`_respawnMode=1` 默认,`p = center − normalize(p−c)·R·0.999`),新粒子永远在对侧 50m 边界出现,配合 `_EdgeFade` 边界渐显 = 连续雨帘;开关 `volkenRainP2Respawn`/配置 `respawnMirror`/面板「出域镜像重生」;次要嫌疑(建议 A/B):**软粒子是我加的、SP2 没有任何深度纹理**(→ `volkenRainP2Soft 0` 验证屏幕空间 alpha 爬行)、**50m 域在 JNO 缩放尺幅下会显成"雨球"**(→ `volkenRainP2Edge 0`/拉远验证;阶段 4 自适应域半径解决)、十字截面世界系朝向(**与 SP2 相同,非差异源**)。**阶段 3 第十轮(§10.19,2026-09-29)删除全部控制台指令、旋钮全进面板**:按用户要求删掉 9 条 `volkenRainP2*` 指令与对应静态 setter,功能 100% 平移到天气面板「雨」分组(22 项;新增「把雨状态写入日志」= 原状态指令的 UI 化、「开发:等距排自检」= 原 Row);中/英/俄各加 3 键;**约定:以后可调项一律进面板,不加控制台指令**;**阶段 3 第十一轮(§10.20,2026-09-29)修"和下面条一个样"**:根因 = 第十轮把参数真值来源换成 weather.xml,而**配置层旧默认值(雨丝宽度 0.05、粒子数量 20000)覆盖了代码默认值(0.1、10000)**,且 **XML 反序列化对已存在字段用存档值**(磁盘 `UserData/VolkenWeatherConfig/Droo/*.xml` 里正写着旧值)→ 改 C# 默认值不生效 → 修:① 配置默认值对齐 SP2 出厂值(`streakWidth 0.1`、`amount 100000`、新增 `stretchLimit 3.5` + 面板滑块);② 新增 `VolkenWeatherConfig.UpgradeUneditedDefaults()`(**只重写"仍是旧默认值"的字段**,加载后调用+打日志);③ 程序化贴图两处:横向由"中心亮线"改"满宽软板条"、纵向由"满长亮柱"改"快速收尾"(= SP2 软 blob 拉伸后的软芯长条);**阶段 3 第十二轮(§10.21,2026-09-29)澄清真问题 = "雨像下面条一样集中一股脑下降"**(§10.20 按雨丝形状修是修错方向):元凶 = §10.18 的**确定性镜像重生** —— 同速下落 + 确定性处置 = **零混合**,初始每一簇粒子永远成团、周期性一起落下(静止周期 2R/15 ≈ 6.7s、飞行 ≈ 0.6s 高频脉动)→ **默认改回随机重生**(`respawnMirror=false`,镜像仅对照)+ 随机位置限定**壳层 [0.15R,R] 体积均匀**(排除球心 0.3% → 不紧贴镜头冒出,兼顾混合与不爆闪);**教训:随机化(持续混合)才是"像真雨"的关键,为消除近旁闪而改成确定性是本末倒置**;**阶段 3 第十四轮(§10.23,2026-09-29)测试脚本收进单一文件夹 `Assets/Scripts/VolkenTests/`**(用户诉求:"和丢进游戏的相对独立"):把 `Volken/Debug/{RainPreview,NoiseVisualizer,RaymarchDebug}.cs`、`Volken/Weather/RainAxisProbe.cs`、`Volken/Profiler/*`(共 8 个 .cs + 9 个 .meta)**搬进 `VolkenTests/`**,并**摘除正式代码里全部 7 处引用**(`Mod.cs` 的 5 条 `volkenRainAxis*` 注册 + `ProfilerController.Create()`;`VolkenMod.cs` 的 2 处 `RainAxisProbe.AttachToCurrentView()`),改由 **`VolkenTests/TestsBootstrap.cs` 自发注册**(`[RuntimeInitializeOnLoadMethod]` + 重试至控制台就绪);命名空间统一 `Volken.Tests`(顺带修掉 `Volken.Debug` 遮蔽 `UnityEngine.Debug` 的隐患),`Profiler/` 保留 `VolkenProfiler`;**契约**=单向依赖 + **可整体删除**(删掉文件夹 mod 仍能编译运行)+ README.md 记录自查口令;验证:正式目录只剩 24 个正式文件、自查**零代码引用**、dotnet build 0 错误;
**阶段 3 第十三轮(§10.22,2026-09-29)把雨搬进 Unity 编辑器**(用户诉求:每轮打包 5 分钟没法迭代观感):新增 `Assets/Scripts/Volken/Debug/RainPreview.cs` **编辑器预览台**(自动建相机/地面/近中远参照方块、右键拖拽转视角 + WASD/QE 自由飞 + Shift 加速 + 滚轮调速、左上角 IMGUI 面板与游戏内天气面板**同一批字段即时生效**、标 ◈ 的容量/雨丝长宽松手才重建资源、实时状态行、重置 SP2 出厂、`PlayerPrefs` 一键「下次 Play 自动启动」仅编辑器生效)+ `Mod.LoadVolkenAsset` **编辑器分支回退 `AssetDatabase`**(`#if UNITY_EDITOR`)+ `RainParticles.DebugAltitudeOverride`(NaN = 真实海拔;编辑器里手动喂以试"云上/太空"闸门);**依据**:`RainParticles` 对游戏 API 的依赖本就"失败即降级"(down→世界下、速度源→相机实测位移速度、闸门→−1、软粒子→无深度图自动关、换帧事件→只打日志)→ 编辑器里跑的是**同一份 compute/shader/驱动代码**,观感结论可外推;**预览测不到**:软粒子、换帧重定位、真实海拔、配置持久化 → 定稿后仍需打包一次做游戏内验收;**本轮另有一次事故**:内联 pwsh 命令里的**中文路径**被 shell 编码破坏 → `ReadAllText` 失败但脚本未停 → 空内容回写把 986 行计划文档覆盖成 1 节,已用同目录 ASCII 全本镜像 `sp2-rain-particledomain-port-2026-09-28.md` 复原(现两份各 1035 行、内容一致)——**铁律:命令行里绝不写非 ASCII 路径(用 `Get-ChildItem -Filter` 取 `.FullName`/编辑工具),`ReadAllText` 后必须确认成功**;Phase 1 的 `volkenRainAxis*` 5 条暂留(另一套构轴探针,无 UI 对应物,§10.4 ④⑥ 未补)。**下一步:用户 SP2 实机对比观感,反馈后进阶段 4(密度 0.139~0.191/m³ 标定、自适应域半径、`_mainLightIntensity`、遮挡剔除)**;**阶段 3 第六轮再挖 SP2(§10.15,2026-09-29)**:拿到 SP2 雨**出厂参数表**(`sp2d4/Assets/Resources/prefabs/ParticleDomain.prefab`:`R=50` / **`particleAmount=100000`** / `fallSpeed=15` / `streakLength=2.5` / `thickness=0.1` / `stretchAmount=0.045` / `limit=3.5` / `mainLightIntensity=1.2` / `occlusionThresholdAGL=100`;贴图=软 blob、compute=BillboardParticles),**解掉"down 用哪个"的最大疑点**:`CraftScript.GravityNormal => _flightData.GravityFrameNormalized`(**帧空间**,`ReferenceFrame` 旋转是 `Quaternion.Euler(0,yaw,0)` **yaw-only 实锤**),而 SP2 是世界 -Y(其帧竖直对齐)**且 jnoCode(SR2)里根本没有 ParticleDomain/ParticleHandler/WeatherValue**(这套雨是 Simple Planes 2 的)→ **SR2 必须用帧空间径向 down,我的实现正确**;并按 SP2 补两处:**域边界淡出**(SP2 把 `_DomainPos`/`_DomainRadius` 传给 material;`volkenRainP2Edge`,默认 0.2)+ **程序化柔边雨丝贴图**(SP2 用软 blob,我原先硬边四边形是"面条感"来源之一);shader 哨兵升到 `_ShaderVer=3`,新增 `down·camUp` 判读(应 ≈ -1),**待重打包再验**;**天气参数按预设名独立存**(预设名与云层**互相独立、不做配对联动**,落在 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`;旧的内联 `<Weather>` 会在读取清单时自动迁移)。**未完成/待办**:to-do 高优先项「切换至有大气星球时 config 卡住」、星环渲染顺序、高轨道云层 scale;优化路线图剩余项(Light Volume / PlaceRays / 噪声 mipmap / HDR RT);水体大修实施未排期;方案 C 的 N/S 风重投影缺口。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `<PROJECT>` |
| Mod 源码 | `Assets/Scripts/Volken/` |
| **测试/开发工具**(与正式代码解耦,可整体删除) | `Assets/Scripts/VolkenTests/`(雨预览台 `RainPreview.cs`、构轴探针 `RainAxisProbe.cs`、噪声/步进可视化、`Profiler/`;命令由 `TestsBootstrap.cs` 自发注册。**正式代码对它零引用** —— 契约见该文件夹 `README.md`) |
| 文档索引 | `docs/README.md`(五节:三区索引(活跃 / proposals / 归档) / 决策速查 / 已知问题 / 写入规则) |
| 会话上下文 | `docs/AGENT_CONTEXT.md`(本文件) |
| 活跃 backlog | `docs/to-do.md` |
| 跨界移植/调试方法论(雨雾移植失败复盘,通用铁律) | `docs/archive/weather-rain-fog-postmortem-2026-09-27.md`(做任何"细长 billboard / GPU 粒子 / 相机相关朝向"特性前必读) |
| 雨重做的难点与分步计划(**开工前必读**) | `docs/sp2-rain-particledomain-port-2026-09-28.md`(4 条真难点 + 8 阶段准出判据 + 停止条件;含**对母计划 §8 第 2 条的更正**:sp2d4 是 Unity 6000.2,`.asset`/`.compute` **不可跨版本导入,须手写**) |
| SP2 天气移植母计划(雾/雨历史决策沿革,**已转 proposals**) | `docs/proposals/sp2-weather-port-2026-09-27.md`(§10.2e 移除记录、§10.2f 勿照做、§10.2g 配置合并、§10.2h 天气标度路线修正) |
| 雷声真实化 + 闪电修复(**已转 proposals**,代码已落地待打包) | `docs/proposals/thunder-realism-2026-09-28.md`(声速 API / 距离衰减 / 残留与紫红根因;§5.3 打包待做) |
| 本机路径映射(真实值,**仅本地**) | `docs/LOCAL_PATHS.md`(已被 `.gitignore` 排除,严禁上传) |
| 游戏运行日志 | `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log` |
| 反编译游戏源码(只读) | `<JNO_CODE>`(含 `SimpleRockets2/`、`ModApi/`) |
| 解包工程(Unity 2022.3,水体分析用) | `<JNO_D2>` |
| JNO 联机 mod 工程 | `<JNO_MP>`(冲突排查用) |
| KSA 体积云参考源码 | `<KSA_REF>`(方案 C / 轨道云 / 优化点文档的移植母本) |
| VolRe(KSP EVE)参考源码 | `<VOLRE_REF>` |

## 2. 架构与关键文件(Assets/Scripts/Volken/)

| 文件 | 职责 |
|---|---|
| `Core/VolkenMod.cs` | mod 入口:`OnSceneLoaded` 注册/初始化、`CloudRenderer` 创建、`StockCloudMap.LoadFor/Release`(有大气 SOI 进/出) |
| `Core/VolkenUserInterface.cs` | UI(方案 A/B/C 分组、覆盖分解滑块、轨道云组、本地化);曾含自愈初始化 `Update` 驱动,已撤除 |
| `Core/PlanetConfig.cs` | **行星 → 预设名 的映射**:`PlanetConfig`(`PlanetName`/`CloudConfigName`/`ExtraCloudConfigName`/**`WeatherConfigName`** + `LegacyWeather`,后者**仅用于迁移旧内联格式**)+ `PlanetConfigList`(清单读写 + 旧格式迁移 + `Get/SetWeatherConfig`)。落盘 `UserData/VolkenConfig/PlanetConfigList.xml`。**云与天气的预设互相独立**(各自的名字、各自的列表、各自新建保存,**不做配对联动**):云 `UserData/VolkenConfig/{行星}/{预设}.xml`,天气 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`(类在 `Core/VolkenWeatherConfig.cs`,含 `GetAllConfigNames`)。⚠️ `AddConfig` 对已有行星只更新预设名(勿改回 `Add`) |
| `Weather/VolkenWeather.cs` | 天气状态机单例:天气值推进/随机/淡变、相机指标(海拔/太阳时/云内淡化)、雷电起停。**各子系统阈值读自己的配置字段**(`rain.triggerValue` / `lightning.stormValue`),不用任何全局档位常量 |
| ~~`Weather/WeatherTypes.cs`~~ | **❌ 已删除(2026-09-27)** —— SP2 的全局天气档位常量(Clear/Few/Rainy/Stormy…)+ `Classify`。**Volken 刻意不要"全局预设"这一层**:云/雨/雾/雷的参数全部由玩家逐项设置并序列化。`Classify` 的去向 = `VolkenWeather.DescribeWeatherValue`(纯显示,**不要在它上面加逻辑**) |
| `Core/SerializableTypes.cs` | 序列化辅助类型 |
| `Clouds/CloudRenderer.cs` | **核心**(~484 行):`[ImageEffectOpaque] OnRenderImage` 全流程(远近深度合并 → 逐层 Cloud pass → DilateMV → Upscale → Composite);`SetLayerDynamicProperties`;`BuildCloudSpaceRepro`(云空间重投影,`prevViewProjMat = GL.GetGPUProjectionMatrix(...) * worldToCameraMatrix`);订阅 `IGameView.ReferenceFrameRecentered`(原点重置清历史) |
| `Clouds/CloudLayer.cs` | 每层 RT 管理(cloudTex/cloudDepth/cloudMV、historyTex/historyDepthTex/historyCloudDepthTex);`SetStaticShaderProperties`;TSS 开关按层处理 |
| `Clouds/CloudConfig.cs` | 配置:coverage/density/layerStrengths、`useStockCloudMap/stockMapStrength/stockDensityScale/stockMaskInfluence/stockAlign*`、`useOrbitClouds/orbitTransition*`、TSS 相关;`low/mid/highAltitudeThreshold` 为**死配置**(勿删,旧 XML 兼容) |
| `Clouds/Clouds.shader` | 体积云 shader:Clouds pass(低清全量 raymarch,MRT 颜色/云面距离/MV)、DilateMV(3×3 膨胀)、Upscale(时序核心)、Composite、OrbitClouds(轨道 2D)、ReflectionComposite;`SampleDensity/SampleDensityCheap/SampleCoverageCheap`(方案 A/B/C + 覆盖分解的 4 处同源消费点) |
| `Clouds/CloudNoise.cs` / `CloudNoiseCompute.compute` | 3D Worley 噪声(GetWhorleyFBM3D)与 compute 变体 |
| `Clouds/StockCloudMap.cs` | 游戏自带云 cubemap 静态缓存:`LoadFor(IPlanetNode)` 按画质档加载、缺层检测、`Release()` |
| `Clouds/UpscalingPixelSequence.cs` | KSA 最优采样序列算法(格网变化时重建缓存) |
| `Clouds/CloudReflectionRenderer.cs` | 水面反射云渲染(方案 A;读 `ModSettings.Instance.WaterReflection`,默认关;复用 Clouds pass) |
| `Clouds/FarCameraScript.cs` / `DepthCapture.cs` | 远相机 CommandBuffer 深度抓取(`farDepthTex`,不用 OnRenderImage,避免割裂线) |
| `Clouds/CloudLayerView.cs` | 调试/视图辅助 |
| `Water/ForceSetting.cs` | 按高度切换水透明等强制设置(水体 A 阶段雏形) |
| `PlanetRing/PlanetRingsZWriteFix.cs` | 星环渲染(相关待办:星环渲染顺序错误) |
| `HarmonyPatches/` | `LayoutRebuiltPatch.cs`、`PlanetRingsShaderPatch.cs` 等 Harmony patch |
| `Debug/`(NoiseVisualizer / RaymarchDebug)、`Profiler/` | 诊断与性能工具 |

## 3. 已确定的技术事实(不要再重复调研)

**渲染管线(BIRP 约束)**
- 全流程挂在 `[ImageEffectOpaque] OnRenderImage` 一次调用内:远深度(`farDepthTex`,CommandBuffer)→ 合并 `combinedDepthTex/lowResDepthTex` → 每层 `Clouds` pass(低清全量 raymarch,MRT:颜色+云面距离+本帧运动矢量)→ `DilateMV`(3×3 反距离加权 ×3,无 1 帧滞后)→ `Upscale`(全清时序)→ 逐层链式 `Composite` → Blit。
- 云 raymarch 相机无关:观察射线由 C# 每帧传入 `_CamFwd/_CamRight/_CamUp/_TanHalfFovV/_Aspect` 构造。
- **时序(TSS)核心**:`Upscale` pass 逐像素 `isFresh`(本帧采样格)取本帧 raymarch,否则 `lerp(重投影历史, 本帧, tssBlend)`;运动自适应 `tssBlend = lerp(_TssBlend=0.5, 1.0, saturate(|MV|·200))`;本帧无云但历史有云 → `≥0.85` 收敛。TSS 关:运动残影 `lerp(本帧, 重投影历史, historyBlend=0.90)`。
- **重投影必须用 GPU 投影**:`prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix`(逻辑投影与 GPU clip 的 Y 约定相反 → 割裂线,已修)。
- **云空间重投影**:`φ = accumulatedRotation + 2π·runningOffset.x`(风平移折算经度旋转),`reproj = prevViewProj * RotAroundCenter(C, +Δφ)`(`CloudRenderer.BuildCloudSpaceRepro`,逐层缓存 `prevCloudAngle`);**N/S 风(`cloudOffset.z`)未覆盖**(已知缺口)。

**自带云分布(方案 B/A)**
- `StockCloudMap`(游戏 Clouds cubemap):R=低云,G=中云,B=高云,A=纬度/行星遮罩,全部 clamp01;`planetToBody` 矩阵做参考系→本体系对齐,`stockAlignSign/AngleOffset` 微调。
- 消费点在 `SampleDensity/SampleDensityCheap`(+轨道两份共 4 处)**:`stockCov = stockBand * stockDensityScale`(方案 A,仅 mapVal),`dist` 用未缩放 stockBand(保足迹边缘)**;`useStockCloudMap=0` 或星球无自带云时 uniform 分支跳过,零开销。
- 覆盖分解:`coverage = cloudCoverage × F_biome(biomeStrength)× F_rotDist(rotDistStrength)× F_tiledDetail(detailCoverageStrength)`,三强度默认 0 = 逐字节一致。

**轨道云(2D)**
- 海拔分派(CPU 侧,全 GPU 无同步):`camAlt < start` 只跑体积云;`start~end` 双渲染 + `orbitFade`(smoothstep)交叉淡入;`> end` 只跑 2D(`OrbitClouds` pass,壳求交 + 同源 `SampleCoverageCheap` 单点 + 光学厚度积分/transmittance 阻尼能量,与体积云 Beer 同源);反射路径恒 `_OrbitFade=0`。
- `orbitDebugMode` 分屏(左=体积云,右=2D;红=着色后不透明度,绿=原始足迹)、`Volken:OrbitDiag`/`Volken:Coverage` 日志为诊断用。

**深度 / 遮挡**
- 体积云靠 `DepthTex`(lowResDepthTex)在场景深度处提前终止;OrbitClouds 同样采样 DepthTex,`sceneDepth <= tStart` 返回透明(craft 遮挡一致)。
- 近/远深度相机拼接缝历史已修(CommandBuffer);RFloat `cloudDepthTex/histCloudDepthTex` 读 R 通道(勿读 alpha)。

**通用架构铁律(雨/雾移植失败的沉淀,详见 [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) §5)**
- **细长 billboard 的"长"必须在屏幕平面内表达**:长度轴只由**物理量**决定,宽度轴由**视线**决定(`W = normalize(cross(L, viewDir))`);世界空间长度轴 + 任意宽度轴 → 视角相关地退化成"横块"。
- **沿视线的方向分量对屏幕方向贡献恒为 0** → 归一化近零向量 = 噪声(径向爆散);退化情况必须显式兜底。
- **每帧都会调到的路径里禁止"重置动画进度"**;重入守卫只能比较**目标值**,且**只能读状态,不能挡在"改状态的逻辑"前面**(否则状态机被自己的守卫锁死)。
- **诊断必须量你关心的那个量所在的轴**(屏幕问题就量屏幕空间),并同时输出**当前值 + 内部状态**,才能区分"从没触发"与"触发后被重置"。
- **日志按消息去重后再统计次数**(同一条 Unity shader 错误会对 N 个 kernel × M 个平台各报一遍);优先用**累计计数器**而非节流日志。
- **先标定数量级,再调观感**:密度(个/m³)、域半径、停留时长(域直径 / 相机速度)要同时满足;`GraphicsBuffer.GetData` 是同步回读,不可常驻。

**坐标原点重置(浮动原点)**
- SR2 会 `RecenterReferenceFrame`(离帧中心>5000m / 帧速>1000m/s / 时间加速每帧 / 表面锁定切换)→ 世界坐标整体平移 → 时序历史失效。
- 修复:订阅 ModApi `IGameView.ReferenceFrameRecentered`(委托签名 `(IReferenceFrame, Vector3d, Vector3d)`),回调清空历史 + `frameNumber=0` 冷启动。

**冲突 / 兼容**
- JNO 联机 mod 的 `MultiPlayerUI.OnSceneLoaded` 曾在 `inspectorPanel==null` 时抛 NRE 中断 `SceneLoaded` 事件链 → Volken `OnSceneLoaded` 被跳过(看不到云/自带云开关锁死);JNO 侧空值护栏已手动应用,Volken 侧自愈已撤除(纯事件驱动)。
- Mods 目录勿同时放 `Volken.sr2-mod` 与 `Volken-R.sr2-mod`(同名程序集冲突)。
- `CloudConfig.low/mid/highAltitudeThreshold` 是死配置但**不可删除**(旧 XML 含节点,删除 → XmlSerializer 反序列化失败回退默认配置)。

## 4. 游戏 API 关键入口(反编译确认 / ModApi)

- `Game.Instance.SceneManager.SceneLoaded`(Volken `OnSceneLoaded` 注册处,`VolkenMod.cs` L69)
- `ModApi` `IGameView.ReferenceFrameRecentered`(原点重置事件,委托 `(IReferenceFrame, Vector3d, Vector3d)`)
- `PlanetCubemapUtility.LoadCubemap(data, PlanetCubemapType.Clouds, size, false)`(自带云 cubemap 加载;`PlanetCubemapType.Clouds` R/G/B/A 语义)
- `WaterReflectionPlaneScript.UpdateReflections(Vector3, Vector3[, Camera])`(水面平面反射,`_WaterReflectionTexture`;反射云挂钩点,见实时反射适配分析)
- `ReflectionProbeScript`(机体 cubemap 探头,方案 B 未做)
- `WaterMaterialModifier` / `PlanetWaterConfig` / `WaterQualitySettings`(水体调参入口,水体大修路线 A/B)

## 5. 开发流程约定

1. **文档写入与维护规则一律以 [`README.md`](README.md) §五 为准**(命名 / 状态单一事实源 / 决策记录 / 交叉链接 / 三区流转 / 编码校验 / 改后自检清单)。研究有明确结论 → 直接写进对应主题文档(加「【决策:YYYY-MM-DD】」),并同步 README 决策速查表。
2. **三区**:根目录 = 活跃(已动手,当前只有 `sp2-rain-particledomain-port-2026-09-28.md`);`docs/proposals/` = 已论证 / 待拍板(未在动手);`docs/archive/` = 已完成 / 历史。状态变化时按 README §五 流转(头部状态 + `git mv` + 修链接 + 更新索引)。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/Volken/HarmonyPatches/`。
4. shader 改动注意两处同源同步:3D 体积云(`Clouds` pass)与轨道云(`OrbitClouds` pass)的密度/覆盖消费函数共 4 处,须同步改;`orbitDebugMode=1` 分屏复验。
5. 新增游戏内可见文案 → 同步改本地化文件(EN-US/ZH-CN/RU-RU,key 前缀 `Volken.UI.*`);新配置字段 → `CloudConfig.Clone/CopyFrom` + 旧 XML 兼容(默认值 = 关闭/恒等)。
6. 回复中给出改动的文件(带完整路径),方便点击。

## 6. 调试/验证

- **日志**:`Mod.LOG`(受 `ShowDevLog` 控制);游戏内诊断:`Volken:OrbitDiag`(2s 节流,相机海拔/淡入/RT 尺寸/真实配置)、`Volken:Coverage`(覆盖探针,`orbitDebugMode>0` 时启用)。
- **UI 开关**:`useTemporalUpscale`(TSS 3×3)、`historyBlend`(运动残影)、`useStockCloudMap`(方案 B)、`stockDensityScale`(方案 A)、`useOrbitClouds` + 过渡带(轨道云)、`orbitDebugMode`(分屏)、`WaterReflection`(反射云,默认关)。
- **复现/验收要点**(详见各归档文档):默认配置全部关闭时行为与旧版逐字节一致;开启各功能后对照归档文档的验收清单。
- **文本编码约定**:`.md` / `.cs` 一律 **UTF-8 无 BOM、LF**。改文档时不要用会把非 UTF-8 字节替换成 `U+FFFD` 的工具(尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8` 与 `-replace`,前者加 BOM、后者在处理含 `[`/反引号的 Markdown 链接时会吃字符)。**完整写入规则 / 校验脚本 / 改后自检清单见 [`README.md`](README.md) §五。**
