# 天气系统现状

> 核对日期：2026-10-14。雨、雾、闪电和雷声均已接入；历史验证与游戏验收分别见 [开发与验证](development.md#已有验证证据) 和 [待办](backlog.md#游戏验收)。

## 配置与入口

配置类为 [VolkenWeatherConfig](../Assets/Scripts/Volken/Weather/VolkenWeatherConfig.cs)，驱动为 [VolkenWeather](../Assets/Scripts/Volken/Weather/VolkenWeather.cs)，面板为 [WeatherPanel](../Assets/Scripts/Volken/Weather/WeatherPanel.cs)。雨、雾、雷电默认关闭，按独立 `enabled` 和大气 / 场景条件运行；空 `OverallSection` 仅用于旧 XML 兼容。

天气运行要求当前行星快照支持大气效果且与所用预设的行星一致；无大气、恒星、缺行星数据和离场均抑制运行，不改用户 `enabled`。雨的挂载、参数应用、toggle、每帧同步与绘制共用此门控，主视图 / PIP 一并停雨并清掉旧水花历史，雨声门控关闭；雷电排程和镜头水珠随环境停用，手动落雷也不能绕过。回到有大气行星按其预设自动恢复；显式 `StandaloneMode` 保留独立预览。跨场景 SOI 订阅归属见 [架构](architecture.md#生命周期与模块边界)。

## 预设运行态同步

【决策：2026-10-14】加载时直接应用配置，不依赖手动 toggle 补同步。

- `ApplyPreset` 替换 `Config` 后调用 `RainParticles.ApplyConfig` 与 `SyncToCurrentView`，立即同步开关、容量、几何、雨声输入和所有存活相机实例；相机暂缺时由正常 tick 重试挂载。
- 启用中的雷电切预设通过 `refreshSchedule=true` 停止旧等待并抽取新间隔，只重排下一次落雷，保留正在播放的闪电及待播雷声。
- 普通状态刷新不反复重排；另存为只保存名称 / 文件，同名切换和缺失文件仍被拒绝。恢复默认同样应用雨参数和雷电启停。
- `PlanetConfigLoaded` 在配置与运行态一致后发出。跨场景加载 / 保存语义见 [架构](architecture.md#配置与预设)。

修复前游戏日志曾出现已加载 `rain=True`、运行仍 `rainEnabled=False`，以及保存 amount=100000、运行仍为 50000；这是遗漏雨参数应用的证据。雷电切换后等待旧间隔不等于永久停雷。

## 雨粒子、朝向与声音

| 入口 | 职责 |
|---|---|
| [RainParticles.cs](../Assets/Scripts/Volken/Weather/Rain/RainParticles.cs) | 每相机实例、参数应用、compute 与间接绘制 |
| [RainParticles.compute](../Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.compute) | Randomize / Positioning / FillArgs / TranslateFixed |
| [RainParticles.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.shader) | 十字雨丝、透明度、光照、软深度与雾 |
| [RainAudio.cs](../Assets/Scripts/Volken/Weather/Rain/RainAudio.cs) | 常驻雨声、强弱混音、环境淡出与音量 |

雨配置默认 100000 粒、半径 50m、落速 15m/s、长度 2.5m、宽度 0.1m，容量上限 400000。`RainParticles.DefaultCapacity=10000` 是配置应用前的静态初值，不能当作实际天气默认雨量。密度为 `amount / (4πR³/3)`，10 万粒 / 50m 约 0.191/m³。

粒子在参考帧空间下落，雨丝沿世界 / 帧空间 `_RotationMatrix` 构轴，流线模式用 **`down * FallSpeed - axisVel`**，保持飞行器速度真实符号；不用相机绕飞速度替代飞行器速度，不写死世界 -Y。`TranslateFixed` 处理重定位。随机重生使用 `id + phase` 的整数 hash，避免大数 float32 `frac` hash 精度塌缩。

自适应雨域只增半径，不在 `OnPreCull` 自动重建 buffer 补密度，域变大时会变稀。容量 / 几何修改由 `ApplyConfig` 向全部实例更新。compute、材质、buffer、碰撞资源与就绪状态按实例持有；共享诊断及环境淡出仅主视图写，避免 PIP 覆盖雨声。

`rain.transparency` 范围 0~1：0 保留原 alpha，1 完全透明，只影响雨滴 / 水花视觉，不改变雨声或共享环境淡出。雨限高和水下淡出看雨自身配置，不读 `maxCloudHeight`。

【决策：2026-10-14】雨滴先接受游戏环境 / 太阳光，再加 `brightness` 自发光。0 表示无独立自发光；颜色空间复用 `FogLighting.WorkingColor`，不设环境光下限。太阳项消费启用状态、强度、RGB、alpha 中的行星遮挡和局部径向高度。未采样地形 / 云影或局部灯光。

首轮把入射光乘法扩展到水花，用户反馈溅落消失，**两个水花 shader 及 `RainCollision.Draw` 光照参数已回退**。水花保留原 RGB / 雾显示路径，自发光滑块只控制雨滴，不能把水花也写成当前已接受新雨滴光照。

## 镜头水珠

[RainLensEffect.cs](../Assets/Scripts/Volken/Weather/Rain/RainLensEffect.cs) 只挂游戏 NearCamera,通过普通 `OnRenderImage` 在透明物体之后折射场景,保留源 alpha;不进入云 / 雾合成或共享雨诊断。独立 `<RainLens>` 配置默认关闭,旧 XML 保持关闭,面板位于雨组内;预设加载 / 默认重置清池,相机替换、离场或关闭释放实例渲染资源。新 [RainLens.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainLens.shader) 已登记 `_otherAssets`,仍需重新打包才能进入游戏。

[RainLensSimulation.cs](../Assets/Scripts/Volken/Weather/Rain/RainLensSimulation.cs) 使用 128 槽固定池,尺寸 / 速度按屏幕高度标定,支持附着、滑动、短拖痕和寿命消退。生成要求雨开启,抬头点积或高速连续增加入射量;速度使用补偿参考帧的 `CraftScript.SurfaceVelocity`,迎雨更多、背向减少。密度由雨量 / 域体积映射,不读雨声音量、雨丝透明度或云高度。暂停冻结,停止下雨或转离触发方向后只消退残留;限高 / 水下沿用雨配置并调制显示。

水珠遮罩先恢复按覆盖度混合后的法线 / 高度,再施加最终淡出;较宽的明暗边缘使均匀天空上也能辨认水珠,高光只借用局部场景亮度,黑背景无独立发光。拖痕半宽为水珠半径的 0.40、覆盖倍率为 0.65。默认生成率 18 滴/秒、半径为屏幕高度的 1.8%、折射 14px、模糊 1.8px;加载时只在这四项全部保持首版默认 14 / 1.2% / 8 / 1.2 时整体升级,任一自定义则保留该组,不改开关。

遮雨开关默认开启,每 0.15 秒从来雨方向 60m 向镜头检测物理层 26 / 29 / 31 的非 Trigger Collider,也可检测包围镜头的机体外壳;缺 Collider、超过检测距离或特殊层会漏遮挡。遮罩按源宽高比缩到最多 1024×512,两次 pass 为水珠遮罩绘制和局部折射 / 模糊;无水珠不分配遮罩。当前不合并水珠、不模拟连续水膜、不限制到挡风玻璃区域,也不挂 PIP;游戏后处理 / 原生水下效果顺序和实际耗时见 [待验收](backlog.md#游戏验收)。

## 雨碰撞与水花

[RainCollision.cs](../Assets/Scripts/Volken/Weather/Rain/RainCollision.cs) 使用每相机正交深度和 GPU 位移段检测，水面另做解析海平面求交。软粒子读取 `CloudRenderer.LinearSceneDepth`，碰撞读取专用正交图，两种深度来源不能互换。

- 碰撞 / 水花默认关闭；面板开启水花会开启碰撞。深度分辨率为 256 / 512。
- 捕获先按 `RenderType` 处理其余层，再对物理层 26 / 29 / 31 使用空 replacement tag，最多两次渲染。原生地形 / 建筑 / 机体 shader 缺少 `RenderType`，不能合回单次 tagged 捕获；导出 DummyShader 的标签不能当原始资产证据。
- GPU 水花池每相机 2048 槽，每帧最多 128 次发射请求获准尝试入池；默认作用距离 25m。地面命中和水面命中互斥，关停 / 重定位清池，不创建逐滴 Collider / Rigidbody。
- [RainSplashes.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainSplashes.shader) 处理地面；[RainWaterSplashes.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainWaterSplashes.shader) 用双 quad 绘制水平双涟漪、竖直冠状飞溅和液滴，沿命中法线构轴。
- 水面线性场景深度可用时用片元 eye-depth 和 4cm 软边检查遮挡，避免原水面波峰写硬件深度挡掉解析平面水花；缺少该深度时回退 `LessEqual`。新增一次水面间接绘制，没有新增捕获相机。

单层深度不能完整表达桥下 / 洞穴 / 多层结构，低分辨率可能漏细杆 / 机翼；水花不绑定移动机体，残留漂移仍待评估。PIP 使用玩家飞行器采样，观察其他远处飞行器时环境门控存在边界。

## 径向薄雾与流动体积雾

| 入口 | 职责 |
|---|---|
| [FogRenderer.cs](../Assets/Scripts/Volken/Weather/Fog/FogRenderer.cs) | 每个 CloudRenderer 持有的独立雾资源、准备 / 合成 / 日志 |
| [FogLighting.cs](../Assets/Scripts/Volken/Weather/Fog/FogLighting.cs) | 颜色空间、环境与太阳散射计算 |
| [FogCommon.cginc](../Assets/Scripts/Volken/Weather/Fog/Shader/FogCommon.cginc) | 背景、云、自有透明物体共用查询 |
| [HeightFog.shader](../Assets/Scripts/Volken/Weather/Fog/Shader/HeightFog.shader) / [VolumetricFog.compute](../Assets/Scripts/Volken/Weather/Fog/Shader/VolumetricFog.compute) | 薄雾查询纹理与体积列积分 |

薄雾按双精度行星坐标求径向海拔，基准以下恒定密度、以上指数衰减，实际尺度 `H=height/heightFalloff`。薄雾单独运行使用 **513×257 RFloat 光学厚度查询纹理**，每相机约 0.503MiB；解析积分保留作生成和兼容回退。

体积雾使用每相机累计 3D RT，噪声固定行星坐标，CPU 双精度取周期余数，风使用排除暂停的时间。同开时合并密度一次积分；两类纹理互斥持有。体积无时序历史，低档可能有网格 / Z 分段。雾从纹理 UV 重建射线，使用 `GL.GetGPUProjectionMatrix(..., false)`，不能照云的 `true` 一刀切。

环境 RGB 与太阳 RGB 独立，无固定 0.03 环境下限。Linear 项目先转换输入色；太阳消费 enabled、intensity、RGB 和遮挡，环境光尊重游戏昼夜结果，不再额外统一压暗。黑环境无太阳时散射为零，密度对应的透射衰减仍在。

雾受场景、大气、水下、范围、零密度 / 不透明度门控，正交相机不支持。自有雨、水花、闪电已接入相机匹配的雾查询；原生透明水、玻璃、折射和反射雾尚未适配。云雾使用首个云命中 / 壳面距离近似，不是云内逐步联合积分。

雾滑块由下一帧读取，不反复刷新雷电启停；雾字段随天气预设 XML 一起保存。`SaveToFile` / `SaveCurrentConfig` 返回实际保存结果，完整写入临时文件后替换旧预设；写入失败保留旧文件、输出不受 DevMode 限制的错误，面板显示失败。另存为只有写入成功才更新预设名 / 列表 / 行星登记，避免显示成功却未落盘。默认每 5 秒记录雾状态 / 深度 / 光照 / 帧时间，`fog.diagnostics=false` 可关摘要；GPU 回读仅显式开发请求，游戏面板没有诊断入口。

## 闪电与雷声

[LightningModule.cs](../Assets/Scripts/Volken/Weather/Lightning/LightningModule.cs) 管理落雷、限高和雷声排程，[LightningBolt.cs](../Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs) 管理动画、分叉、闪光、暂停及清理，[GamePause.cs](../Assets/Scripts/Volken/Weather/GamePause.cs) 提供按帧缓存的暂停 / 时间。

- 分叉第 0 点显式 `SetPosition(0, from)`；整道雷共享独立分叉材质，由拥有者在分叉销毁后释放，避免主干销毁后变品红。网格和渐变静态共用。
- 八个亮灭时间点完整初始化，首个亮脉冲同帧生效；真实 Unity Light 在 `BeginAnimation` 应用最终配置。落点球形闪光为 `flashIntensity × 0.2`，云内雷光保持独立。
- 雷限高按观察者海拔和雷自身 `ceilingAltitude`，默认 12000m，0 关闭闸门。停用 / 离场停止协程、雷声与在播闪电，暂停冻结动画和排程，恢复处理 DSP 时间。
- 雷声 near / far 按观察者距离选素材；声程可混合落点与云底最短距离。声速从同一观察者位置的游戏 `AtmosphereData.SampleAltitude(...).SpeedOfSound` 取得；无有效正声速时不新排雷声。
- **兜底声速字段 / UI 已删除**。即使选择固定延迟模式，当前代码也先要求有效大气样本；不自行填常量声速。使用多 AudioSource 和 `PlayScheduled`，避免新雷声覆盖旧排程。

尚未处理跨大气声程积分、水下介质、镜头进入真空后的旧排程取消、落点贴地形和联机一致性。云内雷光的渲染契约见 [云](clouds.md#雷光与水面反射)。
