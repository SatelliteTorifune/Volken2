# 天气系统现状

> 核对日期：2026-10-14。当前行为见下文，验证 / 验收见 [开发](development.md#已有验证证据) / [待办](backlog.md#游戏验收)，地区方案见 [设计](weather-regions.md)。

## 配置与入口

配置类为 [VolkenWeatherConfig](../Assets/Scripts/Volken/Weather/VolkenWeatherConfig.cs)，驱动为 [VolkenWeather](../Assets/Scripts/Volken/Weather/VolkenWeather.cs)，面板为 [WeatherUserInterface](../Assets/Scripts/Volken/Weather/WeatherUserInterface.cs)。雨、雾、雷电默认关闭，按独立 `enabled` 和大气 / 场景条件运行；空 `OverallSection` 仅用于旧 XML 兼容。

运行要求行星快照支持大气效果且匹配天气预设。无大气、恒星、缺数据或离场时，雨的挂载 / 应用 / toggle / 同步 / 绘制统一停用，主视图 / PIP 清水花，雨声、雷电排程与水珠停用，手动落雷不可绕过；不改用户开关，返回有大气行星按预设恢复。`StandaloneMode` 保留独立预览；SOI 归属见 [架构](architecture.md#生命周期与模块边界)。

## 预设运行态同步

- `ApplyPreset` 替换 `Config` 后调用 `RainParticles.ApplyConfig` / `SyncToCurrentView`，同步开关、容量、几何、雨声与全部相机；缺相机由 tick 重试，不依赖手动 toggle。
- 启用中切雷电预设用 `refreshSchedule=true` 重抽下一次间隔，保留在播闪电 / 待播雷声；普通刷新不重排。恢复默认同步雨参数与雷电启停。
- `PlanetConfigLoaded` 在运行态同步后发出；另存为及跨场景语义统一见 [配置契约](architecture.md#配置与预设)。

## 地区分布

[RegionalWeather.cs](../Assets/Scripts/Volken/Weather/RegionalWeather.cs) 提供无状态球面采样。`VolkenWeather.SampleRegionalWeather` 将每相机 / 落雷候选的帧位置经 `FrameToPlanetPosition`、`PlanetVectorToSurfaceVector` 转为双精度固连坐标，按海平面半径归一化；行星名与 seed 确定噪声，`FlightState.Time` 决定平流。暂停不推进天气，重定位不改变地理分布；无坐标缓存。无效输入返回恒等并按故障段记录诊断，环境门控仍独立生效。

XML `<Regions>` 默认 / 缺节点为关闭，三语面板位于天气根组。`distribution=0/1` 选程序化 / 贴图，旧 XML 默认 0；`mapLongitudeOffset=0`（−180~180°，正值向东）。其余默认 / 范围：`seed=1337`（XML int，UI 0~999999）；`coverage=0.5`、`stormFraction=0.3`（0~1）；`cellSizeKm=150`（1~2000km）；`edgeSoftness=0.2`（0.01~0.5）；`windSpeed=20`（−200~200m/s，赤道东向即绕 −Y，局部速度乘 cos 纬度）。格距不是区域半径；覆盖调阈值，不是面积比例，0 / 1 为全干 / 全雨；雷暴比例 0 / 1 为无雷 / 全雨区可落雷。

程序化模式用两级三维值噪声；贴图模式用 [RegionalCoverage.png](../Assets/Scripts/Volken/Weather/Textures/RegionalCoverage.png) 的 R 线性灰度，同用覆盖阈值 / 柔和度与平流。图为用户原始 1024×512 文件，北在上；`lon=-atan2(x,z)`，`u=lon/2π+0.5-offset/360`、`v=lat/π+0.5`，双线性横向循环，极点半像素收敛到边缘行均值。[RegionalWeatherMap](../Assets/Scripts/Volken/Weather/RegionalWeatherMap.cs) 缓存不可变 0.5MiB CPU 灰度；纹理可读、非 sRGB、无 mip / 压缩，已登记 `_otherAssets`。天气驱动首次使用时读取，失败限频诊断、30秒重试并回退程序化，不改所选模式。

两种来源都以独立类型噪声乘雨倍率得到 Storm；贴图覆盖整球，不随格距或种子缩放，格距 / 种子仍控制雷暴类型分布。当前只返回水平 Rain / Storm，高度由各效果限高控制。雨按相机倍率改有效数量，保留容量和模拟；水花 / 水珠停新生后残留按寿命消退。雨声独立查主 NearCamera，倍率控制混音与淡出；PIP 不回写。切来源不重建雨池或重排雷电；远景 / 雾 / 云边界见 [地区设计](weather-regions.md#效果接入边界)。

## 雨粒子、朝向与声音

| 入口 | 职责 |
|---|---|
| [RainParticles.cs](../Assets/Scripts/Volken/Weather/Rain/RainParticles.cs) | 每相机实例、参数应用、compute 与间接绘制 |
| [RainParticles.compute](../Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.compute) | Randomize / Positioning / TranslateFixed |
| [RainParticles.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.shader) | 十字雨丝、透明度、光照、软深度与雾 |
| [RainAudio.cs](../Assets/Scripts/Volken/Weather/Rain/RainAudio.cs) | 常驻雨声、强弱混音、环境淡出与音量 |

默认 100000 粒、半径 50m、落速 15m/s、长 2.5m、宽 0.1m，上限 400000 粒。`DefaultCapacity=10000` 仅为应用配置前初值。密度 `amount / (4πR³/3)`，默认约 0.191/m³。

粒子在参考帧下落，雨丝由世界 / 帧空间 `_RotationMatrix` 构轴；流线用 **`down * FallSpeed - axisVel`**，不以相机绕飞速度代替飞行器速度或写死世界 -Y。`TranslateFixed` 处理重定位；重生用 `id + phase` 整数 hash，避免大数 float32 `frac` 精度塌缩。

自适应雨域只增半径、密度下降，不在 `OnPreCull` 重建 buffer 补密度。`ApplyConfig` 更新全部实例容量 / 几何；compute、材质、buffer、碰撞与就绪状态按实例持有。CPU 仅有效数量变化或重建后上传间接数量，稳定数量不上传、不清零、不做 GPU 计数；暂无可见性剔除，新增 GPU 可见列表须恢复计数契约。共享诊断 / 环境淡出仅主视图写，防 PIP 覆盖雨声。

`rain.transparency` 范围 0~1：0 保留原 alpha，1 完全透明，只影响雨滴 / 水花视觉，不改变雨声或共享环境淡出。雨限高和水下淡出看雨自身配置，不读 `maxCloudHeight`。

雨滴接受环境 / 太阳光后加 `brightness` 自发光，0 为无自发光；颜色复用 `FogLighting.WorkingColor`，无环境下限。太阳项消费 enabled、强度、RGB、alpha 行星遮挡与径向高度，以及 [云遮蔽](clouds.md#阳光遮蔽) 的本相机直射 / 环境倍率；无逐滴云影、地形遮蔽或局部灯光。

水花保留原 RGB / 雾路径，自发光仅控制雨滴。禁止对水花重加雨滴入射光乘法：该做法曾使溅落消失，两个水花 shader 与 `RainCollision.Draw` 光照参数均已回退。

## 镜头水珠

[RainLensEffect.cs](../Assets/Scripts/Volken/Weather/Rain/RainLensEffect.cs) 只挂 NearCamera，普通 `OnRenderImage` 在透明物体后折射，保留源 alpha，不参与云 / 雾合成或共享雨诊断。雨组 `<RainLens>` 默认 / 旧 XML 关闭；加载预设 / 重置清池，换相机、离场或关闭释放资源。[RainLens.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainLens.shader) 已登记 `_otherAssets`，入游戏仍需打包。

[RainLensSimulation.cs](../Assets/Scripts/Volken/Weather/Rain/RainLensSimulation.cs) 为 128 槽池，尺寸 / 速度按屏幕高度标定，支持附着、滑动、短拖痕、寿命。雨开启后抬头点积 / 高速增加入射；速度取补偿参考帧的 `CraftScript.SurfaceVelocity`，迎雨增加、背向减少。密度取雨量 / 域体积，不读音量、透明度或云高；暂停冻结，停雨 / 转离只消退残留，限高 / 水下沿用雨配置。

遮罩恢复按覆盖混合的法线 / 高度后再淡出；宽明暗边缘增强天空可见性，高光借场景亮度，黑背景不发光。拖痕半宽为半径 ×0.40，覆盖倍率 0.65。默认 18 滴/秒、半径屏高 1.8%、折射 14px、模糊 1.8px；仅四项全为旧默认 14 / 1.2% / 8 / 1.2 时整体升级，任一自定义保留整组，不改开关。

遮雨默认开，每 0.15 秒从来雨方向 60m 检测至镜头，取层 26 / 29 / 31 的非 Trigger Collider，含包围镜头的机壳；缺 Collider、超距或特殊层会漏挡。遮罩等比缩至最多 1024×512，两 pass 画遮罩 / 折射模糊，无水珠不分配。暂无合并、水膜、玻璃区域限制或 PIP；后处理 / 原生水下顺序与耗时见 [待验收](backlog.md#游戏验收)。

## 雨碰撞与水花

[RainCollision.cs](../Assets/Scripts/Volken/Weather/Rain/RainCollision.cs) 用每相机正交深度 / GPU 位移段检测，水面解析求交海平面；碰撞正交图不可与软粒子的 `CloudRenderer.LinearSceneDepth` 互换。

- 碰撞 / 水花默认关闭；面板开启水花会开启碰撞。深度分辨率为 256 / 512。
- 先 tagged 捕获其余层，再以空 replacement tag 捕获层 26 / 29 / 31，最多两次。原生地形 / 建筑 / 机体缺 `RenderType`，不能合为单次 tagged；DummyShader 标签不代表原资产。
- 每相机 2048 槽 GPU 水花池，每帧最多 128 次请求尝试入池，默认距离 25m；地面 / 水面命中互斥，关停 / 重定位清池，无逐滴 Collider / Rigidbody。
- [RainSplashes.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainSplashes.shader) 处理地面；[RainWaterSplashes.shader](../Assets/Scripts/Volken/Weather/Rain/Shader/RainWaterSplashes.shader) 用双 quad 绘制水平双涟漪、竖直冠状飞溅和液滴，沿命中法线构轴。
- 水面用线性场景 eye-depth / 4cm 软边检查遮挡，防波峰硬件深度挡住解析平面水花；缺深度回退 `LessEqual`。水面增加一次间接绘制，无新捕获相机。

单层图不完整表达桥洞 / 多层结构，低分辨率漏细杆 / 机翼；水花未绑定移动机体，残留漂移待评估。PIP 仍取玩家 craft 样本，远处其他机体环境可能不匹配。

## 径向薄雾与流动体积雾

| 入口 | 职责 |
|---|---|
| [FogRenderer.cs](../Assets/Scripts/Volken/Weather/Fog/FogRenderer.cs) | 每个 CloudRenderer 持有的独立雾资源、准备与合成 |
| [FogLighting.cs](../Assets/Scripts/Volken/Weather/Fog/FogLighting.cs) | 颜色空间、环境与太阳散射计算 |
| [FogCommon.cginc](../Assets/Scripts/Volken/Weather/Fog/Shader/FogCommon.cginc) | 背景、云、自有透明物体共用查询 |
| [HeightFog.shader](../Assets/Scripts/Volken/Weather/Fog/Shader/HeightFog.shader) / [VolumetricFog.compute](../Assets/Scripts/Volken/Weather/Fog/Shader/VolumetricFog.compute) | 薄雾查询纹理与体积列积分 |

薄雾以双精度行星坐标求径向海拔，基准下恒密度、上指数衰减，`H=height/heightFalloff`；单独运行用 **513×257 RFloat 光学厚度纹理**（约 0.503MiB / 相机），解析积分用于生成与回退。

体积雾用每相机累计 3D RT；行星固定噪声、CPU 双精度周期余数，风时钟排除暂停。同开合并密度一次积分，两类纹理互斥。无时序历史，低档可能网格 / Z 分段；从 UV 重建射线，`GL.GetGPUProjectionMatrix(..., false)` 不可改成云的 `true`。

环境 / 太阳 RGB 独立、无 0.03 下限；Linear 先转输入色。太阳消费 enabled、intensity、RGB 与遮挡，环境遵守游戏昼夜；各取 [云遮蔽](clouds.md#阳光遮蔽) 本相机倍率，不重复衰减。黑环境无太阳时散射为零，透射衰减仍在。

门控包括场景、大气、水下、范围、零密度 / 不透明度；无正交支持。自有雨 / 水花 / 闪电查询本相机雾，原生透明水 / 玻璃 / 折射 / 反射未适配；云取首命中 / 壳距近似，不做云内联合积分。

雾滑块下一帧读取，不刷新雷电启停；字段随天气 XML 保存。`SaveToFile` / `SaveCurrentConfig` 返回实际结果，另存为写入成功后才更新名称 / 列表 / 行星登记；原子保存及失败保护见 [配置契约](architecture.md#配置与预设)，日志见 [诊断](development.md#当前工具与诊断)。

## 闪电与雷声

[LightningModule.cs](../Assets/Scripts/Volken/Weather/Lightning/LightningModule.cs) 管落雷 / 限高 / 雷声；[LightningBolt.cs](../Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs) 管动画 / 分叉 / 闪光 / 暂停清理；同目录上级 `GamePause.cs` 按帧缓存暂停 / 时间。

- 分叉显式 `SetPosition(0, from)`；独立材质整雷共享，由拥有者在分叉全销毁后释放，防主干销毁后变品红；网格 / 渐变静态共享。
- 初始化全部八个亮灭点，首脉冲同帧生效；`BeginAnimation` 向 Unity Light 应用最终配置；落点闪光 `flashIntensity × 0.2`，云内雷光独立。
- 按观察者海拔 / `ceilingAltitude` 限高，默认 12000m、0 关闭；停用 / 离场停协程、雷声与闪电；暂停冻结动画 / 排程，恢复处理 DSP 时间。
- 地区模式先在 `targetRange` 内选球面落点，再按该点 Storm 倍率接受一次，不在晴区重抽；源点以该地理列和云层中高为基准应用 `spawnRange` 偏移。晴区观察者可见邻区雷电，球面落点仍未贴地形；关闭地区模式沿用旧生成路径。
- near / far 按观察者距离选素材，声程可混合落点 / 云底最短距；声速取同位置 `AtmosphereData.SampleAltitude(...).SpeedOfSound`，无有效正值不排新声。
- 固定延迟也要求有效大气样本；兜底声速字段 / UI 已删，不填常量。多 AudioSource / `PlayScheduled` 防新声覆盖旧排程。

跨大气声程、水下介质、进入真空取消旧声、贴地形与联机未处理；云内雷光见 [渲染契约](clouds.md#雷光与水面反射)。
