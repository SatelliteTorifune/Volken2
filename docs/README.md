# Volken2 文档索引(docs/)

> 项目:Volken(SimpleRockets 2 / JNO 体积云 mod,Unity BIRP)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 关键文件 / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `docs/` 的导航页。**当前活跃文档:`to-do.md`(待办)、`Volken-SP2天气系统移植BIRP计划-2026-09-27.md`(天气系统,现只做雷电)、`Volken-天气雨雾移植失败教训-2026-09-27.md`(雨/雾重做前必读的复盘)、`Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md`(优化路线图)、`Volken-水体系统大修可行性分析-2026-09-02.md`(评估完成,实施未排期)**,其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:方案/排查/分析单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引;**完整文档写入规则见 [§四](#四文档写入规则维护约定)**。
> **调试日志路径**:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志)。

---

## 一、当前活跃(尚有未完成工作)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`to-do.md`](to-do.md) | **待办清单**(高/低优先度 + 已修复台账) | 📋 活跃 backlog | config 卡住(高)、星环渲染顺序/Craft 高轨道云 scale(低);已修复项附根因简述(水面覆盖云、TSS 拖影、原点重置偏移、JNO 冲突) |
| [`Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md`](Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md) | **体积云优化路线图**(借鉴 VolRe/KSA) | 📋 分析完成,T0 部分落地 | 13 项优化点按收益排名:T0 光照解耦(50→6 样本)/距离淡出,已被方案 C、轨道云部分消化;**Light Volume、PlaceRays 为长期项** |
| [`Volken-水体系统大修可行性分析-2026-09-02.md`](Volken-水体系统大修可行性分析-2026-09-02.md) | **水体大修可行性**(路线 A~E) | 📋 评估完成,实施未排期 | 建议先 A/B 零风险调参(运行时改参/水下观感),E 整换 shader 为数周级终局;反射云(方案 A)已落地 |
| [`Volken-SP2天气系统移植BIRP计划-2026-09-27.md`](Volken-SP2天气系统移植BIRP计划-2026-09-27.md) | **SP2 天气系统移植(原生 BIRP)** | 🚧 实施中,**范围已缩减为「只做雷电」**(雨/雾实现已整体移除,配置保留占位待重做) | 保留:骨架(天气状态机)、**天气参数按预设名独立存(预设名与云层互相独立,`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`)**、闪电(C# 雷暴循环 + 程序化 bolt shader + 5 条雷声)、五分组天气面板、三语言文案。**已移除**:雨(compute + shader + `Rain.cs`)、雾(`FogRenderer` + `HeightFog.shader`)、雨声 —— stash 在 `%TEMP%\volken-rain-fog-stash`;**`WeatherTypes.cs`(SP2 全局天气档位)也已删除**(改为玩家逐项设置参数)。**§10.2/10.2b/10.2c0/10.2c2/10.2d 记录 30+ 处实测更正**;**§10.2e 是移除记录**;**§10.2f 是被取代的配置方案(勿照做)**;**§10.2g 是配置合并**;**§10.2h 是天气标度路线修正**;**§10.3 列剩余待真机确认项(打包清单、XML 往返)**;**复盘见 [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)** |
| [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md) | **雨/雾移植失败复盘(根因 + 铁律 + 诊断方法)** | ✅ 复盘完成(原状态:移植失败并移除) | 直接原因 = **雨丝长度轴表达错了空间**(世界空间长度轴 + 任意宽度轴 → 视角相关的"横块");真正原因 = **诊断量错对象**(用世界空间夹角当屏幕倾角)。**4 层根因 / 27 条铁律 / 8 条已证伪思路 / 量化基线**;重做雨/雾**前必读** |

---

## 二、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md`](archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md) | **方案 A:自带云区域内密度缩放** | ✅ 已实现归档(2026-09-06) | `stockDensityScale`(默认 1)只压区域内附加密度地板、`dist` 保足迹边缘;`scale<1` 把实心云拆成蓬松结构;翻案自阈值重映射(见 §9 历史) |
| [`archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md`](archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md) | **方案 B:游戏自带云 cubemap 作全球分布形状** | ✅ 已实现归档(2026-08-23) | `useStockCloudMap` 把 Clouds cubemap 接入 layers/shape 两处分布源;`stockMapStrength=0` 逐字节回退;缺层/无云星球逐带回退 |
| [`archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md`](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) | **方案 C:KSA 体积云技术移植(BIRP,时序超采样核心)** | ✅ 已实现归档(2026-08-24,2026-08-27 收工) | 低清全量 raymarch + 全清时序上采样 + 运动自适应;重投影 Y 镜像已修;坐标原点重置已修;JNO 冲突已定位 |
| [`archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md`](archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md) | **覆盖分解:biome × 旋转分布 × tiled detail** | ✅ 已实现归档(2026-08-27) | `coverage = cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 = 逐字节一致 |
| [`archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md`](archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md) | **轨道 2D 云 + 过渡带交叉淡入** | ✅ 已实现归档(M0~M3,2026-08-27) | `OrbitClouds` 壳求交 + 同源密度采样,`orbitFade` 交叉淡入;海拔分派:低空体积云 / 高空 2D 云;夜间晨昏线已修 |
| [`archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md`](archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md) | **割裂线排查(TSS 关 + 运动残影)** | ✅ 已修复归档(2026-08-25) | 根因 = 重投影矩阵用逻辑投影,与射线重建 clip 约定差 Y 翻转 → 历史镜像采样;修复:`GL.GetGPUProjectionMatrix` |
| [`archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md`](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md) | **JNO 联机 mod 冲突(NRE 中断 SceneLoaded 事件链)** | ✅ 已定位并修复归档(2026-08-27) | JNO `MultiPlayerUI.OnSceneLoaded` 对 null `inspectorPanel` 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用) |
| [`archive/Volken-实时反射适配分析-2026-09-02.md`](archive/Volken-实时反射适配分析-2026-09-02.md) | **实时反射适配分析**(水面平面反射 / 机体探头) | ✅ 分析完成,方案 A 已落地归档(2026-09-02) | 云只渲染在主相机 OnRenderImage;方案 A(水面反射合入云)已落地,方案 B(机体 cubemap 探头)后置未做 |

> ✅ 归档文档头部「状态:」为最终结论;文档内勾选项标记实际落地情况,未勾选项 = 待复跑/未排期项,按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策)

| 决策 | 结论 | 出处 |
|---|---|---|
| 方案编号体系 | 方案 A/B/C 同一套:方案 B = 自带云分布接入;方案 A = 自带云区域内密度缩放(前置 B);方案 C = KSA 时序超采样移植 | 各方案文档 |
| 自带云分布接入 | **✅ 方案 B 已实现**:游戏 Clouds cubemap 作全球分布形状,`stockMapStrength=0` 纯回退;缺层逐带回退 | [archive/Volken-方案B-…](archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md) |
| 自带云区域内密度 | **✅ 方案 A 已实现**:`stockDensityScale` 区域内密度缩放(默认 1 恒等);**翻案说明**:初版阈值重映射 `stockCoverage/stockSoft` 会收缩足迹边缘,弃用改为密度缩放(§9 历史,勿复用) | [archive/Volken-方案A-…](archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md) |
| 时序超采样 | **✅ 方案 C 已实现**:低清每帧全量 raymarch + 全清时序上采样(Upscale),运动自适应 `tssBlend`;单 `historyTex`(无 flip/flop),TSS 关走运动残影路径 | [archive/Volken-方案C-…](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) |
| 重投影修复 | **✅ 已修复**:`prevViewProjMat` 用 `GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)` 与射线重建 clip 约定对齐(修 fresh + 时序两路径) | [archive/Volken-割裂线排查记录-…](archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md) §4.5 |
| 坐标原点重置 | **✅ 已修复**:订阅 `IGameView.ReferenceFrameRecentered`,清空时序历史 + `frameNumber=0` 冷启动 | [archive/Volken-方案C-…](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) §12 |
| 覆盖分解 | **✅ 已实现**:覆盖 = `cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 | [archive/Volken-覆盖分解-…](archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md) |
| 轨道云 | **✅ 已实现**:海拔分派(低空体积云 / 高空 2D 壳着色)+ `orbitFade` 过渡带交叉淡入;2D 与体积云**同源密度采样**避免云形突变;夜晚侧晨昏线门控已修 | [archive/Volken-轨道云与过渡带交叉淡入-…](archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md) |
| 实时反射 | **✅ 方案 A(水面反射合入云)已落地**,方案 B(机体 cubemap 探头)后置;反射场景关 TSS、粗步长、低光样本 | [archive/Volken-实时反射适配分析-…](archive/Volken-实时反射适配分析-2026-09-02.md) §4 |
| JNO 冲突 | **✅ 根因定位**:JNO `OnSceneLoaded` NRE 中断事件链 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用);Volken 侧自愈曾加后撤除,回纯事件驱动 | [archive/Volken-冲突排查-…](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md) |
| **跨界移植方法论** | **⚠️ 通用铁律(雨/雾移植失败的沉淀)**:①细长 billboard 的**长度轴必须在屏幕平面内表达**,长度轴只由物理量决定、宽度轴由视线决定;②诊断必须量**你关心的那个量所在的轴**(屏幕问题量屏幕空间),并输出**当前值 + 内部状态**;③**每帧路径里禁止重置动画进度**,守卫只能读状态、只能比较目标值;④先标定数量级(密度 / 域半径 / 停留时长)再调观感;⑤同一问题连续 3 轮无可信改善信号 → 换方案,不许原地修 | [Volken-天气雨雾移植失败教训-2026-09-27.md](Volken-天气雨雾移植失败教训-2026-09-27.md) §5 |
| SP2 天气移植 | **🚧 实施中,范围已缩减为「只做雷电」**:雨(阶段 2)与雾(阶段 3)的实现**已整体移除**,准备重做;保留骨架 + 雷电 + 天气状态机。原生 BIRP,不引入 Enviro3;天气状态由 mod 持有(行星 `weather.xml`) | [Volken-SP2天气系统移植BIRP计划-2026-09-27.md](Volken-SP2天气系统移植BIRP计划-2026-09-27.md) §10.2e |
| ~~雨/雾实现~~ | **❌ 已移除(2026-09-27)**:`Rain.cs`/`RainParticles.compute(.shader)`/`FogRenderer.cs`/`HeightFog.shader`/`enviro_rain_1~3.ogg` 已 stash 到 `%TEMP%\volken-rain-fog-stash`,可整体取回。移除原因:雨丝朝向多轮未收敛。**重做前必读复盘** | [教训](Volken-天气雨雾移植失败教训-2026-09-27.md) / 同计划 §10.2e |
| 天气系统命名空间 | **✅ `VolkenMod.Weather`**(不能用 `Volken.Weather`:与全局类 `Volken` 冲突 → CS0101);新增天气文件必须沿用 | 计划 §10.2 ① |
| mod 资源加载 | **✅ 统一用 `Mod.LoadVolkenAsset<T>(path, required)`**(内部 `IModResourceLoader.LoadAsset<T>`);`Load<T>`/`LoadAudio` 属游戏 `IResourceLoader`,读不到 mod bundle | 计划 §10.2 ⑦ |
| 雾的深度来源 | **📦 历史结论(雾已删除)**:雾原本取 `CloudRenderer.combinedDepthTex`(经只读口 `LinearSceneDepth`,该属性**保留但已无消费者**);`DepthCapture.cs` 是死代码(其 `Hidden/DepthLinear` shader 在工程里不存在)。重做雾时这条仍成立 | 计划 §10.2 ② / §10.3 |
| 天气 UI 与联动 | **✅ 天气面板挂在 Volken 检查器内**(`WeatherPanel.cs`,不另开浮动窗口);云层联动三项增益**默认全 0 = 不碰云配置**(与项目"新增特性默认不改变现有画面"约定一致) | 计划 §10.2b ⑯ |
| 天气设置项 | **✅ Mod 设置里已无天气项** —— 两个「总闸」(`WeatherEnabled` / `ThunderEnabled`)已按用户要求移除,开关交给**逐行星的天气预设**(见下条) | 计划 §10.1 / §10.2e |
| **天气配置序列化** | **✅ 按预设名独立存,与云层同构但互不干扰** —— `<PlanetConfig>` 分别记**云预设名**与**天气预设名**(`CloudConfigName` / `WeatherConfigName`);参数本体在各自文件:`UserData/VolkenConfig/{行星}/{预设}.xml`(云)、`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`(天气)。天气面板可**独立新建 / 保存 / 读取**(「另存为新配置」+「加载配置」下拉),换天气预设不动云,反之亦然。类:`Assets/Scripts/Volken/Core/PlanetConfig.cs` + `Core/VolkenWeatherConfig.cs` | 计划 §10.2g ㊹(已被本次改动取代) |
| **【路线】不要 SP2 的全局天气预设** | **✅ 【决策】`WeatherTypes` 已删除** —— SP2 的 `WeatherTypes`(Clear/Few/Broken/Overcast/Rainy/Stormy/Heavy)是个**全局中间层**,由它统一决定云/雨/雾的预设与阈值。**Volken 刻意不要这一层**:云、雨、雾、雷的参数**全部由玩家逐项设置并序列化**,面板上就是直接的数值滑块。"天气值"仍存在(驱动随机/淡变与各子系统自己的触发阈值),但**不映射到任何档位**。阈值变成配置字段:`rain.triggerValue`(2.25)/ `lightning.stormValue`(2.5)/ `overall.maxWeatherValue`(3) | 计划 §10.2h |
| **档位标签可以留,但只能是显示** | **⚠️ `VolkenWeather.DescribeWeatherValue(float)`** 把连续值切成 `Clear/Few/Rainy/Stormy…` **纯用于日志/UI 文字**。**不要在它上面加任何逻辑**(`if (name == …)`)—— 那等于把 SP2 的中间层又建回来 | 计划 §10.2h ㊿ |
| **天气配置结构** | **✅ 按「5 个 Section」组织,XML 节点 = 面板分组 = 数据块,三者同名同序**:`Overall`(总体)/ `CloudLinkage`(云层联动)/ `Rain`(雨,占位)/ `Fog`(雾,占位)/ `Lightning`(雷)。每块是嵌套 `[Serializable]` 类 + 自带 `CopyFrom`;`CopyFrom` **逐块委派**而不是浅拷贝(嵌套实例是引用,浅拷贝会让两份配置共享同一个 Section 对象) | 计划 §10.2f ㊵ |
| **占位参数的处理** | **✅ 雨/雾/云层联动保留"完整占位"**:字段 + XML 节点 + 面板分组都在,但**面板整组 `ItemModel.Enabled = false` 禁用** + 灰字说明"已移除待重做"。**为什么禁用而不是隐藏**:结构完整可读,且玩家不会以为功能丢了。**改这些字段不会有任何效果**(没有消费者) | 计划 §10.2f ㊶ |
| **`VolkenWeather.Config` 的语义** | **⚠️ 它是清单里那条记录上的实例本身(引用,不是副本)** —— 面板一改就立刻改了清单内存对象,所以「重置为默认」必须显式实现(`CopyFrom(CreateDefault())` 就地写而**不是换引用**,换引用会让 `Config` 与清单脱钩)。落盘只发生在"点保存"或云层 `AddConfig/SetConfig` 时 | 计划 §10.2g ㊼ |
| **`AddConfig` 的隐藏 bug** | **✅ 已修**:原实现无条件 `configList.Add(new PlanetConfig(...))`,对已存在的行星会**追加第二条同行星记录**。云层调用点都被 `ExistsInConfig` 挡着所以没暴露;**天气内联进来之后这会让天气参数分叉成两份**(症状:"设置时不时自己变回去")。改为已有记录只更新层名 | 计划 §10.2g ㊽ |
| **旧 `PlanetConfigList.xml` 兼容** | **✅ 有显式兜底,而且会自愈**:老记录的 `<PlanetConfig … />` 是**自闭合标签**(属性-only)没有 `<Weather>` 节点 → `LoadFromFile` 里补一份默认(全关)+ `EnsureSections` + `ClampAll`(**不依赖"字段初始化器不被反序列化器重置"这个行为细节**)。而且 `Volken.OnSceneLoaded` 会调 `AddConfig` → 结尾 `SaveToFile`,所以**进一次场景文件就被重写成带 `<Weather>` 的形态** | 计划 §10.2g ㊾ |
| ~~天气独立配置目录~~ | **❌ 已废弃(仅存活一次改动)**:曾短暂改为 `UserData/VolkenWeatherConfig/` + `PlanetWeatherConfigList.xml` + 多预设。现**不创建、不读取**;若磁盘上有该目录可手删。**别再照 §10.2f ㊴ 实现** | 计划 §10.2g ㊺㊻ |
| 淡变/重入守恒 | **⚠️ 通用教训(雨已删,结论留用)**:任何"每帧都会调到的路径"里禁止出现重置动画进度的副作用;重入守卫只能比较**目标值**,不能比较当前值 —— 否则淡入每帧被重置,值恒 0。**守卫只能读状态,不能挡在"改状态的逻辑"前面** | [教训](Volken-天气雨雾移植失败教训-2026-09-27.md) §5.2 / 计划 §10.2b ㉑㉖㉚ |
| 天气与云的关系 | **✅ 【决策】天气系统不联动云层** —— 云厚度/覆盖度/浓度/颜色/风速全部由云自己的配置决定;曾有 `cloudCoverageGain` 等三个联动项,已连同 UI/本地化/配置字段一起移除 | 计划 §10.2b ㉔ |
| JNO 无风系统 | **✅ 已确认**:`WindManager`/`WindVelocity` 在 jnoCode 全仓库**零命中**;`IPlanetAtmosphereData` 只有气压/温度/成分,**无气流速度接口** → 雨若重做,风只能来自 `CloudConfig.windSpeed/windDirection` | 计划 §10.2c A |
| 雨重做的密度标定 | **📦 历史结论(雨已删除,重做时直接用)**:密度必须与域大小一起标定 —— EVE 的 `rain-Kerbin` 是 20 万粒子 / 半径 70m ≈ **0.139 个/m³**;域随相机速度自适应放大时必须补偿密度(指数 r^2.5)。"雨时有时无"的主因是**密度不足**,不是闪断。**密度与停留时长要一起算** | [教训](Volken-天气雨雾移植失败教训-2026-09-27.md) §5.1 / 计划 §10.2c2 |
| 雨重做的方向铁律 | **📦 历史结论(雨已删除,重做时直接用)**:①细长 billboard 的**长度轴必须在屏幕平面内表达**(世界空间长度轴 + 任意宽度轴 → 视线接近长度轴时透视压成 0 → 屏幕上是"横块");②**长度轴只由物理量决定,宽度轴由视线决定**,混用必错;③沿视线的方向分量对屏幕方向**贡献恒为 0**(会造成径向爆散);④诊断必须量**屏幕空间**角度,世界空间夹角会误导多轮。**可行算法骨架见教训 §3.1 ㈢** | [教训](Volken-天气雨雾移植失败教训-2026-09-27.md) §3.1 / 计划 §10.2d F~I |
| 两个速度不能混 | **📦 历史结论(雨已删除)**:雨丝朝向用"减玩家速度",粒子在域内平移用"减相机速度" —— 混成一个量是多次返工的根源;`Camera.velocity` 不可当玩家速度用 | [教训](Volken-天气雨雾移植失败教训-2026-09-27.md) §5.1 ⑥ / 计划 §10.2c D/I |
| 雨重做的域尺寸 | **📦 历史结论(雨已删除)**:相机速度必须 < 域半径,否则粒子每帧穿过全域 → 雨幕闪断;位移需硬钳到半域/帧 | 计划 §10.2c E |
| 雨重做的坐标系 | **📦 历史结论(雨已删除)**:用**相机相对空间 + 手工构造视锥平面**;**不要**混用 `GeometryUtility.CalculateFrustumPlanes`(它给参考系空间 → 剔除整体错位) | 计划 §10.2b ⑨ |
| GPU 绘制自检手段 | **✅ 用 GPU 回读日志判定"到底画没画"**(雨当年是 `VolkenRain:GPU`,每 10s 回读间接绘制参数 buffer 的 instanceCount),可把"剔除/参数环节"与"绘制/着色环节"一次分开,不必靠肉眼猜 | 计划 §10.4 ④ |
| SP2 雨滴 shader | **⚠️ 提取工程里的文件是 `DummyShaderTextExporter` 占位模板**,只有 Properties 块可信(`_MainTex/_Emission/_MainColor/_InvFade`);不能反推实现 | 计划 §10.2b ⑳ |
| 风的坐标系 | **📦 历史结论(雨已删除)**:不能用 `FlightData.North/East`(飞船局部,随姿态转) —— 必须在行星位置坐标系里解析构造地理北/东;风量级钳到 25 m/s | 计划 §10.2c B/C |
| 调试开关 | **⚠️ 全局调试开关必须是 `static`** —— 实例字段时 dev 命令写的实例可能与实际绘制的实例不同 → "开了等于没开"(`_activeInstance` 同源坑) | 计划 §10.2c J |
| 多相机共享材质 | **⚠️ 材质 uniform 是全局的,多相机共享同一个 Material 实例** → 每个相机绘制前必须重设基向量/相机相关 uniform,否则后一个相机覆盖前一个 | 计划 §10.2c G |
| mod 打包清单 | **⚠️ 资产清单是 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表)**,ModTools 构建时据此写 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`;新资产不会自动入包,**删掉资产文件也不会让 GUID 自动消失**(实测删除后重建的 manifest 仍带旧路径)。**增删资产都要改 `_otherAssets` 并核对无悬空 GUID**。玩家侧看资源台账用 dev 命令 `volkenAssets` | 计划 §10.2e / §10.3 1 |
| shader 验证手段 | **✅ `Editor.log` 里的 `Shader error in '<名>': … (on d3d11)`** 是唯一能离线抓到 shader 错误的途径;**Unity 的 HLSL 没有 `expm1`**(本轮实测踩到);未被场景引用的 shader 不会被编译(Rain 当年即如此,只能等游戏内验证)。**错误行按消息去重后再统计次数**,同一错误会 print 上千遍 | 计划 §10.2b ⑭ / §10.4 ② |

> 上表「计划」= [`Volken-SP2天气系统移植BIRP计划-2026-09-27.md`](Volken-SP2天气系统移植BIRP计划-2026-09-27.md);「教训」= [`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)。

**当前待定(尚未拍板/未调研)**:
- **水体大修实施顺序**:评估完成(A/B 先行),未排期([Volken-水体系统大修可行性分析-2026-09-02.md](Volken-水体系统大修可行性分析-2026-09-02.md))。
- **优化点剩余项**:Light Volume(#11,长期)、PlaceRays(#10,长期)、噪声 mipmap/密度 LUT(#4/#5)、HDR RT(#6)、MV 4 次膨胀(#9)等,见 [Volken-体积云优化点分析-…](Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md)。
- **方案 C 已知缺口**:N/S 风(非刚体 Y 旋转,云空间重投影近似未覆盖)、flip/flop 双缓冲(单缓冲当前可用)、TSS 开时 !isFresh 硬回退闪烁待用户实测确认。
- **to-do 高优先**:切换至有大气星球时 config 卡住(未知原因,等复现和 log)。

---

## 三之二、当前代码里的已知问题(复核)

> 这一节记录**已核实、但还没动手修**的问题,供下次开工直接取用。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | **切换至有大气星球时 config 卡住**(高优先) | `to-do.md`;未知原因,等待更多复现和 log | 进有大气 SOI 时配置卡死,需复现后定位 |
| 2 | **星环渲染顺序错误**(低优先) | `to-do.md` | 星环相对云/水体层级错误 |
| 3 | **Craft 在高轨道时云层 scale 错误**(低优先) | `to-do.md` | 高轨道下云的缩放不符合预期 |
| 4 | **N/S 风重投影缺口**:云空间重投影只覆盖绕 Y 自转 + 东西风平移 | 方案 C §5 已知局限 / 割裂线 §7 遗留 | 强南北风时历史重投影失效 → 残影/鬼影;需完整 worldToCloud 矩阵 |
| 5 | **`CloudConfig.low/mid/highAltitudeThreshold` 死配置**(只定义+序列化,零消费) | 轨道云文档 §2/§4.3 | 与 KSA 的 start/end 一对设计不对应;**不可删除**(旧 XML 含节点,删除会导致 XmlSerializer 反序列化失败回退默认配置),仅弃用 |

---

## 四、文档写入规则(维护约定)

> 适用:所有 `docs/**` 下的 `.md`;`README.md` / `AGENT_CONTEXT.md` 本身也遵守 0 命名与 8 编码规则。
> 本规则仿照 JNO 联机 mod 的 `plans/` 目录约定(见 JNO `plans/README.md` §四)适配而来。

### 0. 命名规范
- 方案/排查/分析文档:`<主题>-YYYY-MM-DD.md`(中文主题 + 日期后缀;日期 = 创建/事件日期,补零)。**禁止无日期名**(历史教训:早期文档无日期,游离在索引外)。
- 索引/参考文档(`README.md`、`AGENT_CONTEXT.md`)不加日期;待办清单 `to-do.md` 作为活跃 backlog 也不加日期。
- 单一主题一个文件;已完成文档移入 `archive/` 子目录,文件名规则不变。

### 1. 状态与单一事实源
- **文档头部 blockquote 的「状态:」是唯一事实源**;README 索引行、决策速查表、待定段、AGENT_CONTEXT 里的进度都只是它的镜像。
- 状态词汇受控,禁止自造:📋 规划中 / 研究 / 评估中 → ✅ 已实现 / 已落地 → ✅ 已归档。同一主题同一时刻只能有一个状态。
- **改文档头部状态时,必须同一次改动里同步更新**:README 索引行(§一↔§二 分区移动)、决策速查表、待定段,以及 AGENT_CONTEXT 相关行。

### 2. 结构模板(新建文档建议)
- 头部 blockquote:`状态:` / `日期:`(或 `创建日期:`) / `关联:`(相关文档链接 + 一句关系说明) / 一句话主题定位。
- 正文顺序:动机 → 现状 → 方案 / 决策 → 实施记录(按日期分小节)→ 剩余项 / 回归判据。
- 结论先行;被取代的方案要标注「已被取代,保留作决策记录」(如方案 A §9 历史设计)。

### 3. 决策记录
- 格式:「【决策:YYYY-MM-DD】」;明确结论写进对应文档,并汇总到 README「决策速查」表(出处列 = 文档链接 + 小节号)。
- 翻案决策:保留旧记录,加「翻案说明」链接到新决策(如方案 A:阈值重映射 → 密度缩放)。

### 4. 交叉链接
- 一律相对路径;同目录内互链用裸文件名 `x.md`。
- root 引用归档:`archive/x.md`;归档引用 root:`../x.md`;**归档内互链:裸文件名(不要加 `../`)**。
- **移动 / 归档一个文档后,必须全仓库 grep 修正所有指向它的链接**(含文档正文与 README),并跑一次死链校验。
- 站内不写本机绝对路径(一律用令牌,见 §10「隐私红线」);源码引用用工程相对路径 `Assets/Scripts/...`。

### 5. 归档流程(完成一个主题后)
1. 头部状态改「✅ 已归档(原状态:…)」;
2. 未排期剩余项 / 待复跑项:文档内写明,并同步进 README「当前待定」段;
3. `git mv` 移入 `archive/`(保留历史)→ 按 §4 修正全部链接;
4. 更新 README:从 §一 移到 §二,同步决策速查表出处列;
5. 归档 ≠ 删除:仍被索引引用,按摘要可随时找回。

### 6. 已知问题与修复记录
- 已核实未修的问题 → 登记进 README「三之二」表(问题 / 证据 / 影响;证据给 `文件:行号` + 反编译出处)。
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
- [ ] 头部「状态:」与 README 索引行一致(§一/§二 分区正确)
- [ ] 新决策已进 README「决策速查」表(出处带链接)
- [ ] 归档文档已从 §一 移入 §二,且全仓库无指向旧位置的链接
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

- 引用反编译源码时,保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),**不要**写本机路径形式的站外链接。
- **上传前**:把文档内 `<TOKEN>` 之外的本机路径全部替换为令牌;`LOCAL_PATHS.md` 已被 `.gitignore` 排除,不会误提交。
