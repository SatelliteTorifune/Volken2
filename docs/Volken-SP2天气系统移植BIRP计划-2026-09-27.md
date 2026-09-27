# Volken-SP2天气系统移植BIRP计划-2026-09-27

> 状态:🚧 **实施中 —— 且已大幅缩减范围**(2026-09-27 末)
>
> **⚠️ 当前实际范围:只做雷电。雨(阶段 2)与雾(阶段 3)的实现已整体移除,准备重做。**
> 保留:阶段 0 骨架 + 阶段 1 雷电 + 天气状态机。移除详情与原因见 [§10.2e](#102e-重大变更雨与雾整体移除只保留雷电2026-09-27用户决定)。
> 因此下文 §5.2/§5.3/§6.1/§7 阶段 2/3 的内容是**原始计划**,不是当前代码状态。
>
> **⚠️ 另一条作废路线:不移植 SP2 的天气标度/档位系统。**
> SP2 的 `WeatherTypes`(Clear/Few/Rainy/Stormy… 全局预设,统一驱动云/雨/雾)已按用户决定**删除**;
> Volken 改为**让玩家直接设置云/雨/雾/雷的逐项参数并序列化**。详见 [§10.2h](#102h-设计取向修正删除-weathertypes--volken-不走-sp2-的全局天气预设路线)。
>
> **⚠️ 天气配置形态(以本行为准,§10.2g 已被取代)**:天气参数**按预设名独立存**,
> 与云层预设**完全独立、互不干扰** —— `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`;
> 预设名记在 `<PlanetConfig WeatherConfigName>` 上,与 `CloudConfigName` 各不相干,
> 可在天气面板**自由新建 / 保存 / 读取**(「另存为新配置」+「加载配置」下拉)。
> 云与天气的**配对联动暂不做,留到后期**再考虑。
> `PlanetConfigList.xml` 里那条 `<PlanetConfig>` **只记预设名**,不再内联 `<Weather>`
> (旧内联格式在读取清单时会自动迁移成预设文件)。
> §10.2g 记录的是**当时**的"内联进 PlanetConfig"方案,保留作决策沿革,**勿照做**;
> §10.2f 是**更早被取代**的中间方案,同样勿照做。
>
> 目标:把 SimplePlanes 2(SP2) 的天气系统(雨/雾/雷电)以**原生 BIRP** 方式移植进 Volken mod,与现有体积云管线合并。
> 决策:已确认走**原生 BIRP 实现**(不引入 Enviro3),闪电纯 C#、雾写成 BIRP 全屏 pass、雨用 ParticleDomain 计算管线。
> *(原始决策,雾/雨部分待重做时重新评估。)*

>
> 实施进展、与原计划不符之处的实测更正,见 [§10 实施进展与计划更正](#10-实施进展与计划更正2026-09-27-实测)。

---

## 1. 背景与目标

- Volken 当前 = SR2/Juno 的 **raymarch 体积云 mod**(自研 `CloudRenderer` + `Clouds.shader`,BIRP `[ImageEffectOpaque]` 合成链,Unity 2022.3.62f3)。
- 需求:在有大气的行星上增加**雨、雾、雷电**,由逐行星天气配置驱动,与云层/大气联动。
- 移植来源:SP2 反编译工程 `C:\renko\shitProgram\反编译的\sp2` + 游戏安装 DLL 反编译产物 `analysis\dll\` + 素材提取工程 `C:\renko\unityProjects\sp2d4`。
- 目标工程:本仓库 Volken2(Unity 2022.3.62f3, BIRP, SR2 mod)。

## 2. SP2 天气系统本质(反编译实证)

> 全部结论来自对游戏安装目录 DLL 的反编译:`SimplePlanes 2_Data\Managed\Enviro3.Runtime.dll`、`Jundroo.Common.dll`。反编译源码见 `C:\renko\shitProgram\反编译的\sp2\analysis\dll\`。

### 2.1 雨 = Jundroo ParticleDomain(GPU 计算粒子域)+ Enviro 雨粒子(次要)

SP2 主雨是 `Jundroo.Common.ParticleDomain`(游戏侧子类 `ParticleHandler`):

- **渲染**:单一 ComputeShader(`BillboardParticles`,6 内核 `Positioning / Randomize / TranslateFixed / CullAndOccludePoints / CullPoints / FillArgs`)+ `Graphics.RenderMeshIndirect` 间接实例化;粒子 = 程序生成十字交叉雨丝网格(8 顶点/12 三角),billboard 纹理,沿「风 + 重力 15m/s + 玩家速度 − 相机速度」拉伸对齐;
- **域**:以相机为 `Target` 的 50m 半径球域,默认 10 万粒子,随画质档缩放(0.25/0.5/1.0);
- **遮挡**:`OcclusionDepthCam` 正交深度相机(256×256 Depth RT,从相机上方沿雨下落方向俯拍)+ **URP `RTCameraRendererFeature`**(`RTCameraRenderPass`,RenderGraph,`DepthOnly` pass,`AfterRenderingOpaques`)渲染深度,`CullAndOccludePoints` 用上一帧 VP 矩阵(`_previousOrthoVP`)采样 `_OcclusionDepth` 剔除被遮挡雨滴;
  - ⚠️ **RTCamera 部分是 URP 专属,是移植到 BIRP 唯一必须重写的组件**;
- **控制**(`ParticleHandler`):`WeatherValue > 2.25` 触发;AGL<100m 或座舱内 → 整体淡出;入水冻结;云内按 `CameraCloudFadeVal` 渐隐;上下地面切换 1s 快速重入;
- 雨声:机舱雨声 `RainAircraft`(音量=Clamp01(weather−2.25)×云淡化,音调随空速)+ 环境雨声(音量∝1/离地高度^0.7)。

SP2 另有一套 Enviro 雨粒子(`Rain.prefab` 标准 ParticleSystem 2.5 万粒子 + `Rain_Splash` 水花),由天气预设 effectsOverride 生成 —— 移植时**可跳过**,水花可后补。

### 2.2 雾 = Enviro3 高度雾 + 体积雾(可选)

- **高度雾**:全屏 Blit(`Hidden/EnviroHeightFog` / URP 版 `Hidden/EnviroHeightFogURP`),参数:`fogHeight`、`fogHeightFalloff`、`fogDensity`、`fogMaxOpacity`、`startDistance`、`fogColorBlend`,支持**两段高度雾**(`_EnviroFogParameters2`);
- **体积雾(可选)**:`EnviroVolumetricFogLight`(自动加在平行/点/聚光上)+ 全屏步进 + 降采样 + 模糊 —— 移植优先级低,先做高度雾;
- BIRP 挂载:`EnviroRenderer.OnRenderImage(src,dest)` 全屏合成(已反编译实证)。

### 2.3 雷电 = Enviro3 程序化闪电(纯 C#)

- 触发链:`lightningStorm=true` → `EnviroLightningModule.UpdateModule()` → `CastLightningBoltRandom()`(源点=云层中高 `(云底+云顶)/2` 随机偏移,落点=地面随机偏移)→ 实例化 lightning prefab(`ILightningEffect`)→ `CastBolt(from,to)` → 延迟 0.05s → `PlayRandomThunderSFX()` 随机播一条雷声;
- 渲染:`Lightning`(默认实现,约 170 行)= LineRenderer 20 段锯齿主干 + 每段 0~4 分叉子 LineRenderer + 点光源闪烁 4 次(30ms 级)+ 材质 `_Intensity` 发光 + 50 帧淡出销毁;
- 音频:EnviroAudioModule 的 `thunderClips` 列表随机(SP2 配置 5 条 Thunder 1~5)。

## 3. 移植路线:原生 BIRP(已确认)

| 组件 | 做法 | 依赖 |
|---|---|---|
| 雷电 | 移植 `EnviroLightningModule`+`Lightning` 逻辑(纯 C#,BIRP 无关) | 1 个 LineRenderer 材质(`_Intensity`)+ 雷声音频 |
| 雾 | 自写 BIRP 高度雾全屏 pass(仿 Enviro3 参数),复用 Volken `DepthCapture` 线性深度 | 1 个全屏 shader |
| 雨 | 移植 `ParticleDomain` 计算管线(ComputeShader 部分 BIRP 原生兼容),**遮挡改 BIRP** | compute shader + 雨滴软粒子 shader(重写)+ billboard 纹理 |

**不引入 Enviro3**:Volken 已有自己的云/天空栈,Enviro3 会与 `Clouds.shader` 合成链冲突,且体积/许可负担大。

## 4. 与 Volken 现有架构的合并点

| Volken 组件 | 天气接入 |
|---|---|
| `Mod.cs`(`OnModLoaded`) | `WeatherSystem.Initialize()`;注册 dev 命令 |
| `VolkenMod.cs`(单例,`SceneLoaded`+`PlayerChangedSoi`) | 同模式:按行星加载 `WeatherConfig`,按 `HasPhysicsAtmosphere`/`HasWater` 启停天气 |
| `CloudRenderer`(`[ImageEffectOpaque] OnRenderImage` 合成链) | 雾 pass 插入同链(顺序:深度→雾→云→合成);闪电/雨独立于链 |
| `DepthCapture`(FarCamera 线性深度 RFloat RT) | 直接复用于雾的视距计算与雨的软粒子深度淡化 |
| `CloudConfig`(逐行星 JSON)+ `PlanetConfigList` | 仿写 `WeatherConfig`(雨量/雾浓度/雷电频率/云联动) |
| `ModSettings`(`SettingsCategory`) | 加 `WeatherEnabled`、`RainQuality`、`ThunderEnabled`、`FogEnabled` 等 |
| `VolkenUserInterface`(58KB 面板) | 可选:天气面板(覆盖/强度/手动触发闪电) |
| `Mod.Instance.ResourceLoader.LoadAsset<T>("Assets/...")` | 加载 compute shader / 雨 shader / billboard 纹理 / 音频 |
| `ReferenceFrameRecentered` 事件 | 雨粒子域平移、雾/闪电无需处理(相机系) |

## 5. 模块设计(新目录 `Assets/Scripts/Volken/Weather/`)

```
Weather/
  WeatherSystem.cs       单例:WeatherValue 状态机(移植 SP2 VolumetricEnvironment 纯逻辑)
                          - 标度:Foggy -1 / Clear -0.05 / Few 0.25 / Broken 0.75 / Overcast 1.5 / Rainy 2.25 / Stormy 2.5 / Heavy 2.75
                          - 动态天气:停留 480~960s,淡变 0.001/s;晴后必转云、雨天后必转晴的规则
                          - 雾黎明特例(5~6 时强制转雾)
                          - 逐行星 WeatherConfig 覆盖
  WeatherConfig.cs       逐行星配置(JSON,仿 CloudConfig):启用、雨强、雾浓度、雷电频率、云层联动参数
  Rain.cs                ParticleDomain BIRP 移植(见 §6.1)
  RainOcclusion.cs       遮挡 BIRP 方案(见 §6.1 方案A/B)
  FogRenderer.cs         BIRP 高度雾全屏 pass(OnRenderImage 链内)
  Lightning.cs           移植 Enviro Lightning(LineRenderer 锯齿+分叉+闪烁)
  LightningModule.cs     随机闪电生成 + 雷声(5 条 thunderClips 随机)
```

### 5.1 WeatherSystem 状态机(移植自 VolumetricEnvironment)

- 保留:WeatherValue 标度、随机天气规则、淡变、雾黎明、`OnWeatherValueChanged` 事件;
- 删除:Enviro 预设混合(LerpWeatherTypes)、光照/雾混合反射、`IsDarkOutside`/`Brightness`(SR2 无夜间光照需求,可后补);
- 新增:天气 → Volken 云层联动(覆盖度/颜色暗化),SOI 切换时按行星配置重置;
- 挂载:随 `Volken` 单例,`OnFlightSceneLoaded` 时重建,不依赖场景物体。

### 5.2 Rain(BIRP 版 ParticleDomain)

**可用部分(原样移植)**:compute shader 管线(6 内核)、`RenderMeshIndirect` 间接绘制、雨丝网格生成、`TranslateParticles` 浮点原点平移、淡入淡出协程、域跟随相机、软粒子深度淡化(复用 `DepthCapture`)。

**遮挡重写(二选一)**:

- **方案 A(先跑通,= SP2 Low/Medium 档)**:不做几何遮挡贴图,只用 CPU 判定(AGL<100m、座舱内、入水、云内淡化)整体淡出。成本≈0,低空穿建筑为已知限制;
- **方案 B(完整,≈SP2 High 档)**:保留 `OcclusionDepthCam` 正交相机 + `_occlusionTex`(256×256 Depth),渲染方式从 URP RenderGraph 换成 BIRP:
  - `_occlusionCam.targetTexture = _occlusionTex; _occlusionCam.RenderWithShader(depthShader, "RenderType")`(每帧手动 Render),或
  - CommandBuffer:`SetViewProjectionMatrices(occlusionView, occlusionProj)` + `DrawRenderers(DepthOnly pass)` → `_occlusionTex`;
  - `CullAndOccludePoints` 内核与 `_previousOrthoVP` 延迟一帧链路**原样保留**(只换深度贴图来源)。
- 建议:A 起步、B 二期(深度 pass 与 Volken 已有深度管线同构,工作量可控)。

**雨滴 shader(必须重写)**:SP2 的 `Jundroo/ParticleConstantNormal_Soft` 只有 DummyShaderTextExporter 占位,源码在游戏 bundle 编译产物里。重写一个 BIRP 软粒子 billboard(≈50 行):`_MainTex`+`_Emission`+`_InvFade`(软粒子深度淡化,用 Volken `DepthCapture` 深度),实例化绘制属性用 `unity_ObjectToWorld`/`_Positions` buffer 语义保持一致。

### 5.3 FogRenderer(BIRP 高度雾)

- 全屏 pass,参数与 Enviro3 对齐:`fogHeight / fogHeightFalloff / fogDensity / fogMaxOpacity / startDistance / fogColorBlend`,可选第二段;
- 采样 `DepthCapture.DepthTexture` 线性深度做视距衰减;
- 合成顺序:与 `CloudRenderer` 同一条 `OnRenderImage` 链(雾在云前或云后,按效果调;建议雾先于云,让云吃雾遮挡);
- 颜色:与行星大气散射颜色联动(Volken 已有 `atmoBlendFactor` 相关参数)。

### 5.4 Lightning(纯 C# 移植)

- `Lightning.cs`:移植 `CreateLightningBolt`(20 段锯齿 + 分叉 + 点光源 4 次闪烁 + `_Intensity` 淡出);材质用一个新建 BIRP unlit/additive shader(`_Intensity` 属性),或直接复用 Volken 现有透明 shader;
- `LightningModule.cs`:随机生成(源点=云层中高 ± randomSpawnRange,落点=地面 ± randomTargetRange)+ 两次雷击间隔 `randomLightingDelay` + 落雷 0.05s 后随机播雷声;
- 触发:WeatherValue ≥ 2.0 且云层覆盖高时按概率开火(`lightningStorm` 语义简化);可加 dev 命令手动劈一道;
- 音频:`thunder_1~5.ogg`(从 sp2d4 `AudioClip/` 拷入 mod bundle)。

## 6. 素材清单(从 sp2d4 拷贝)

| 素材 | 来源路径(sp2d4) | 用途 |
|---|---|---|
| `BillboardParticles.asset` | `Assets/ComputeShader/BillboardParticles.asset` | 雨 compute shader(同为 Unity 2022.3,可直接导入) |
| `enviro_thunder_1~5.ogg` | `Assets/AudioClip/` | 雷声(如未提取到对应文件则用 `enviro_rain_1~3` 的配套目录) |
| `enviro_rain_1~3.ogg` | `Assets/AudioClip/` | 环境雨声(可选) |
| billboard 雨滴纹理 | sp2d4 纹理目录中雨/雪相关 | 雨滴贴图 |
| `VolumetricEnvironment.prefab` | `Assets/Resources/environment/sky/` | 参考:weatherTypes/audio/lightning 配置值(不直接搬) |
| `Rain.prefab` | `Assets/GameObject/` | 参考:水花/雨粒子参数(可选二期) |

⚠️ 注意:sp2d4 是 SP2 的资产提取工程,其 Unity 版本也是 2022.3,compute shader 资产可直接拷入本工程重新编译;但**雨滴 shader 源码缺失**,需按 §5.2 重写。

## 7. 分阶段实施

- **阶段 0 — 骨架**:`Weather/` 目录 + `WeatherSystem` 状态机 + `WeatherConfig` + `Mod.cs`/`VolkenMod.cs`/`ModSettings.cs` 接入点(空实现,可编译)。
- **阶段 1 — 闪电(最快见效)**:`Lightning.cs` + `LightningModule.cs` + bolt 材质 + 雷声音频;dev 命令手动触发验证。
- **阶段 2 — 雨(核心工作量)**:`Rain.cs` BIRP 移植 + 雨滴 shader 重写 + 遮挡方案 A;挂相机、SOI 启停、天气值联动。
- **阶段 3 — 雾**:`FogRenderer.cs` + 雾 shader,接入 `OnRenderImage` 链;与云层/大气散射联动。
- **阶段 4 — 打磨**:遮挡方案 B、水花、天气与云层配置联动、UI 面板、联机行为核对(天气只作用于本机或主机同步,待定)。

## 8. 风险与注意点

1. **许可**:`ParticleDomain`/`ParticleHandler` 属 Jundroo(SP2 开发商);`Enviro3` 为商业资产。本计划仅移植**逻辑与算法**到自用 mod,不搬运原 DLL/商业资源文件,雷声/雨声音频如来自游戏资源需自行确认使用边界;
2. **compute shader 跨版本**:SP2 与 Volken 同为 2022.3.62,`BillboardParticles.asset` 大概率直接可用;若 Unity 重编译失败,按 6 内核逻辑手写(逻辑简单,内核职责明确);
3. **SR2 无 WeatherValue 环境**:SP2 的 `FlightSceneScript.Instance.Environment` 不存在,天气状态由 mod 持有;云/雾/闪电全部以相机为参照(BIRP OnRenderImage 链内);
4. **浮动原点**:雨粒子平移复用 `ReferenceFrameRecentered`(CloudRenderer 已有先例);雾/闪电无影响;
5. **联机**:SR2 联机时天气是否同步、由谁驱动,阶段 4 再定(可先仅本机表现);
6. **性能**:雨 10 万粒子 = GPU 实例化,移动端/低配注意;画质档(`RainQuality`)照 SP2 的 Off/Low/Medium/High 实现。

## 9. 参考(反编译产物)

- `C:\renko\shitProgram\反编译的\sp2\analysis\dll\Jundroo.Common.ParticleDomain.decompiled.cs`(698 行,雨域全源码)
- `C:\renko\shitProgram\反编译的\sp2\analysis\dll\Jundroo.Common.RTCameraRendererFeature.decompiled.cs` + `RTCameraRenderPass.decompiled.cs`(遮挡 URP 实现,供 BIRP 改写参照)
- `C:\renko\shitProgram\反编译的\sp2\analysis\dll\Enviro.EnviroLightningModule.decompiled.cs` + `Enviro.Lightning.decompiled.cs`(闪电全源码)
- `C:\renko\shitProgram\反编译的\sp2\analysis\dll\Enviro.EnviroFogModule.decompiled.cs`(728 行,雾全源码)
- `C:\renko\shitProgram\反编译的\sp2\analysis\dll\Enviro.EnviroAudioModule.decompiled.cs`(雷声播放)
- 游戏侧:`C:\renko\shitProgram\反编译的\sp2\Game\Game\Assets\Scripts\Flight\ParticleHandler.cs`、`Assets\Scripts\Environment\VolumetricEnvironment.cs`、`Assets\Scripts\Environment\WeatherTypes.cs`

---

## 10. 实施进展与计划更正(2026-09-27 实测)

### 10.1 已完成(⏩ 2026-09-27 末更新:雨与雾已移除;天气配置并入 `PlanetConfig` —— 见 §10.2e / §10.2f / §10.2g)

| 阶段 | 状态 | 落地文件 |
|---|---|---|
| 0 骨架 | ✅ 编译通过 | `Weather/WeatherTypes.cs`、`VolkenWeather.cs`;配置类并入 `Core/PlanetConfigList.cs` |
| 1 闪电 | ✅ 编译通过(真机未验证) | `LightningBolt.cs`、`LightningModule.cs`、`LightningBolt.shader`、`Weather/Audio/enviro_thunder_1~5.ogg` |
| 2 雨 | ❌ **已整体移除**(配置字段保留占位) | ~~`RainParticles.compute`、`RainParticles.shader`、`Rain.cs`~~ → stash 在 `%TEMP%\volken-rain-fog-stash` |
| 3 雾 | ❌ **已整体移除**(配置字段保留占位) | ~~`HeightFog.shader`、`FogRenderer.cs`~~ → stash 同上;`CloudRenderer.LinearSceneDepth` 保留 |
| 4 打磨 | 🟡 部分完成 | 已做:**天气配置并入 `PlanetConfig`(与云共用 `PlanetConfigList.xml`)**、`WeatherPanel.cs` 五分组(雨/雾/云层联动三组禁用占位)、三语言文案。**未做**:雨重做、雾重做、云层联动实现、水花、联机行为核对 |

改动到的既有文件:`Mod.cs`(初始化 + dev 命令 + `LoadVolkenAsset<T>` + `LogThrottled`)、`ModSettings.cs`(**2 个**天气设置:`WeatherEnabled`/`ThunderEnabled`)、`VolkenMod.cs`(`Weather` 静态口 + `using VolkenMod.Core/Weather`)、`VolkenUserInterface.cs`(挂天气面板)、`CloudRenderer.cs`(`LinearSceneDepth`,现无消费者)、`Assets/Content/Languages/{EN-US,ZH-CN,RU-RU}.xml`、`Assets/ModData.asset`(资产清单)。

**落盘位置(云与天气同一份文件 —— 见 §10.2g)**:

```
UserData/VolkenConfig/PlanetConfigList.xml      ← 一条 <PlanetConfig> 记录 = 云预设名 + 该行星的 <Weather>
UserData/VolkenConfig/{行星}/{预设名}.xml        ← 云层的参数本体(未改动)
```

新增 dev 命令:`volkenWeather`(打印状态:**行星 / 天气值 / 雷电 / 配置文件路径**)、`volkenSetWeather <float>`(强制天气值)、`volkenBolt`(手动劈一道雷)、`volkenAssets`(资源台账)。
~~`volkenRainProbe` / `volkenRainAxis` / `volkenRainLean` / `volkenRainAuto`~~ 已随雨删除。

**编译验证方式(可复用)**:`dotnet build Volken.csproj` 直接编译整个 mod 程序集,**约 2~25 秒**、报错带行号,不必等 Unity 刷新。注意 Unity 未刷新时 `Volken.csproj` 不会包含新文件,需要手工补 `<Compile Include="..." />` 再编(仅用于验证,Unity 下次刷新会重生成)。它**只编 C#**,看不到 shader/compute 错误。


### 10.2 原计划需要更正之处(逐条实测)

> 📌 **先看一条整体性更正**:§2/§5 里"移植 SP2 天气标度(`WeatherTypes`)"这条路线**已作废** ——
> 该文件已删除,改为"各子系统参数由玩家逐项设置"。见 **§10.2h**。

**① 命名空间不能叫 `Volken.Weather` —— 编译期硬错误(CS0101)。**
工程里已有**全局命名空间**下的类 `Volken`(`Clouds/CloudRenderer.cs` 等都以裸 `Volken.` 引用它)。再声明 `namespace Volken { ... }` 会让 `<global namespace>` 出现两个 `Volken` 定义 → `CS0101: already contains a definition for 'Volken'`。
→ 天气系统统一用 **`VolkenMod.Weather`**(与既有 `VolkenProfiler` 命名空间风格一致)。**后续新增天气文件必须沿用此命名空间。**

**② `DepthCapture` 是死代码,不能"复用"。** 计划 §3/§4/§5.3 三处写"复用 Volken `DepthCapture` 线性深度",实测:
- 全工程 `grep DepthCapture` **零引用**;
- 它依赖 `Shader.Find("Hidden/DepthLinear")`,而**工程里没有这个 shader**;
- 之所以没人用:其头部注释说明 `OnRenderImage` 会让 far camera 走中间 RT + resolve,在近相机远裁剪面处留 1px 接缝、并微调其输出 —— 已被 `FarCameraScript` 的 CommandBuffer 方案取代。

**真正可用的深度是 `CloudRenderer.combinedDepthTex`(实例私有 RFloat,远深度 + 近深度合并后的线性深度)。**
→ **阶段 3 的雾必须取 `CloudRenderer.combinedDepthTex`,或直接写进 `CloudRenderer.OnRenderImage` 内部。** 计划里"雾在云前或云后"的顺序问题,也取决于这一点:两个各自独立的 `OnRenderImage` 组件之间的执行顺序**由组件挂载顺序决定,不可靠**;若走独立 `FogRenderer`,必须用 `[DefaultExecutionOrder]` 显式钉死顺序,否则雾/云前后关系会随挂载顺序漂移。

**③ `BillboardParticles.asset` 是编译产物,读不出 HLSL 源码。**
实测该文件是 YAML `!u!72 ComputeShader`,`code:` 字段是 hex 编码的 DXBC 字节码(不是源码)。反射信息可读全,已确认 6 个内核的名称、buffer 绑定与线程组尺寸:

| 内核 | 输入/输出绑定 | threadGroupSize |
|---|---|---|
| `Positioning` | out `_Positions`;uniform `_domainRadius/_domainPos/_frameVel/_particleVel/_InstanceCount` | 64,1,1 |
| `Randomize` | out `_Positions`;uniform `_domainRadius/_domainPos/_InstanceCount` | 64,1,1 |
| `TranslateFixed` | out `_Positions`;uniform `_translation/_InstanceCount` | 64,1,1 |
| `CullAndOccludePoints` | in `_Positions`;out `_CulledPositions/_CulledCount`;tex `_OcclusionDepth`;uniform `_FrustumPlanes/_OcclusionNear/_OcclusionFar/_InstanceCount/_OcclusionVP/_OcclusionView` | 64,1,1 |
| `CullPoints` | in `_Positions`;out `_CulledPositions/_CulledCount`;uniform `_FrustumPlanes/_InstanceCount` | 64,1,1 |
| `FillArgs` | in `_CulledCount`;out `_ArgsBuffer` | 1,1,1 |

→ **决策(已确认):阶段 2 按上表把 6 个内核重写为 `.compute` 源码**,不搬运编译字节码(跨平台/跨图形 API 有风险,且无法调试)。绑定与线程组尺寸照抄上表即可与 `ParticleDomain` 的 C# 侧调用 (`SetBuffer`/`SetTexture`/`Dispatch`) 对齐。Buffer 元素布局(来自 `ParticleDomain.SetupBuffers`,须一致):
`_Positions` stride 12(`float3`)、`_CulledPositions` stride 16(`float4`:xyz = 位置、w = 随机种子/尺寸)、`_CulledCount` stride 4(`uint`)、`_ArgsBuffer` 20 字节(`GraphicsBuffer.IndirectDrawIndexedArgs`)。

**④ SR2 没有 `WindManager`,雨的风向来源要换。**
SP2 的 `ParticleHandler.GetWindVelocity()` 取 `_windManager.WindVelocity`;SR2/JNO ModApi 无此入口。
→ 雨的定向风**复用 `CloudConfig.windSpeed / windDirection`**,经 `CloudRenderer.AdvanceGlobalCloudState` 已有的"行星北/东向量 → 世界向量"换算拿到(顺手保证**云雨同向**)。

**⑤ SP2 的浮动原点事件名在 SR2 不存在。**
SP2 用 `GameWorld.Instance.FloatingOriginChanged`(见 `ParticleHandler.OnFloatingOriginChanged`);SR2/JNO 没有这个 API。
→ 雨粒子域平移用 **`IGameView.ReferenceFrameRecentered`**(委托 `(IReferenceFrame, Vector3d, Vector3d)`),即 `CloudRenderer.OnReferenceFrameRecentered` 的既有写法。

**⑥ `ParticleHandler` 的抽象成员在 SR2 侧的对应实现。**

| SP2 抽象 | SP2 实现 | SR2 对应 |
|---|---|---|
| `GetAltitudeASL()` | `GameWorld.FloatingOriginOffset.y + target.y` | 相机位置到行星中心距离 − `PlanetData.Radius`(已实现在 `VolkenWeather.CameraAltitudeAsl`) |
| `GetPlayerVelocity()` | 联机 `FlightScenePlayer.Velocity` | `ICraftNode.SurfaceVelocity` / `ICraftFlightData.SurfaceVelocity`,或 `VolkenWeather.CameraVelocity`(已实现,相机速度平滑值) |
| `GetWindVelocity()` | `WindManager.WindVelocity` | 见 ④ |
| `IsCameraSubmerged()` | `WaterRenderer.ViewerHeightAboveWater < 0` | `CameraAltitudeAsl < 0 && PlanetData.HasWater`(已实现在 `VolkenWeather.CameraSubmerged`) |
| 云内淡化 `CameraCloudFadeVal` | Enviro 的 `bottom/topCloudsHeight` | Volken 主层 `layerHeights/layerSpreads/layerStrengths` 推出的 [云底, 云顶−400](已实现在 `VolkenWeather.CameraCloudFade`) |
| `TimeOfDay`(黎明起雾) | Enviro 时间模块 | **由太阳方向与地表法线夹角现算局部太阳时**(`VolkenWeather.ComputeLocalSolarHour`),见 10.3 |

**⑦ mod 自己的资源加载器没有 `LoadAudio`,也没有 `Load<T>`。**
实测:`Mod.Instance.ResourceLoader` 是 `IModResourceLoader`,只暴露 **`LoadAsset<T>(path)`**;
`Load<T>(path, logErrors)` 与 `LoadAudio(path, logErrors)` 都在游戏的 `IResourceLoader` 上,而 `LoadAudio` 内部是 `Resources.Load`(**读不到 mod bundle 里的素材**)。
→ 统一用 `Mod.LoadVolkenAsset<T>(path, required)`(本次新增的护栏封装)。雷声因此**自建 `AudioSource`** 播放(`PlayScheduled` 定时),代价是不经过游戏音效混音组,音量由 `WeatherConfig.thunderVolume` 单独给。

**⑧ `Shader` 上没有 `FindPass`。** `FindPass` 是 `Material` 的方法。`LineRenderer`/`MeshRenderer` 会自动选 pass,本 shader 两个 pass 分属不同材质、各只有一个可用 pass,不需要手动指定索引。

### 10.2b 阶段 2 / 3 / 4 实施中新增的更正(同样是与原计划/与 SP2 的实测差异)

> ⚠️ **本节 ⑨~㉞ 与 10.2c0 / 10.2c2 / 10.2d 全部是"雨/雾实施期"的实测记录。
> 雨与雾已于 §10.2e 整体移除,这些记录现在是"重做时的参考资料",不是当前代码的说明。
> 其中被删除的 API 名(`Rain.*`、`FogRenderer.*`、`RainParticles.*` 等)在工程里**已不存在**。**

**⑨ 雨必须用"相机相对"坐标系,而且视锥平面必须**手工构造**。** ⚠️ 这条前后改过一次结论,最终版如下。

第一版实现把域中心放到**参考系原点**(`_domainPos = Vector3.zero`),理由是"要和
`GeometryUtility.CalculateFrustumPlanes` 的输出同空间"。那个理由本身站得住,但选错了空间:
`GeometryUtility` 给的是**参考系空间**的平面(法线是世界朝向、距离相对参考系原点),
而粒子存的是"域局部偏移";两者只有在相机恰好位于参考系原点附近时才一致 ——
**相机一飞远,剔除判据就整体错位**(症状:雨只在一小块区域出现,或整片消失,
而且会随相机离原点的距离变化)。

**最终方案:一切都在相机相对空间里做。**
- 粒子位置 = **相对相机的偏移**;`_domainPos` = 相机世界位置(所以"世界 = _domainPos + 偏移"仍成立);
- 视锥平面由 `Rain.BuildCameraPlanes` **解析构造**(近/远平面法线 = ±forward、距离 = ∓near/±far;
  四个侧面都过相机原点故 d = 0,法线由半 FOV 与宽高比推出)。约定仍是
  `dot(n,p) + d >= 0` 为内侧,与 `GeometryUtility` 一致,便于对照;
- **不要**再用 `GeometryUtility.CalculateFrustumPlanes` 配这套坐标 —— 空间不同。

副产品:相机高速移动(10km/s 级)下没有大数精度问题;而且既然"域中心 == 相机",
**浮动原点重置天然不需要补偿**(SP2 需要,因为它的域局部坐标要反向平移,而本实现两边一起挪),
所以 `_frameVel` 恒 0 是**刻意的、不是漏了**。

**⑩ compute shader 没有 `_Time` 内置量。**
`_Time` 是顶点/片元着色器的内置 uniform,compute 里不可用(编译报未定义)。回绕重生需要掺入时间
(否则同一粒子每次都在同一个 xz 重生 → 固定竖线),因此新增 `float _time` uniform 由 C# 每帧传 `Time.time`。

**⑪ 雨的绘制必须放在渲染回调里,且回调类型是 `Camera.CameraCallback`。**
原计划没写这一点。放在 `Update` 里调用 `Graphics.RenderMeshIndirect` 会得到"帧开始前的游离渲染命令",
与具体相机无关;雨是**独立网格**,需要排进该相机的透明队列并赶上它自己的深度纹理。
另:静态委托的类型是 **`Camera.CameraCallback`**,在 .NET 4.x 下 `Action<Camera>` **不能**隐式转换过来(CS0029)。

⚠️ **真机更正(2026-09-27):`Camera.onPreCull` 这个静态委托在本工程里从未被调用。**
证据是"排除法":雨的一切前置条件全部满足
(`populated=True active=50000 pendingDraw=True`,且 `fade` 从 0 正常爬到 0.765),
但 `draws/s` 恒 0、`everDrew=False`,并且 `RenderForCamera` 里那三条 `LogThrottled`
(相机不匹配 / buffer 缺失 / 状态跳过)**一条都没打** ——
说明函数**一次都没被进入**,不是被某个条件拦住的。工程内也确认没有第二处代码碰 `Camera.onPreCull`
(不会是被别人覆盖)。
→ 现在主路径改为 **`RainCameraRenderer : MonoBehaviour` 的实例 `OnPreCull`**(挂在游戏相机上),
静态订阅保留作双保险。实例回调在本工程里是验证过可靠的(`FogRenderer.OnRenderImage` 一直在跑)。
心跳里新增 `renderer: host= preCullCalls=` —— `preCullCalls` 恒 0 就说明实例回调也没触发。

**⑫ 实例索引用 `SV_InstanceID`,不要用 UNITY_VERTEX_INPUT_INSTANCE_ID 那一套。**
`Graphics.RenderMeshIndirect` + 结构化 buffer 与"带实例属性的传统 Instancing"不是同一条路径;
`unity_InstanceID` / `UNITY_SETUP_INSTANCE_ID` 是为后者设计的,混用容易拿到恒 0 的实例号
(症状:所有雨丝重叠在同一处)。`SV_InstanceID` 与 `_CulledPositions` 的原子累加写入顺序一一对应。
⚠️ **这一条是推断 + 设计选择,尚未在游戏里跑过** —— 属于最需要真机确认的点(见 §10.3)。

**⑬ 雨丝 UV 不做平铺。**
原计划没写。雨丝的**长度**已经由 `AlignStreaks` 的基向量列 1(`dir × stretch × streakLength`)表达,
贴图本身纵向就是"头亮尾淡"的渐变;再按拉伸量平铺 UV 会在 `wrapMode = Clamp` 下被夹到边缘形成糊块。
故 UV 直接用网格 UV。

**⑭ 高度雾用解析积分,不能用 `1 − exp(−density × distance)`;而且 Unity 的 HLSL 没有 `expm1`。**
Enviro 的 `EnviroFogModule` 是沿视线的**指数高度雾解析积分**,本实现照做:
`∫₀ᴰ ρ dt = density · ρ(y₀) · H · (1 − exp(−f)) / f`,`f = D·dy/H`。
朴素形式会得到"抬头时头顶堆一片雾"的错误观感;解析积分给出
"地平线附近最浓、抬头迅速变淡",这正是高度雾的观感来源。

⚠️ **实测踩坑(由 Unity 自己的 shader 编译器抓到的,见 §10.4)**:初版对该式用了
`-expm1(-f)/f`(想借 expm1 的数值稳定性),Unity 直接报
`Shader error ... undeclared identifier 'expm1' ... (on d3d11)`。
**Unity 的 HLSL 编译目标没有 `expm1`**(它是 C99/C++11 的函数,不是 HLSL 内置)。
现改为:对 `|f| < 1e-3` 的区间用泰勒展开 `1 − f/2 + f²/6`,其余区间用 `(1 − exp(−f))/f`。
后者在 f 很小时会因 `1 − exp(−f)` 的**灾难性抵消**而失真(float 只有 ~7 位有效数字),
这正是必须保留泰勒分支的原因,不是可选优化。

**⑮ `InspectorModel` 不是 `GroupModel`。**
阶段 4 的天气面板:两者是各自独立的顶层模型,`InspectorModel` 只有 `AddGroup(GroupModel)` /
`Add<T>(T)`;**没有** `Add(ItemModel)`。所以面板必须自建一个顶层 `GroupModel` 再 `AddGroup` 上去,
不能像云那样直接往 inspector 里塞子项。另 `GroupModel.Add(T)` 泛型不带约束,组套组可以工作。

**⑯ 云层联动必须从**基线**重算,不能在上一帧结果上叠加。**
从 `ApplyCloudCoupling` 的实现:进行星时记下 `coverage/windSpeed/cloudColor` 的基线,之后每帧
`值 = 基线 + 天气值 × 增益` 的**纯函数**重算。叠加式会逐帧漂走(gain 再小也会发散)。
三个增益默认**全 0** = 一个字段都不写 = 与未实现本特性逐字节一致。

**⑰ 雨的 buffer 容量与"生效粒子数"必须分离,不能照抄 SP2 的 `ParticleAmount` setter。**
SP2 的 `ParticleDomain.ParticleAmount` setter 在数量变化时会
`FreeBuffers() + SetupBuffers()` **重建全部 GPU buffer** 并重新 Dispatch `Randomize`。
它的调用时机是天气值变化事件(低频、且不在渲染循环里),所以安全。
但本实现是从**每帧的 `Tick`** 里算粒子数的,一旦真的重建,就会在
"上一帧的绘制命令还没执行完"时释放掉它引用的 `GraphicsBuffer` —— 未定义行为。
故拆成两个概念:
- **容量**(`_capacity`):按画质档**上界**分配一次(High = 100000 / Medium = 50000 / Low = 25000),
  只在"需要超过现有容量"时才重建,并在**首次分配**时才 `Dispatch(Randomize)`
  (否则小雨转暴雨会把整片雨的位置重置,视觉上"雨幕跳一下");
- **生效数量**(`_activeCount`):每帧设置,只改 `_InstanceCount` 与 draw 实例数。
  少于容量的那部分粒子**完全不参与计算** —— 内核每个线程开头都有
  `if (i >= _InstanceCount) return;`,不用重建 buffer 也不会算到它们。
这个取舍是**故意的:稳定性优先于逐字节照抄**。

**⑱ 非下雨区间的粒子数必须是 0,不能沿用"上次的雨强系数"。**
SP2 的 `GetRainStrengthFactor` 在 `weatherValue <= 1.5` 时**返回当前值**(语义是
"维持上次雨强,等下次进雨区时用"),而 `ParticleHandler._currentRainStrengthFactor`
的**初始值就是 1.0**。照搬会让**晴天常驻 5 万个粒子**在 GPU 上空转
(位置推进 + 视锥剔除全部照跑,只是 `_FadeAmount = 0` 不画)。
`Rain.ResolveParticleAmount` 因此在 `!IsRaining` 时直接返回 0。

**⑲ 方案 B 的"半接通"状态必须有硬护栏。**遮挡深度图一旦是空的,`CullAndOccludePoints` 会把**每个**粒子都判成"被几何体遮挡"并剔除
→ **雨完全消失,而且没有任何报错**。因此 `Rain` 判定走不走遮挡内核需要**两个**条件同时成立:
`OcclusionEnabled`(配置开关)**且** `OcclusionDepthRendered`(由"真正把 DepthOnly 渲进
那张 RT 的地方"置位)。后者在阶段 4 接通渲染之前**恒为 false**,所以任何人现在打开
`OcclusionEnabled` 也只会退化为方案 A,不会毁掉雨。

**⑳ SP2 雨滴 shader 的"源码"在提取工程里存在,但内容是占位模板 —— 计划原文的提醒是对的。**
`sp2d4/Assets/Shader/Jundroo_ParticleConstantNormal_Soft.shader` 确实有文件,但头部标着
`//DummyShaderTextExporter`,内容是通用模板:
`vert` 就是 `mul(unity_MatrixVP, mul(unity_ObjectToWorld, pos))`、`frag` 就是
`_MainTex.Sample(...)`,**没有任何实例化 / 朝向 / 软粒子逻辑**,也没有 `#pragma target`。
**不能用它反推 SP2 的真实实现。**
唯一可靠收获是**属性名的权威来源**(Properties 块是第一手信息,不是导出器编的):

| 属性 | 实际类型 | 用途 |
|---|---|---|
| `_MainTex` | `2D` | 雨滴贴图(**RGB 有效、A 是 alpha** —— 与计划里写的 `_Tex` 不同名) |
| `_Emission` | `Float` | 自发光强度 |
| `_MainColor` | `Vector` | 染色(**不叫 `_Color`**) |
| `_InvFade` | `Range(0.01, 3)` | **软粒子因子** —— 计划 §5.2 猜的 `_InvFade` 名字是对的 |

本实现的 `RainParticles.shader` 用的是自己的一套名字(`_Tex` / `_Color` /
`_SoftParticleFade`),因为 C# 侧是自写的、本来就成对。若要靠拢 SP2 的原名以便对照,
改这两处即可(2 个文件里各 3 行)。

**㉑ 淡变重入守卫只看目标值 —— 这是"雨完全看不见"的真正根因(真机抓到的)。**
`Rain.SetTargetFade` 原来的守卫是:

```csharp
if (Mathf.Approximately(_targetFade, to) && Mathf.Approximately(_fadeMultiplier, to)) return;
```

淡入过程中 `_fadeMultiplier` 从 0 慢慢爬向 1,**永远不等于** to →
守卫不成立 → 每次调用都把 `_fadeElapsed = 0` → 进度永远归零 →
**淡变彻底卡死,`_fadeMultiplier` 恒为 0**。

触发它的是 `VolkenWeather.UpdateRain` **每帧**调用
`Rain.OnWeatherValueChanged(WeatherValue)` —— 本意是"不依赖事件时机",
但正好变成每帧一次重入。两者叠加 = 雨永远不可见。

**正确语义:只有"要去的目标变了"时才重新开始一段淡变**(目标没变就让它自己跑完):

```csharp
if (Mathf.Approximately(_targetFade, to)) return;
```

真机日志证据(`Player.log`,`[VolkenDiag]` 心跳):

```
Rain:HB raining=True wv=2.75 active=50000/50000 fade=0.000
        populated=True pendingDraw=False draws/s=0 everDrew=False
```

`populated=True active=50000` 说明**资源和管线都是好的**(打包没问题、compute/shader 都加载了),
`fade=0.000 + draws/s=0 + everDrew=False` 说明它从没画过 —— 问题 100% 在淡变。

**㉒ 心跳日志必须放在守卫之后,否则 `pendingDraw` 会误导。**
`_pendingDraw` 由 `RenderForCamera`(渲染阶段 onPreCull)消费置 false,
而心跳在 Tick(逻辑阶段)读它。若心跳排在守卫**之前**,而本帧被守卫提前 return,
心跳打出的就是**上一帧的残留值** —— 看起来像"Tick 走到了下发绘制"或反之,全是误导。
现在:守卫分支里显式 `_pendingDraw = false` 再打心跳,正常分支打心跳后继续。

**㉓ 相机切换分支里不能清 `_FadeAmount`。**
原实现在 `cam != _cam` 时 `_material.SetFloat("_FadeAmount", 0f)`。
一旦相机解析每帧返回不同实例(或首帧解析到临时相机),雨会被**永久清零**。
淡入淡出统一由 `_fadeMultiplier` 一条路径管理,分支里只更新 `_cam`。

**㉕ `Shutdown` 必须幂等 —— 第二个 fade 重置源。**
`VolkenWeather.UpdateRain()` 在**闸门关闭的每一帧**都调用 `_rain.Shutdown()`,
而旧 `Shutdown` 每次进来都 `ResetFade(0f)` + `FreeBuffers()` →
fade 被每秒重置 60 次,永生不起;buffer 还被反复申请释放。
改为:Shutdown 只在**首次**转入关闭时生效(内部 `_shutdownRequested` 标志),
且**不**把 fade 硬置 0,而是走 `SetTargetFade(0f, rainFadeOutDuration)` 自然淡出
(否则雨会"啪"地消失);buffer 留到淡出结束后由 `FinishShutdown()` 释放。

**㉖ 凡是"每帧都会调到的路径"里,禁止出现"重置动画进度"的副作用。**
这是本轮连踩两次的**同一类设计错误**,值得单独记一条:

| 位置 | 每帧触发的原因 | 副作用 | 修法 |
|---|---|---|---|
| `SetTargetFade` 守卫 | `UpdateRain` 每帧调 `OnWeatherValueChanged` | `_fadeElapsed = 0` | 守卫只看 `_targetFade` |
| `Shutdown` | `UpdateRain` 闸门分支每帧调 | `ResetFade(0f)` | `_shutdownRequested` 幂等 |

`ComputeOcclusionActive` 里的"穿出地面 1s 快速重入"(`ResetFade(0f)` + `SetTargetFade(1f,1f)`)
**严格遵守这条规则**:它只在 `aboveGround != _isAboveGround` 的**跳变沿**执行一次,
不是在稳态里每帧执行 —— 这是它能安全重置的原因。新增任何淡变/动画代码时照此自检:
**"这段代码一帧里会被调用几次?重置进度能不能被调用多次?"**

**㉗ 淡变状态的写点必须只有 3 处,且都要留痕。**
`Rain` 里能改 `_fadeMultiplier/_targetFade/_fadeFrom/_fadeElapsed` 的路径只有:
`SetTargetFade` / `ResetFade` / `AdvanceFade`。
为了不再靠推理定位"fade 为什么是 0",这三处现在都带 `[VolkenDiag]` 追踪:
- `Rain.SetTargetFade:` —— 每次目标变化打一行(带 from/cur),**安静 = 目标从没被设过**;
- `Rain.ResetFade:` —— 把 fade 从 >0.001 压到 0 时打一行,**并附调用栈**;
- `Rain.AdvanceFade:` —— 淡变过程中每 2 秒打一行进度(带 elapsed/duration/dt)。
  注意它写在 `_fadeMultiplier == _targetFade` 的**提前 return 之后** ——
  所以"日志里完全没有 `AdvanceFade:`"本身就是强信号:**`_targetFade` 从没被设成 1**。

**㉘ 心跳必须带淡变内部状态(`fadeState:`)。**
真机排查时最耗时的不是"看到 fade=0",而是**分不清**下面两种成因:

| 现象 | 成因 |
|---|---|
| `fade` 为 0 且 `fadeState.target=0` | 淡入**从没被触发** → 查 `OnWeatherValueChanged` 的 `_isRaining` |
| `fade` 为 0 但 `fadeState.target=1` | 淡入触发了但**被反复重置/进度不涨** → 看 `Rain.ResetFade:` 的调用栈、或 `dt` 是否为 0 |

所以 `Rain:HB` 行尾追加了
`fadeState: target= from= elapsed=/ dt= shutdown=`,一次就能分开这两类。

**㉙ 【架构缺陷】`UpdateRain()` 只在 `ApplyPlanet` 里调用,不在每帧的 `Tick` 里。**
这意味着"天气值 → 雨"的同步**只发生在行星加载/SOI 切换那一刻**。
后果:如果初始化时 `WeatherValue` 还没到 2.25(或配置还没加载好),
`OnWeatherValueChanged(2.75)` 这唯一一次调用就会用旧值走 else 分支 →
`_targetFade` 停在 0 → **之后无论天气怎么变,雨都不会开始淡入**。
即便当时值是对的,`Tick` 里也**没有任何机制**把新的天气值推给 `Rain`。

**㉚ `Tick` 的步骤顺序:`改状态的逻辑` 必须在 `画不画的判定` 之前。**
```csharp
// 正确顺序
ComputeOcclusionActive(...)   // ← 它会 ResetFade/SetTargetFade(穿出地面重入)
GetWindVelocity/AlignStreaks
AdvanceFade(dt)               // ← 无条件推进,不依赖自己的输出
// ...最后才:
if (_activeCount <= 0 || !_isPopulated || _fadeMultiplier <= 0.001f) return;  // 纯判定,不改状态
```
旧顺序把 `ComputeOcclusionActive` 放在守卫**之后** —— 而 `fade` 一旦卡在 0,
那段恢复逻辑就永远不会执行,**状态机被自己的守卫锁死**。
一般规则:**守卫只能读状态,不能挡在"改状态的逻辑"前面**。

**㉛ `_shutdownRequested` 必须在闸门重开时复位(否则雨永久失效)。**
`Shutdown()` 设 `_shutdownRequested=true`;它只由 `FinishShutdown()`(淡出结束)清除。
若闸门在淡出**完成前**又打开,该标志会一直留着 → 下一次 `Tick` 的 `FinishShutdown`
把 buffer 释放掉,而雨对象**不会**被重建(它不是 null)→ **雨永久失效**。
`UpdateRain()` 现在在闸门打开分支里调 `Rain.ClearShutdownRequest()`。

**㉟ 【真机踩坑】`RWStructuredBuffer<uint>` 既没有 `InterlockedAdd` 也没有 `Load4/Store4`
—— 计数器与间接参数 buffer 都必须是 `RWByteAddressBuffer`。**
compute 编译直接失败,而且是**两个**独立的错(先修了一个还有第二个):

```
Shader error in 'RainParticles': RWStructuredBuffer<uint> object does not have
method 'InterlockedAdd' at RainParticles.compute(243) (on d3d11)     [4728 次]
Shader error in 'RainParticles': RWStructuredBuffer<uint> object does not have
method 'Load4'        at RainParticles.compute(308) (on d3d11)       [1164 次]
```

Unity 的 HLSL 里,结构化 buffer **只支持元素级 `[]` 访问**;
`Interlocked*` / `Load/Store/Load4/Store4` 这套"字节地址/无类型"操作
**只对 `RWByteAddressBuffer` 可用**。

→ `_CulledCount`(计数器)与 `_ArgsBuffer`(间接绘制参数)**两个都改成 `RWByteAddressBuffer`**:
- `_CulledCount.InterlockedAdd(0, 1u, slot)` 不变(0 就是字节偏移);
- `_CulledCount[0]` → **`_CulledCount.Load(0)`**;
- `_ArgsBuffer.Load4/Store4(0, …)` 不变(本身就是字节地址语义)。

C# 侧**无需改动**:`new GraphicsBuffer(Target.Structured, count, stride)` 对两者都兼容。

⚠️ **教训(这条最值钱)**:这两个错是**同一个根因的两次发作**,我第一次只修了
`InterlockedAdd`(因为日志被刷屏,只看了第一组),于是**紫色照旧**。
根因是"我凭 D3D 习惯假设了 `RWStructuredBuffer<uint>` 拥有全部 uint 操作方法"。
**正确做法:一次性把该类资源的所有用法都换成同一套语义**,
并且把日志里的错误**按消息去重**(`Group-Object`),而不是只看头几条 ——
4728 行同一条错误会把别的错误淹掉。

**㊱ 【真机踩坑】compute 编译失败的连带后果:品红 + 严重掉帧。**
`FillArgs` 是唯一写 <c>_ArgsBuffer.instanceCount</c> 的地方。它一失败:
- 间接绘制参数**停留在 C# 初始化时写的值 = 容量** → 每帧硬画 `容量 × 12` 个顶点。
  实测容量 400000 → **480 万顶点/帧**,笔记本 GPU 直接掉帧;
- 剔除结果 buffer 没被写入 → 顶点着色器读到的位置是垃圾/零 → 退化几何 + 品红观感。

**结论**:**看到"能画但品红 + 掉帧",先查 compute 的编译错误,而不是先调参或怀疑显存。**
`Editor.log` 里搜 `Shader error` 一次就能定性 ——
**并且一定要按消息去重**(同一错误会对 6 个 kernel × 4 个平台各报一遍,
实测最多 4728 行,足以把第二条不同的错误完全淹掉)。

**㊲ 粒子上限从 SP2 的 100000 下调到 20000(真机实测)。**
SP2 的 `_baseAmount = 100000` 是**实例数**,而每个粒子要生成 2 个四边形 = **12 个顶点**,
所以顶点数 = `实例数 × 12`。在 4 倍粒子缩放下容量会到 400000 → 480 万顶点/帧。
雨是满屏高频细节,20000 实例(24 万顶点)的视觉密度已经够;
SP2 之所以需要更大数量,是因为它的粒子域半径只有 50m、
而本项目默认域半径更大、雨丝更长更粗,单个粒子覆盖面更大。
需要更密时调 `WeatherConfig.rainStrength`(不是这个常量)。

**㊳ GPU 回读自检默认关闭。** `GraphicsBuffer.GetData` 是**同步**回读,会强制 CPU 等 GPU 跑完这一帧 ——
常驻会造成周期性顿挫。改为默认关,需要时用 dev 命令
**`volkenRainProbe 1`** 打开 / `volkenRainProbe 0` 关掉。

### 10.2c0 域半径必须随相机速度自适应(2026-09-27)

> 真机日志直接指出:
> ```
> Rain: **相机速度 162 m/s 超过域半径 50m** —— 雨会表现为间歇/闪断
> Rain: **相机速度 348 m/s 超过域半径 50m** —— 雨会表现为间歇/闪断
> ```
> 同时 `Rain.Shutdown` / `Rain.ResetFade` 都是 **0 次** —— 说明**不是**淡入淡出在打断。
> 这条日志是上一轮加的,**它自己把问题指出来了**,是"加对日志"的回报。

**根因**

1. **停留时间**。域是**相机相对**的(粒子存相机偏移、`_domainPos` = 相机位置),
   所以粒子相对域的速度 ≈ **相机速度**。`域直径 / 相机速度` = 粒子在域内的停留时间。
   50m 半径 + 348 m/s → **0.29 秒**。
2. **密度**。域是**球**,体积 ∝ r³。粒子数固定时半径翻倍 → 密度掉到 1/8。
   **所以"把 radius 调大"反而更稀** —— 必须**同时**提高粒子数
   (具体标定见 §10.2c2)。

**修法**
```csharp
needed = camSpeed * 0.6f;                          // 停留 ≈ 1.2s
radius = clamp(max(configured, needed), configured, 400f);
densityScale = (radius / 50)^2.5f;                 // 见 §10.2c2 的 EVE 标定
amount = BaseParticleAmount × quality × rainStrength × densityScale;
```

**顺序要求(踩过)**:域半径必须在**算完相机速度之后、算粒子数之前**确定 ——
否则粒子数慢一帧、首帧用错值。现在的 `Tick` 顺序是:
`_spectatorVel`(相机速度)→ `EffectiveDomainRadius`(域半径)→
`ApplyParticleAmount`(粒子数,含密度补偿)→ `ComputeOcclusionActive` → `AdvanceFade` → 绘制判定。

**开关**:`volkenRainAuto 1|0`(默认 1)。关掉则完全按 `weather.xml` 的
`rainDomainRadius` 走(高速时必然失败,仅用于对照)。

### 10.2c2 粒子密度标定:EVE(blackrack 体积云)的实测配置(2026-09-27)

> 参照物:`C:\renko\RaymarchedVolumetricsEarlyAccess03_01_26 - 副本`
> —— EVE 的体积云预览版(KSP mod,含**完整的降水系统**)。
> 它的雨配置在 `GameData/StockVolumetricClouds/Clouds/particleFields.cfg`:

```ini
OBJECT
{
    name = rain-Kerbin
    randomDirectionStrength = 0.5
    particleStretch    = 20        // 靠拉伸成长条(而不是靠长网格)
    fieldParticleCount = 200000    // ★ 20 万粒子
    fieldSize          = 70        // ★ 域半径只有 70
    tangentialSpeed    = 0
    fallSpeed          = 12
    particleSheetCount = 6,1       // 贴图图集分格
    particleSize       = 0.03      // ★ 单个粒子极小
    color              = 255,255,255,80
    minCoverageThreshold = 0.15
    particleTexture { value = .../RainDropsSheet }
    splashes { ... }               // 落地水花(独立 sheet)
}
```

**关键换算:密度 ≈ 200000 / (4/3·π·70³) ≈ 0.139 个/m³。**

| 方案 | 粒子数 | 域半径 | 密度 | 相对 EVE |
|---|---|---|---|---|
| **EVE rain-Kerbin** | 200,000 | 70 | **0.139 /m³** | 1× |
| 本实现(最初) | 10,000 | 50 | 0.019 /m³ | 1/7 |
| 本实现(自适应半径生效后,曾) | 10,000 | 400 | **0.00004 /m³** | **1/3500** |

→ **"雨时有时无"的真正原因是密度太低,不是我先前以为的"闪断"。**
稀疏的粒子阵在移动时,肉眼就是时有时无。

**EVE 的其他可借鉴点**
- **域是固定尺寸(70m)的 3D 粒子场**,粒子数不随域变 —— 所以密度天然稳定;
  本实现的域**随相机速度自适应放大**,必须**显式补偿密度**(见 §10.2c)。
- **靠 `particleSize=0.03` + `particleStretch=20` 得到细长雨丝** ——
  即"小四边形 + 沿速度方向强拉伸",与本实现"四边形长度 = 拉伸 × 雨丝长度"同思路。
- **`particleSheetCount`**:雨滴贴图是**图集**(6×1 / 12×12 等),
  用 UV 分格做每粒子随机变体,避免所有雨丝长得一模一样。本实现目前是单张贴图 + 随机种子
  (种子已从 compute 传进 shader 的 `o.seed`,但**尚未用于 UV 分格**)—— 可作后续改进。
- **`randomDirectionStrength`**:雨滴方向的随机扰动强度(雨 0.5 / 雪 2 / 沙尘 10)。
- **`minCoverageThreshold`**:与云覆盖度联动 —— 覆盖度低于阈值就不下雨。
  本实现目前只按 `WeatherValue` 触发,可以后续接上。
- **落地水花是独立 sheet**(`splashes{}`),不在雨丝 pass 里。

**本实现的标定结果**
- `BaseParticleAmount` = 40000(域半径 50m 基准)→ 0.076 /m³(EVE 的约一半,
  考虑到本实现粒子更大更亮,观感密度可以略低);
- 密度补偿指数从 r² 提到 **r^2.5**(球体积 ∝ r³,r² 补偿不足;r^2.5 是折中);
- `MaxParticleAmount` 50000 → **400000**(400000 × 12 顶点 = 480 万顶点/帧,
  是本机 5060 Laptop 的可承受边界);性能由**画质档**显式控制;
- 新增密度诊断日志 `Rain.Density`,直接报出 `个/m³` 与**与 EVE 的比值** ——
  这是判断"够不够密"的直接指标,不用再靠肉眼。



> 真机日志直接指出:
> ```
> Rain: **相机速度 162 m/s 超过域半径 50m** —— 雨会表现为间歇/闪断
> Rain: **相机速度 348 m/s 超过域半径 50m** —— 雨会表现为间歇/闪断
> ```
> 同时 `Rain.Shutdown` / `Rain.ResetFade` 都是 **0 次** —— 说明**不是**淡入淡出在打断,
> 而是粒子被太快回收。这条日志(上一轮加的)**自己把问题指出来了**,是"加对日志"的回报。

**根因(两层,必须一起修)**

1. **停留时间**。域是**相机相对**的(粒子存相机偏移、`_domainPos` = 相机位置),
   所以粒子相对域的速度 ≈ **相机速度**。
   `域直径 / 相机速度` = 粒子在域内的停留时间。
   50m 半径 + 348 m/s → **0.29 秒** → 肉眼就是"雨一阵一阵地闪"。
2. **密度**。域是**球**,体积 ∝ r³。粒子数固定时,半径翻倍 → 密度掉到 1/8。
   **所以"把 radius 调大"反而更稀** —— 必须**同时**提高粒子数。

**修法**

```csharp
// 1) 半径自适应:取"配置值"与"相机速度 × 0.6"的较大者,上限 400m
needed   = camSpeed * 0.6f;                       // 0.6 → 停留约 1.6s,不再闪断
radius   = clamp(max(configured, needed), configured, 400f);

// 2) 密度补偿:粒子数 × (radius / 50)^2.5    ← 见 §10.2c2 的 EVE 标定
densityScale = (radius / 50)^2.5;
amount       = BaseParticleAmount × quality × rainStrength × densityScale;
```

> ⚠️ **修正(2026-09-27)**:本节的"闪断"归因后来被 §10.2c2 的密度标定修正为
> **主因是密度不足** —— 固定 10000 粒子在自适应放大的域里密度低到 EVE 的 1/3500。
> 两个原因都真实存在,所以两处都修了(半径自适应 + 密度补偿指数提到 r^2.5)。

- `0.6` 的来历:`直径/速度 = 2r/(r/0.6) = 1.2s`,保证停留 ≥ 1 秒级;
- 密度用 **r²** 而不是 r³:粒子只填相机前方的一个视锥壳层,
  可见数量 ∝ 屏幕覆盖 ∝ r²(经验折中);
- **`MaxParticleAmount` 从 50000 提到 200000**:高速时半径 200m+ 会算出十几万粒子,
  上限太小会**静默截断**并表现成"雨突然变稀"。性能改由**画质档**
  (`ModSettings.RainQuality` 的 0.25/0.5/1.0)显式控制,而不是靠隐藏钳制;
- 撞上限时打一条明确日志(否则"雨变稀"没有线索)。

**为什么不在 Update 里放** —— 半径必须在**算完相机速度之后、算粒子位移之前**确定
(位移钳制要用它),所以放在 `Tick` 里 `_spectatorVel` 之后。

**验收判据(心跳)**:`domainR=(eff)` 应随 `camSpd` 变化;`Rain:` 不应再出现
"域太小导致闪断"的告警(除非撞到 400m 上限)。

### 10.2d JNO 坐标系研究结论(2026-09-27,针对"雨横向 + 间歇")

> 起因:用户反馈"JNO 是航天游戏,导致雨滴间歇性 + 方向横向"。下面是读
> `C:\renko\shitProgram\jnoCode` 得到的**事实**(不是推测),以及由此定下的修法。

**A. JNO/SR2 没有任何风系统。**
`WindManager` / `WindVelocity` 在整个 `jnoCode` 仓库**零命中**;
`IPlanetAtmosphereData` 只提供 `CrushAltitude / Height / MeanGamma / MeanMassPerMolecule /
MeanSurfaceTemperature / HasPhysicsAtmosphere / PressureCurve` 等,`PlanetAtmosphereData`
有按高度算密度/气压的方法,**没有任何气流速度接口**。
→ 风只能来自 `CloudConfig.windSpeed/windDirection`(好处:云雨同向)。

**B. `FlightData.North/East` 是"飞船局部"的北/东,不能当风的地理方向用。**
`ICraftFlightData` 的文档写明:方向量在**行星位置坐标系**里,但它是**飞船所在处、随飞船姿态**的
北/东(`CraftForward/CraftRight/East/North` 是同一组"飞船局部方向")。
后果:
- 飞船一俯仰/滚转/偏航,**风的方向跟着转** → 雨丝方向随姿态乱摆(用户报的"横向雨");
- 极点附近 `up` 与自转轴平行 → `cross` 退化 → 方向变 NaN 或跳变。

**修法**:在行星位置坐标系里**解析构造地理北/东**,与飞船姿态完全解耦:
```
planetNorth = PlanetToFrameVector(0,1,0)          // 行星不自转,北轴就是 +Y
up          = normalize(framePos − frameCenter)
north       = normalize(projectOnPlane(planetNorth, up))     // 极点退化时取 up 的任意垂线
east        = normalize(cross(up, north))                    // 校验:up=+Y,north=+Z → east=+X ✓
windDir     = normalize(cos(dir)·north + sin(dir)·east)
```
副产品:风随行星自转一起走(行星坐标系本就随行星转),符合"地面上的风"的直觉。

**C. 风量级的换算原本没有依据,且会把雨吹成水平。**
旧代码 `windSpeed = cloudCfg.windSpeed * 4000f` —— 这个 4000 是我编的。
当 `windSpeed` 到 0.01 量级就得到 40 m/s 侧风,**与下落速度同量级**,雨丝自然接近水平。
→ 改为 `× 1000` 并**硬钳到 25 m/s**:0.01 → 10 m/s。
原则:**下落方向必须是雨丝的主方向,风只负责让雨丝倾斜**。

**D. 【已被 I 条修正】"朝向不能掺速度"曾经是错的结论 —— 但"两个速度要分开"这个结论是对的。**
> ⚠️ 本条最初写的是"雨丝朝向绝不能掺入相机/飞船速度",**这个结论后来被推翻了**:
> 按 SP2 权威源码,朝向**应该**减**玩家速度**(见 I 条)。
> 保留本条是因为它里面"两个速度各司其职"的拆解仍然有效,而且记录了一次错误判断的来龙去脉。

当时的现象:旧代码 `AlignStreaks(_combinedVel - _spectatorVel)`,而 `_spectatorVel` 来自
`Camera.velocity` —— **相机被瞬移/跟随时它会给出不真实的值**,导致雨丝乱摆。
我据此误判为"任何速度都不能掺",实际根因是**用错了速度源**(该用玩家速度,不是相机速度)。

**仍然成立的部分 —— 这个向量承担了两个不该合并的职责:**
| 职责 | 正确取值 |
|---|---|
| **雨丝朝向**(雨往哪下) | 空气相对运动 − **玩家速度** = `_combinedVel − _playerVel` |
| **粒子在域内的平移**(雨幕不粘在原地) | 空气相对运动 − **相机速度** = `_combinedVel − _spectatorVel` |

拆成两个量分别使用(见 I 条):`AlignStreaks(_combinedVel - _playerVel, cam)` +
`_particleVel = dt × (_combinedVel − _spectatorVel)`。

**E. 间歇性的成因:域尺寸与相机速度不自洽。**
域是**相机相对**的(粒子存偏移、`_domainPos` = 相机位置)。相机速度一旦远超域半径,
粒子每帧被推移的距离接近/超过域直径 → 几帧内穿过整个域并回绕 → **雨幕闪断**。
航天器速度动辄几百 m/s,而默认域半径只有 50m,这个问题必然出现。
→ 两处处理:
1. **粒子位移硬钳到"半域/帧"** —— 保证粒子在域内至少停留 2 帧,回绕不变成闪烁;
2. 相机速度 > 域半径时打一条**明确的诊断日志**,直接建议把
   `weather.xml` 的 `rainDomainRadius` 调到相机速度的 2~3 倍。

心跳新增 `| vel: camSpd= combined= wind= playerVel=` —— 一眼看出相机速度与飞船速度的量级,
用来确认"横向"和"间歇"是不是速度引起的。

**F. 【真机第二轮】"雨还是横着 + 随相机朝向位移" → 雨丝四边形必须**面向相机**(billboard 十字)。**
修完 A~E(物理量全部正确:长度轴严格竖直、不含任何相机量)之后,用户反馈**雨仍是横向**
且**随相机朝向变化**。说明问题不在物理量,而在**几何/基向量**。

根因:旧实现用两个**世界空间固定**的平面搭"十字":
| 片 | 宽度轴 | 长度轴 | 平面法线 |
|---|---|---|---|
| 0 | `side` | `dir` | `other` |
| 1 | `other` | `dir` | `side` |

`side`/`other` 都由 `dir` 与世界 up/forward 推出,**随每帧的 dir 抖动**。
后果两个,正好对上反馈:
1. 相机视线一旦接近某一片的**平面内方向**,那片就**退化成正对边缘 → 完全看不见**
   → 看上去是"雨一闪一闪"(间歇);
2. 两片的可见性随视角此消彼长 → 看上去是"雨滴随相机朝向变化/位移"。

**正确做法:把宽度轴建立在垂直于视线的平面里 —— 两片四边形都正对相机。**
```csharp
lengthAxis = normalize(fallDir)                    // 长度轴:只由物理量决定
side  = normalize(cross(lengthAxis, cam.forward))  // 宽度轴:垂直视线 → billboard
other = normalize(cross(lengthAxis, side))         // 第二片:与 side 正交,真正的十字
```
**一般规则:"长度/朝向"轴只由物理量决定,"宽度"轴由视线决定 —— 这两个绝不能混。
混了就是"横向雨";全都用世界固定基就是"视锥边缘消失/闪烁"。**

**I. 【真机第四轮 · 决策落定】雨丝朝向按 **SP2 语义**(减玩家速度),但用**屏幕空间表达**。**
用户明确选择"SP2 语义(雨丝随飞行速度向后倾)"。

**先把两个速度概念彻底分开**(之前混用是多次返工的根源):
| 用途 | 正确来源 | 理由 |
|---|---|---|
| **雨丝朝向** | `_combinedVel − **玩家速度**` | SP2 `ParticleHandler.GetPlayerVelocity()` → JNO 对应 `ICraftNode.Velocity` |
| **粒子在域内平移** | `_combinedVel − **相机速度**` | 域是相机相对的,不减相机速度粒子会被甩出域 |

⚠️ `Camera.velocity` **不能**当玩家速度用:相机被瞬移/跟随时它会给出不真实的值(实测导致雨丝乱摆)。

**为什么不能照搬 SP2 的世界空间方向**:`风+重力−玩家速度` 在 100 m/s 前飞时几乎水平
(与竖直约 67°,**物理正确**)。但把这个方向投影到屏幕平面后,竖直分量只剩 ~1/10
→ 屏幕上就是"横向雨"。**观感由投影决定,不由世界空间几何决定**(这是 H 条的结论)。

**折中实现**(`Rain.LeanGain`,默认 **0.5**,dev 命令 `volkenRainLean` 可实调):

⚠️ **第一版数学写错了,而且真机表现极具迷惑性**:写成
`hUp·dot(rel,hUp) + hRight·dot(rel,hRight) + hFwd·(dot(rel,hFwd)·g)`。
因为 `hUp/hRight/hFwd` 是**一组正交基**,前两项加上 `hFwd·dot(rel,hFwd)` 恒等于 `rel` ——
**那个增益根本没抑制前后分量**,方向仍然是 `normalize(rel)`。
后果:`rel = 风+重力−玩家速度` 在高速飞行时几乎**沿视线**,于是所有雨丝都指向
同一个消失点,屏幕上呈现**从中心径向爆散的星芒**(用户截图确认,第一眼会被误当成"横向")。

**正确的拆法**(屏幕内竖直 / 屏幕内横向**按原值**,前后分量**单独乘增益**):
```csharp
rel    = 风 + 重力 − 玩家速度                          // SP2 语义
upComp    = dot(rel, −camUp)      // 屏幕内"向下"分量
rightComp = dot(rel,  camRight)   // 屏幕内横向分量
fwdComp   = dot(rel,  view)       // 前后分量(沿视线)
lengthAxis = normalize( −camUp·upComp + camRight·rightComp + view·(fwdComp · LeanGain) )
```
注意 `lengthAxis` 必须是 `−camUp` 与 `view` 的**线性组合**(而不是 `hUp+hFwd·g` 那种写法),
这样增益才真正改变屏幕上的倾角。
| LeanGain | 前后分量权重 | 与"屏幕下方"夹角 |
|---|---|---|
| 0 | 0 | **0°(永远竖直)** |
| 0.5(默认) | 0.5 | **≈26.6°** |
| 1(照搬 SP2) | 1 | **45°** |

**J. 隔离测试开关必须是 `static`。**
`DebugFixedVertical` 原本是实例字段 → dev 命令写的是 `VolkenWeather._rain` 那个实例,
而实际绘制可能发生在**另一个** Rain 实例上 → **"开了等于没开"**
(真机日志证实:`SetRainFixedVertical` 打印了 True,但 `Rain.Streaks` 全是普通模式的值)。
和 `_activeInstance` 那次是**同一类坑**:调试开关/全局状态做成静态,天然对全部实例生效。
同时加了 `DebugFixedVerticalHits` 计数器 + **绕过节流**的即时日志,用于立刻确认开关生效。

**G. 【配套】基向量必须在**每次下发绘制前**重设,不能在 Tick 里写一次。**
实测 `drew≈142/s` 而 `Tick≈20/s` —— 雨会在**多台相机**上各画一次
(`OnPreCull` 每台相机都触发)。材质 uniform 是**全局共享**的,
只在 Tick 写一次的话,后画的相机会用上被别的相机覆盖过的基向量
→ 又一层"随视角漂移"。
→ 拆成 `AlignStreaks()`(算,存字段)+ `ApplyStreakBasis()`(写材质),
在 `RenderForCamera` 里**每台相机各调一次**;
`_DomainPos`/`_CamPos` 同理改成绘制时按当前相机设置。

**H. 【真机第三轮 · 关键】雨的"长"必须在**屏幕平面内**表达 —— 这是"还是横的"的真正根因。**
F 改成 `cross(dir, 视线)` 之后不再闪烁,但用户反馈**"还是横的"**。
真机日志给出了决定性的数:

```
lengthAxis=(0.01,-1.00,-0.01)      ← 世界空间里是竖直的 ✓
side=(0.75,0.00,0.66)  other=(-0.66,-0.02,0.75)   ← 两者 Y≈0,都**水平**
dot(lenAxis,view)=0.988            ← 视线几乎正对下落方向(俯视)
```

物理量全对,问题在**投影**:
- 雨丝长度 2.5m 是**垂直**的,而宽度 0.1m 是**水平**的;
- 当 `dot(dir,view)→1`(俯视/仰视),垂直方向的长度**在屏幕上被透视压缩到 ≈ 0**,
  而水平方向的宽度是**完整投影**的;
- 于是屏幕上得到的是"0.25m × 0.1m 的一小横块" —— **观感就是横向雨**。
- 附带:`cross(dir,view)` 在两者接近平行时数值退化,`side` 会在近水平面里乱转。

**正确做法(第 4 版,标准屏幕对齐 billboard)**:
```csharp
lengthAxis = normalize(fall − view · dot(fall, view))   // 下落方向在**屏幕平面内**的分量
side       = cam.transform.right                        // 宽度严格横跨屏幕
other      = cam.transform.up                           // 第二片,构成真十字
// 视线与下落方向近平行时投影退化为 0 → 用 -camUp 兜底(屏幕上仍是朝下的线)
```
俯视时雨丝在屏幕上仍会**变短**(那是真实的透视缩短),但它**永远竖直地躺在屏幕上**,
不会再变成横块。

**一般规则(这条最通用)**:**任何"细长"的 billboard(雨丝/拖尾/激光)——
"长"必须在屏幕平面内表达。**用世界空间长度轴 + 任意宽度轴,
视线一旦接近长度轴就必然退化成"横块"。观感由**投影**决定,不由世界空间几何决定。

心跳/`Rain.Streaks` 诊断现在直接给出判据 **`长宽比` = 屏幕长 / 屏幕宽**:
> 3 才像雨丝。这个比值可以直接判断"横不横",不用靠肉眼。



**㉝ 【真机定位】实例回调工作正常,但被一条**多余**的静态守卫 100% 挡掉。**

换成 `RainCameraRenderer` 的实例 `OnPreCull` 之后,日志第一次出现
`实例 OnPreCull 首次触发` + `preCullCalls` 持续增长(≈20/s)——
证明**实例回调这条路是通的**(静态 `Camera.onPreCull` 不触发,见 ⑪)。

但 `draws/s` 仍然 0。加了**逐守卫计数器**后一行定位:

```
rfc/s: calls=82 notActive=82 notPending=0 camMismatch=0 noBuffers=0 stateSkip=0 drew=0
```

`calls == notActive`(100% 命中),即每次都死在
`RenderForCamera` 开头的 `if (_activeInstance != this) return;`。

**根因**:`_activeInstance` 是**静态字段**,那条守卫是为**静态**转发写的。
而 `RainCameraRenderer` 挂在相机上、持有某个 `Rain` 实例的引用;
当 `VolkenWeather` 重建 `Rain`(新实例)后,`_activeInstance` 指向新实例,
相机上的渲染器却还指着**旧**实例 → 旧实例的每次回调都被这条守卫挡掉
(是"第二个实例在给自己开火")。

**修法**:
1. **删掉** `RenderForCamera` 里的 `_activeInstance != this` 守卫 ——
   实例回调靠下面的 `_cam` 配对就够了;静态路径的防重入判断留在 `OnPreCullStatic` 里;
2. `AttachCameraRenderer` 即使相机没变,也要**校正 `Owner` 指向当前实例**
   (原来只在"相机变了"时才重建,所以 Owner 会一直留在旧实例上)。

**方法论**:`LogThrottled` 的 5 秒节流让守卫日志在 1 秒的心跳窗口里可能完全看不到 ——
**排查"函数到底进没进、被哪条挡了"时,累计计数器 + 按秒输出差值才是可靠手段**,
不要依赖节流日志。

**㉞ 【真机验证通过】淡变链路已确认正常(2026-09-27 日志)。**

```
Rain.SetTargetFade: 0.000 → 1.000 用时 20.00s (当前 fade=0.000, from=0.000)
Rain.AdvanceFade: cur=0.000 target=1.000 from=0.000 elapsed=0.00/20.00s dt=0.0219
Rain:HB ... fade=0.050 ... pendingDraw=True draws/s=0 everDrew=False
       | fadeState: target=1.000 from=0.000 elapsed=1.01/20.00 dt=0.0496 shutdown=False
Rain:HB ... fade=0.765 ... pendingDraw=True draws/s=0 everDrew=False
       | fadeState: target=1.000 from=0.000 elapsed=15.30/20.00 dt=0.0399 shutdown=False
```

`fade` 从 0 单调爬到 0.765,`ResetFade` **0 次**(没有东西再压它),`target=1.000`。
**至此"淡变卡死"这一类问题全部解决**,剩下的唯一断点是绘制回调 ——
后来由 ㉝ 定位到是实例回调被多余的静态守卫挡掉,已修。
这也说明 §10.4 的追踪方案有效:一条 `fadeState:` 就把
"淡入从没触发(`target=0`)"和"触发了但被反复重置(`target=1` 但 `fade` 不涨)"分开了 ——
本次是前者(㉙ 的架构缺陷),一眼定性,没有再来回猜。

**㉔ 【决策】天气系统**不**联动云层 —— 云厚度/覆盖度/浓度/颜色/风速全部由云自己决定。**
2026-09-27 用户决定。曾实现过 `ApplyCloudCoupling`(覆盖度 / 云色暗化 / 风速,
从基线纯函数重算,增益默认 0),代码 + UI + 本地化 + `WeatherConfig` 字段**已全部移除**。

理由:联动会让玩家在云面板上调好的云形被天气悄悄改掉 —— 调参结果不稳定,
而且分辨不出"看到的是我调的,还是天气改的"。**云的观感归云,雨的观感归雨。**
如果将来真要联动,必须做成面板里能明确关掉、且默认关的显式选项,不能是隐式行为。
(注:`WeatherConfig` 里留了两行注释说明这三个字段为何移除、以及万一旧 XML 带同名节点
该怎么处理 —— 与 `CloudConfig.low/mid/highAltitudeThreshold` 那个"死配置不可删"的坑同源。)



### 10.2e 【重大变更】雨与雾整体移除,只保留雷电(2026-09-27,用户决定)

> 经过多轮真机调试,雨的方向问题(横向/径向爆散/随视角漂移)始终未能收敛。
> **用户决定:移除雨与雾的全部组件,只保留雷电,准备重新开始。**

**已删除的文件**(已 stash 到 `%TEMP%\volken-rain-fog-stash`,需要时可取回)

| 文件 | 说明 |
|---|---|
| `Assets/Scripts/Volken/Weather/Rain.cs` | 雨系统主体(约 2000 行,含 GPU 粒子域/间接绘制/淡变/风/密度自适应) |
| `Assets/Scripts/Volken/Weather/RainParticles.compute` | 6 内核计算管线 |
| `Assets/Scripts/Volken/Weather/RainParticles.shader` | 雨丝 billboard(程序化绘制) |
| `Assets/Scripts/Volken/Weather/FogRenderer.cs` | 全屏高度雾组件 |
| `Assets/Scripts/Volken/Weather/HeightFog.shader` | 高度雾解析积分 |
| `Assets/Scripts/Volken/Weather/Audio/enviro_rain_1~3.ogg` | 环境雨声 |

**被清理的引用**

- **`Assets/ModData.asset`(打包清单的权威来源)—— 这是最关键的一处,容易漏。**
  `ModTools` 的 `ModData._otherAssets` 存的是**资产 GUID 列表**,构建 mod 时按 GUID 解析路径写进
  `Temp\ModManifest.xml` 与 `ModAssetBundles\StandaloneWindows64\volken.manifest`。
  **删掉资产文件不会让 GUID 自动消失** —— 实测删除文件后重新生成的 manifest 里**仍然带着这 6 条**:
  `enviro_rain_1/2/3.ogg`、`HeightFog.shader`、`RainParticles.shader`、`RainParticles.compute`
  (GUID `f34a10ca…` / `ce009423…` / `98bbe0bd…` / `f0ec7a8f…` / `9e3e2564…` / `f4593250…`)。
  → 已手工从 `_otherAssets` 删除这 6 条;`_otherAssets` 现为 **20 项**,已逐条复核
  **每个 GUID 都能在 `Assets/` 下解析到真实文件(0 个悬空)**。
  ⚠️ 重做雨/雾时**要把 GUID 加回来**,或者从 stash 恢复文件后在 Unity 里重新指向。
- `Mod.cs` —— 移除 `volkenRainProbe` / `volkenRainAxis` / `volkenRainLean` / `volkenRainAuto`
  四个 dev 命令与 `SetRainAuto` / `SetRainLean` 方法;`WeatherStatus` 去掉 rain/fog 两行;
- `ModSettings.cs` —— 移除 `RainQuality` / `FogEnabled` 两个设置与 `FormatRainQuality`;
- `WeatherConfig.cs` —— 移除全部 rain*/fog* 字段(共 21 个)及其 `ClampAll` / `CopyFrom` 条目;
- `WeatherPanel.cs` —— 移除 `BuildRainGroup` / `BuildFogGroup`;
- `VolkenMod.cs` —— 移除 `MountFogRenderer` 与两处调用;
- 三语言文件 —— 移除 `RainQuality`/`FogEnabled` 设置文案与 `Rain*`/`Fog*` 面板文案(各约 30 行);
- `CloudRenderer.LinearSceneDepth` —— **保留但已无消费者**(它当初是为雾加的),注释已说明。

**保留的东西(以及为什么)**

- **雷电全链路**:`LightningBolt.cs` / `LightningModule.cs` / `LightningBolt.shader` / 5 条雷声;
- **天气状态机**(`VolkenWeather` + `WeatherConfig` + `WeatherTypes`):`WeatherValue` 标度、
  随机天气规则、淡变、SOI 重载 —— 雷电靠它驱动;
- `WeatherTypes.RainTrigger` / `IsRaining` / `foggyDawn`(黎明起雾):**现在没有渲染消费者**,
  只是天气标度里的分档与状态机行为。保留是因为它们是天气系统的一部分,
  且重做雨/雾时是最自然的触发判据(SP2 也是 2.25)。
  ⚠️ 别误以为"改了 `foggyDawn` 会起雾" —— 雾的渲染已删除。

**这次的经验教训(供重做时参考)**

> 📌 **完整复盘已独立成文:[`Volken-天气雨雾移植失败教训-2026-09-27.md`](Volken-天气雨雾移植失败教训-2026-09-27.md)**
> —— 4 层根因 / 27 条铁律 / 8 条已证伪思路 / 量化基线 / 重做起步清单。**重做雨/雾前必读。**
> 下面只是提要。

雨的方向问题踩了一长串坑,按发现顺序:

1. compute 里 `RWStructuredBuffer<uint>` 不支持 `InterlockedAdd` / `Load4`(要 `RWByteAddressBuffer`)→ 编译失败 → 品红 + 掉帧;
2. `Camera.onPreCull` 静态委托在本工程**从不触发** → 改用实例 `OnPreCull`;
3. 一条给静态转发写的 `_activeInstance != this` 守卫把实例回调 100% 挡掉 → **draws=0**;
4. 淡变被反复重置(守卫看当前值 / `Shutdown` 非幂等)→ **fade 恒 0**;
5. `UpdateRain` 只在 `ApplyPlanet` 调、不在每帧 `Tick` 调 → 天气值推不进雨;
6. 雨丝朝向:掺相机速度 → 横向;世界固定基 → 视角退化;沿视线 → **径向爆散**;
7. 密度:域随速度放大而粒子数固定 → 稀到"时有时无"(EVE 对照见 §10.2c2);
8. 诊断本身量错对象(用**世界空间夹角**当"屏幕倾角")→ 多轮定位不到。

**最值钱的三条通用结论**
- **观感由投影决定,不由世界空间几何决定** —— 细长 billboard 的"长"必须在**屏幕平面内**表达;
- **"长度/朝向"轴只由物理量决定,"宽度"轴由视线决定** —— 两者混用必出错;
- **每帧都会调到的路径里,禁止出现"重置动画进度"的副作用** —— 重入守卫只能看目标值。



### 10.2f 天气配置一度改为「照抄云层」的多预设方案(2026-09-27 末)

> ⚠️ **本节方案已被 §10.2g 取代,仅作决策记录保留。** 现在的做法是"天气参数内联进 `PlanetConfig`,
> 与云层共用一份 `PlanetConfigList.xml`、**没有天气自己的清单、没有多预设**"。
> 本节里"另开 `UserData/VolkenWeatherConfig/` 目录"、`PlanetWeatherConfigList`、
> `WeatherPanel` 的「当前配置 / 保存为新配置 / 加载配置下拉」**都已不存在**。
>
> 仍然有效、且被 §10.2g 继承的部分:
> - **按 Section 组织配置**(㊵)—— 5 个嵌套 `[Serializable]` 类 + 逐块 `CopyFrom`;
> - **面板分组与配置节点一一对应**(㊶)—— 含"雨/雾/云层联动整组禁用而不是隐藏"这条判断;
> - **`Config` 是引用而不是副本**这条语义(㊼);
> - **落盘与内存分离**(改内存 → 手动保存)这条语义。

> 【用户要求】"天气分成 4 个部分:总体,类似云层配置的总开关和云层联动机制窗口以及雨、雾、雷;
> 序列化 config 和 UI 应该保持一致";"应该模仿云层的序列化,在 UserData 创建一个
> VolkenWeatherConfig,用一个文件保存雨雾雷的设定,考虑到雨和雾已被移除,保留占位符即可,
> 相关逻辑直接抄云层配置即可"。

**㊴【重构(已部分撤销)】天气配置的落盘方案一度换成云层那一套。**

改前的形态是"每行星固定一份 `UserData/VolkenConfig/{行星}/weather.xml`" —— 与云配置**共用目录**,
且**没有清单文件、没有多预设**(云层是"每行星多份具名预设 + 一份清单记录当前用哪个")。

改后(与云层一一对应):

| 层面 | 云层(既有) | 天气(本节方案,已撤销) |
|---|---|---|
| 配置类 | `CloudConfig` | `VolkenWeatherConfig` |
| 清单类 | `PlanetConfigList` | `PlanetWeatherConfigList` ← **已删除** |
| 清单文件 | `UserData/VolkenConfig/PlanetConfigList.xml` | `UserData/VolkenWeatherConfig/PlanetWeatherConfigList.xml` ← **已不读不写** |
| 预设文件 | `UserData/VolkenConfig/{行星}/{预设}.xml` | `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` ← **已不读不写** |
| 默认预设名 | `Default` | `Default` ← **已不存在** |
| 枚举预设 | `CloudConfig.GetAllConfigNames(planet)` | `VolkenWeatherConfig.GetAllConfigNames(planet)` ← **已删除** |
| 落盘 API | `SaveToFile(planet, name)` / `LoadFromFile(planet, name)` | 同上(签名一致)← **已删除** |
| 当前预设名 | `CloudLayer.currentConfigName` | `VolkenWeather.CurrentConfigName` ← **已删除** |
| 面板 | 「配置管理」组:当前 / 保存 / 另存为 / 加载下拉 / 重置 | 曾同形 ← **只保留 保存 / 重置 / 路径** |

**为什么(当时)另开文件夹而不是继续共用 `VolkenConfig/`**:云与天气都是"按文件名枚举预设",
放同一目录会让两套预设名互相污染(给云起名 `Stormy` 会出现在天气的下拉框里,反之亦然),
而清单文件本来就已经把两者分开了。
**→ 后来的结论(§10.2g)是:只要"天气改成一棵内联子树、不再有自己的预设名"这个问题就自动消失,
所以还是并回一份文件更简单。**

**㊵【重构】配置类按「5 个 Section」组织,XML 节点 = 面板分组 = 数据块,三者同名同序。**

```xml
<VolkenWeatherConfig>
  <Overall>      <!-- ① 总体:总开关 / 动态天气 / 天气节奏 / 黎明起雾 -->
  <CloudLinkage> <!-- ② 云层联动机制(占位,默认全 0 = 恒等) -->
  <Rain>         <!-- ③ 雨(占位;实现已移除) -->
  <Fog>          <!-- ④ 雾(占位;实现已移除) -->
  <Lightning>    <!-- ⑤ 雷(唯一在跑的子系统) -->
</VolkenWeatherConfig>
```

实现方式:每块是一个 `[Serializable]` 的**嵌套类**(`OverallSection` / `CloudLinkageSection` /
`RainSection` / `FogSection` / `LightningSection`),各自带一个 `CopyFrom(同类)`。
好处是**"新增一个字段要同步改哪些地方"收在离字段最近的一处** ——
`CopyFrom` 写错只会丢一个字段,不会像原来那样"扁平 30 个字段挤在一个方法里,漏一行看不出"。

⚠️ `copyFrom` 用**逐块委派**而不是 `MemberwiseClone`:嵌套类实例是**引用**,
浅拷贝会让新旧配置共享同一个 Section 对象,`CopyFrom` 之后改一个等于改两个。

**㊶【重构】面板与配置保持一致的分组,雨/雾/云层联动三组**整组禁用**。**

面板结构(顺序与 XML 节点顺序一致):

| 面板分组 | 状态 | 说明 |
|---|---|---|
| 状态(只读) | 活 | 行星 / 天气值 / 运行状态 / 海拔+太阳时(~~预设名~~ ← §10.2g 撤销) |
| ① 总体 | 活 | 全局总闸提示 + 行星开关 + 动态天气 + 强制天气值 + 天气节奏 + 黎明起雾 |
| 配置管理 | 活 | ~~与云层「配置管理」同形~~ → §10.2g 只保留 **保存当前 / 重置为默认 / 配置路径(只读)** |
| ② 云层联动(未实现) | **整组禁用** | `enabled` / `coverageGain` / `darkenGain` / `windGain`,默认 0 |
| ③ 雨(已移除,占位) | **整组禁用** | 10 个占位参数(数量/域半径/自适应/落速/强度/风影响/雨丝长宽/雨声音量) |
| ④ 雾(已移除,占位) | **整组禁用** | 8 个占位参数(底高/厚度/密度/高度衰减/最大不透明度/起雾距离/雾色混合) |
| ⑤ 雷 | 活 | 原有 15 个参数 + 立刻劈一道 + 落雷状态 |

**为什么"禁用"而不"隐藏"**:用户明确要求雨雾两组保留占位;而且
`ItemModel.Enabled = false`(**基类属性**,`TextModel/SliderModel/ToggleModel` 都有)
比"不画这一组"更诚实 —— 玩家能看到"这里将来会有雨的参数",而不是以为功能丢了。
每组的第一行是灰字说明("雨系统已整体移除待重做,以下参数为占位,修改不会有任何效果")。

**㊷ API(本节方案;§10.2g 已裁掉多预设相关的那几个)**

| 成员(本节方案) | 现状 |
|---|---|
| `static PlanetWeatherConfigList ConfigList` | ❌ 已删 |
| `CurrentConfigName` / `AvailableConfigs` | ❌ 已删 |
| `LoadConfigPresetInPlace(name, resetState)` | ❌ 已删 |
| `SaveConfigAs(newName)` | ❌ 已删 |
| `SaveCurrentConfig()` | ✅ **保留**(改为写整份 `PlanetConfigList.xml`) |
| `ResetCurrentConfigToDefault()` | ✅ **新增**(§10.2g 里为"就地重置"补的) |
| `PlanetConfigList.GetWeather(planet)` | ✅ **新增**(§10.2g:取/补这条记录的天气参数) |
| `ApplyPlanet` 内部 | ✅ 改为"从清单那条记录上取 `Weather` 引用" |

面板侧:`WeatherPanel.Build(inspectorModel)` —— §10.2g 之后**去掉了第二个 `Action` 参数**
(不再有"换预设要重建下拉框"的需求)。

**㊸ 旧文件无兼容负担 —— 但这是"运气",不是"设计"。**

`VolkenConfig/{行星}/weather.xml`(旧方案)在真机上**从未产生过**(改之前 `weather.xml` 一次都没落盘,
实测 `Get-ChildItem -Recurse -Filter weather.xml` 零命中),因此本次重构**不需要迁移逻辑**。
⚠️ 但这意味着**没有任何东西验证过"旧扁平 XML 能否被新嵌套类读出来"** ——
如果将来发现有玩家手上有旧的扁平 `weather.xml`,症状会是`XmlSerializer` 把整段当未知元素忽略、
所有字段回落到默认值(全部关闭),**不会报错**。README 决策表里已记这条。

**编译验证**:`dotnet build Volken.csproj -t:Rebuild` → **0 错误**(6 个既有警告);
`WeatherPanel` 引用的 **88 个本地化 key 在 EN-US / ZH-CN / RU-RU 三份文件里全部存在**(脚本核对)。

### 10.2g 【再重构】天气配置合并进 `PlanetConfig` —— 一份记录同时记云层与天气(2026-09-27 末)

> 【用户要求】"把 `VolkenWeatherConfig` 合并到 `PlanetConfig`,让一个类记录天气和云层就行"。
> 这条**推翻了 §10.2f ㊴ 的一半** —— 那份"另开 `UserData/VolkenWeatherConfig/` 目录 +
> 独立清单 + 多预设"的方案只活了一次改动。**记录如下,以免将来又有人照 ㊴ 去实现。**

**㊹【最终方案】天气参数内联进 `PlanetConfig`,与云层共用一份清单文件。**

```
UserData/VolkenConfig/PlanetConfigList.xml          ← 唯一的行星配置文件
  <PlanetConfigList>
    <Configs>
      <PlanetConfig PlanetName="Droo" CloudConfigName="Default" ExtraCloudConfigName="Atmospheric">
        <Weather>                                    ← 天气参数就在这条记录里
          <Overall>…</Overall>
          <CloudLinkage>…</CloudLinkage>
          <Rain>…</Rain>
          <Fog>…</Fog>
          <Lightning>…</Lightning>
        </Weather>
      </PlanetConfig>
    </Configs>
  </PlanetConfigList>

UserData/VolkenConfig/{行星}/{预设名}.xml            ← 云层的参数本体(保持不变)
```

**职责划分(这次的关键设计判断)**

| 数据 | 存在哪 | 为什么 |
|---|---|---|
| 云层的**参数本体** | 独立文件 `{行星}/{预设名}.xml` | 一层可以有多套具名预设,`CloudConfig` 结构不动 |
| 云层**用哪套预设** | `PlanetConfig` 的属性(`CloudConfigName` / `ExtraCloudConfigName`) | 原样保留 |
| 天气的**参数本体** | **内联在 `PlanetConfig.Weather`** | 一颗行星只有一套天气,没有"多预设"的必要 |

**㊺ 类型与文件的重排**

| 变化 | 说明 |
|---|---|
| `Core/PlanetConfig.cs` **删除** | 其内容(`PlanetConfig` 类)并入下面的文件 |
| `Core/PlanetConfigList.cs` **新增** | `PlanetConfigList` + `PlanetConfig`(两者同处一个文件,因为"一份记录同时记云与天气"这件事必须在一处才看得清) |
| `Weather/VolkenWeatherConfig.cs` **删除** | 其内容(天气配置类)并入 `Core/PlanetConfigList.cs`(仍保留 `VolkenMod.Weather` 命名空间) |
| `Weather/WeatherConfigList.cs` **删除** | 那份 `PlanetWeatherConfigList` / `PlanetWeatherConfig` 整套作废 —— 天气不再有自己的清单 |
| `UserData/VolkenWeatherConfig/` | **不再创建、不再读取**(若磁盘上已有旧目录,会变成孤儿目录,可手删) |

**㊻ 去掉的东西(㊴ 里加过、现在全部撤销)**

- `PlanetWeatherConfigList` / `PlanetWeatherConfig` 两个类;
- `VolkenWeather.ConfigList`(静态清单)、`CurrentConfigName`、`AvailableConfigs`;
- `LoadConfigPresetInPlace` / `SaveConfigAs`(多预设的"加载/另存为");
- 面板上的**「当前配置」只读行 / 「保存为新配置」按钮 / 「加载配置」下拉框**;
- `VolkenWeatherConfig` 上的 `CONFIG_FOLDER` / `DefaultFileName` / `SaveToFile` / `LoadFromFile` / `GetConfigPath` / `GetAllConfigNames`(**落盘职责移交 `PlanetConfigList`**);
- 三语言里的 `Volken.UI.WeatherPreset`。

**留下的**:面板「配置管理」组只剩 **「保存当前配置」/「重置为默认」/「配置路径(只读,指向 `PlanetConfigList.xml`)」**
—— 与云层语义一致(**改内存 → 手动保存**)。

**㊼ `VolkenWeather.Config` 现在是「清单里那条记录上的实例本身」。**

```csharp
// VolkenWeather.ApplyPlanet 里:
Config = list.GetWeather(planetName);   // ← 引用,不是副本
```

`PlanetConfigList.GetWeather(planetName)` 会**就地补全**:没这条记录 → 建一条
(`CloudConfigName = "Default"`);记录没有 `<Weather>` 节点 → 挂一份默认(全关)。它**不落盘**,
落盘只发生在两处:玩家点"保存当前配置",或云层的 `AddConfig`/`SetConfig` 顺带写整份清单。

⚠️ **这个"引用语义"是有代价的**:面板一改就**立刻改了清单内存对象**,所以
「重置为默认」必须显式实现(不能靠"丢掉引用重新读盘")。这也是为什么
`ResetCurrentConfigToDefault()` 用 `CopyFrom(CreateDefault())` **就地写**而不是换引用
—— 换引用会让 `Config` 指向一个游离对象,与清单脱钩。

**㊽ `PlanetConfigList.AddConfig` 顺带修掉一个真 bug(合并后才会发作)。**

原实现是**无条件** `configList.Add(new PlanetConfig(...))` —— 对已存在的行星会**追加第二条同行星记录**。
云的调用点都先用 `ExistsInConfig` 挡了,所以一直没暴露;但天气合并进来之后,
一条记录里带着整份天气参数,**再来一条同行星记录就会把天气参数分叉成两份**
(改一条、读另一条 → 症状是"我的天气设置时不时自己变回去")。

→ 改成:已有记录就**只更新层名**(`SetConfigName`),不再 `Add`。
这条改动**只影响"已存在的行星再 AddConfig"这一种情形**,而那种情形在改前是 bug,
所以没有兼容负担。

**㊾ 旧记录兼容:`<Weather>` 节点不存在时会怎样?**

**先把真实文件形态记下来**(改前实测,这台机器的
`UserData/VolkenConfig/PlanetConfigList.xml`,16 条记录):

```xml
<PlanetConfigList xmlns:xsd="…" xmlns:xsi="…">
  <Configs>
    <PlanetConfig PlanetName="Droo" CloudConfigName="2Dtest" ExtraCloudConfigName="Atmospheric" />
    …
  </Configs>
</PlanetConfigList>
```

注意:**没有 `<Weather>` 的记录是自闭合标签**(`… />`),不是带子节点的元素 ——
`XmlSerializer` 对"属性-only 的对象"就是这么写的。

`PlanetConfig` 的字段初始化器
`public VolkenWeatherConfig Weather = VolkenWeatherConfig.CreateDefault();` 在
`new PlanetConfigList()` 里先跑,`XmlSerializer` 对**缺失的元素是"不碰"而不是"置 null"** ——
所以理论上不缺字段。但这条依赖"字段初始化器 + 反序列化器不重置"这个行为细节,
**不去赌**:`PlanetConfigList.LoadFromFile` 里加了显式兜底(缺失就补一份默认 + `EnsureSections` + `ClampAll`)。
老玩家的 `PlanetConfigList.xml` 因此不会读坏,只是天气部分是默认关闭。

**而且实际会自愈**:`Volken.OnSceneLoaded` 对"清单里已有该行星"这条路径会调
`planetConfigList.AddConfig(planet, name)`(用于纠正预设名),而 `AddConfig` 结尾就是
`SaveToFile` —— 所以**进一次飞行场景,这份文件就会被重写成"带 `<Weather>` 子树"的形态**。
换句话说:旧格式不会长期存在,但**第一次进场景的那一瞬间**必须靠上面的兜底才不出 NRE。

**编译验证**:`dotnet build Volken.csproj -t:Rebuild` → **0 错误**(5 个警告,少掉的那个是删掉的死代码带的);
三语言文件用 `XmlReader` 严格校验**全部 well-formed**;
面板/清单/状态机引用的 **176 个本地化 key 在三份文件里全部存在**。

### 10.2h 【设计取向修正】删除 `WeatherTypes` —— Volken 不走 SP2 的"全局天气预设"路线

> 【用户要求】"我对 `WeatherTypes` 的做法不是很认可,这是个 SP2 用的全局设置,统一设置了云层、雨、雾
> 等等的预设,而 Volken 出于我比较懒和给用户更多自由,决定让玩家自己设置云层、雨、雾、雷的
> 各个参数并序列化,所以我觉得可以删了"。

**㊿ 这条把 §2/§5 里"移植 SP2 天气标度"这条路线正式作废。**

SP2 的做法是一套**全局预设系统**:`WeatherTypes`(Clear / Few / Broken / Overcast / Rainy / Stormy /
Heavy / Foggy)是一个**中间层** —— 天气档位先被统一决定,再由它去驱动云层、雨、雾各自的预设与阈值。
玩家调的是"今天什么天气",不是"雨多密"。

**Volken 的做法(用户决定)**:去掉这个中间层 ——
**每个子系统(云层 / 雨 / 雾 / 雷)的参数全部是玩家直接设置并序列化的数值**,面板上就是一堆直接的滑块。
"天气值"这个连续量仍然存在(它驱动状态机的随机与淡变,以及各子系统自己的触发阈值),
但它**不再映射到任何档位、也不驱动任何"预设"**。

| | SP2 | Volken(现在) |
|---|---|---|
| 玩家调什么 | 天气档位(preset) | 云/雨/雾/雷的**逐项参数** |
| 有没有中间层 | 有(`WeatherTypes` + Enviro 预设混合) | **没有** |
| 阈值来源 | 全局常量 | **各子系统自己的配置字段** |
| 云层怎么定 | 由天气档位驱动 | **完全由 `CloudConfig` 决定,天气不碰**(§10.2b ㉔) |

**删掉的东西**

| 删除项 | 原用途 | 去向 |
|---|---|---|
| `Weather/WeatherTypes.cs`(**整个文件**) | SP2 标度常量 + `Classify` | 已删,连带 `.meta` 与 `.csproj` 条目 |
| `WeatherTypes.Foggy / Clear / Few / Broken / Overcast / Rainy / Stormy / Heavy` | 档位常量 | 不再需要(默认值改字面量) |
| `WeatherTypes.RainTrigger` / `HeavyRainThreshold` / `LightRainCeiling` / `LightningTrigger` / `FogCeiling` | 派生阈值 | → 变成配置字段(见下) |
| `WeatherTypes.Classify` | 连续值 → 档名 | → `VolkenWeather.DescribeWeatherValue(float)`(纯显示,无逻辑) |
| `VolkenWeather.IsRaining` 里的 `RainTrigger` 常量 | 雨天判定 | → 读 `Config.rain.triggerValue` |

**新增的配置字段(把写死的常量变成玩家可调的项)**

| 字段 | 默认 | 说明 |
|---|---|---|
| `VolkenWeatherConfig.DefaultWeatherValue`(**常量**) | `0.25f` | 天气值标度的默认起点(原 `WeatherTypes.Few`)。**没有档位含义** |
| `OverallSection.maxWeatherValue` | `3f` | 随机天气目标的**取值上限**(下限恒 0;原为 SP2 硬编码 `[0,3]`) |
| `RainSection.triggerValue` | `2.25f` | 雨触发阈值(原 `WeatherTypes.RainTrigger`) |
| `LightningSection.stormValue` | `2.5f` | 雷击触发阈值(原 `WeatherTypes.Stormy` / `LightningTrigger`) |

**保留的一个东西(以及为什么)**:`VolkenWeather.DescribeWeatherValue` ——
它把连续值切成 `Clear/Few/Broken/Overcast/Rainy/Stormy/Heavy` 几个**标签**,只用于日志与 UI 显示。
判断依据:**它不驱动任何逻辑**(没有任何 `if (name == "Rainy")`),
所以它不构成"预设系统",只是个 `float → string` 的格式化函数。
⚠️ **不要在它上面加逻辑** —— 那一步就等于把 SP2 的中间层又建回来了。

**随之调整的细节**

- `PickRandomWeather` 的"晴后必转云"偏置从硬编码 `0.4f` 改成**按上限的比例**(`ceiling * 0.133f` ≈ 原 0.4/3),
  这样玩家把上限从 3 改成 6 时偏置不会失真;"雨后压到 1.5"仍然硬编码但 `min(1.5, ceiling)` 兜底;
- 面板的「天气值(强制)」滑块范围从写死的 `[Foggy, Heavy] = [-1, 2.75]` 改成 `[-1, ceiling]`;
- 面板「⑤ 雷」新增 **触发天气值** 滑块;「③ 雨(占位)」新增 **触发天气值** 滑块(与雷对称);
- 三语言各新增 `Volken.UI.LightningStormValue` 与 `Volken.UI.RainTriggerValue`。

**编译验证**:`dotnet build -t:Rebuild` → **0 错误**(5 个警告);
全工程 `WeatherTypes` **只剩文档注释里的历史说明**,零代码引用;
178 个本地化 key 三语言齐全;三份语言文件 `XmlReader` 严格校验通过。

### 10.3 需要在真机上确认的开放问题

> ⚠️ 本节原有 9 条,其中与雨/雾直接相关的 6 条(`打包`/`雨是否画出`/`雾合成顺序`/`雾首帧`/
> `风速量级`/`几何遮挡骨架`/`ParticleConstantNormal_Soft 观感`)已随 §10.2e 的移除**作废**,
> 记录保留在 §10.2e 的经验教训里。以下为**移除后仍然有效**的条目。

1. **打包(所有天气功能的前置条件)**:清单有两层,都要对。
   - **权威来源 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表)** —— 新增 shader/音频要加进去,
     **删除资产要手工移除对应 GUID**(删文件不会自动清除,见 §10.2e)。现已复核:20 项,GUID 全部可解析、无悬空;
   - 生成物 `Temp\ModManifest.xml` 与 `ModAssetBundles\StandaloneWindows64\volken.manifest` ——
     构建后请核对里面出现了 `LightningBolt.shader` 与 5 条 `enviro_thunder_*.ogg`,
     且**不再出现** `RainParticles.shader` / `RainParticles.compute` / `HeightFog.shader` / `enviro_rain_*.ogg`。
   缺 shader 的症状:闪电静默失效并在日志里打 `shader NOT FOUND` /
   `LoadVolkenAsset: '...' not found`;缺音频的症状是 `loaded 0/5 thunder clips`。
2. **局部太阳时的日/夜半边**:SR2 侧拿不到行星自转轴的可靠朝向(本体轴无公开字段,用本地"北"推断会随飞行翻转)。因此 `ComputeLocalSolarHour` **无法区分日出侧的 6 点与日落侧的 18 点**,实现上把 0~12 一律当"上行"。
   *移除前*这一点的后果是"黎明雾在黄昏也对等触发";**雾删除后**它只影响天气标度的分档(`foggyDawn` 配置的黎明档),无渲染副作用,优先级降为最低。见 `VolkenWeather.ComputeLocalSolarHour` 注释。
2. **天气配置的 XML 往返(合并后新引入的唯一不确定项)** —— 把天气配置**内联**进
   `PlanetConfig` 是**第一次**在本工程用嵌套 `[Serializable]` 子对象(`CloudConfig` 是扁平结构 +
   一个 `Vector4` 字段),`XmlSerializer` 对 `[XmlElement("Weather")] public VolkenWeatherConfig Weather`
   这种写法应当正常,但**没有离线验证手段**(见 §10.4:本工程无法离线跑 XmlSerializer)。进游戏后按序确认:
   - `volkenWeather` 的 `WeatherStatus.config:` 行给出路径,打开 `PlanetConfigList.xml`;
   - 该行星那条 `<PlanetConfig>` 里**出现了 `<Weather>` 子节点**,里面是
     `<Overall>` / `<CloudLinkage>` / `<Rain>` / `<Fog>` / `<Lightning>`;
   - 手改其中一个值(例如 `<Lightning><enabled>true</enabled>`)→ 重载飞行场景 → 面板上该值跟着变
     (**证明读回没问题**);改面板 → 点"保存当前配置" → 文件里的值跟着变(**证明写出没问题**);
   - 进另一颗行星 → 清单里多一条记录,各自带自己的 `<Weather>`;
   - **云的部分没被影响**:`CloudConfigName` 属性、`VolkenConfig/{行星}/{预设名}.xml` 都照旧能用,
     云面板的"保存/另存为/加载"行为不变。
   ⚠️ 若读回失败,症状是**静默回落到默认(全部关闭)**而不是报错 —— 所以"面板值没变"
   本身就可能是失败信号,别只靠"没报错"判断。
3. **落雷的地形贴合**:落点按"行星半径的正球面"近似(忽略局部地形起伏),山峰上的落雷可能插进山体。方案 A 的可接受损失;二期可换 `Physics.Raycast`。
4. **雷暴循环的时间基准**用 `Time.deltaTime`(受时间加速影响)而非 `unscaledDeltaTime`:与 SP2 的"游戏时间驱动"一致,但极高时间加速下雷击会密集。待实测确认是否需要加钳制。
5. **`CloudRenderer.LinearSceneDepth` 现在没有消费者**:它是为雾加的公开属性,雾删除后**保留**(一行只读属性,零成本)。若长期不用可在重做雾时复用,或直接删掉。

---

### 10.4 验证手段(本轮新建立的两条,可复用)

**① `dotnet build Volken.csproj`** —— 直接编译整个 mod 程序集,**约 2~25 秒**,报错带行号。
比等 Unity 刷新快得多。注意两点:
- Unity 未刷新时 `Volken.csproj` 不含新文件,需手工补 `<Compile Include="…" />` 再编
  (仅用于验证;Unity 下次刷新会重新生成,手工改动会丢);
- **它只编 C#,完全看不到 shader/compute 的错误**。

**② Unity 的 shader 编译器日志** —— `Editor.log` 里会有
`Shader error in '<Shader 名>': … at <文件>(<行>) (on d3d11)`。
这是**唯一能离线抓到 shader 语法/内置函数错误**的途径(本轮靠它抓到 `expm1`)。
限制(实测确认):
- Unity 只编译**被素材引用**的 shader。`RainParticles.shader` 没有被任何场景材质引用,
  所以反编译日志里**只有 import 记录、没有 compile 记录** —— 它是否真的能过,要等游戏里加载才见分晓;
- `Camera` 相关的 shader(`LightningBolt`)会因为运行时 `Shader.Find` 而被编译,所以有记录;
- `UnityShaderCompiler.exe` 是 **端口式 worker 进程**
  (`Usage: UnityShaderCompiler <base folder> <log path> <port number> ...`),
  不能直接命令行调用去单独校验一个 shader 文件(实测确认,此路不通)。

**③ 资源台账(本轮新增)** —— 因为打包清单是显式的,最容易出的问题是"资源没打进 bundle"。
`Mod.LoadVolkenAsset<T>` 现在把每次加载结果记进一张表,dev 命令 **`volkenAssets`**(或
`volkenWeather`,它会顺带打印)输出形如:

```
AssetLedger:
  ok  Assets/Scripts/Volken/Weather/LightningBolt.shader
  MISSING(required)  Assets/Scripts/Volken/Weather/RainParticles.shader
  missing(optional)  Assets/Scripts/Volken/Weather/RainStreak.png
```

一条命令就能区分"代码 bug"和"打包漏了资源"。

**④ GPU 回读自检(本轮新增,专治"雨到底画出来没有")** —— 雨的整条链路
(剔除 → `FillArgs` 写参数 → `RenderMeshIndirect` 取参数 → shader 里按 `SV_InstanceID`
索引 `_Positions`)全在 GPU 上,CPU 侧只看得到"我下发了绘制命令",**无法区分**
"画了但没显示"和"根本没准备画"。
`Rain.TryGpuProbe` 每 10 秒回读一次间接绘制参数 buffer(`_ArgsBuffer[1]` = instanceCount),
日志前缀 **`VolkenRain:GPU`**,输出形如:

```
VolkenRain:GPU probe active=50000 capacity=100000 indexCount/invocation=36
   instanceCount=50000 occlusion=A fade=1.000 → OK: GPU 已准备绘制,若屏幕上仍无雨 → 问题在绘制/着色环节
```

判据表:

| instanceCount | 结论 |
|---|---|
| `indexCount == 0` | mesh / index buffer 异常 |
| `instanceCount == 0` | **剔除或参数环节**有问题(视锥判定 / 原子计数 / buffer 绑定) —— 不是绘制环节 |
| `> capacity` | 计数器或 buffer 绑定异常 |
| `≈ active` | GPU 侧正常;若屏幕无雨 → 问题在**绘制/着色**(参数 buffer 没被 shader 用上 / 实例索引取错) |

实现细节:`GetData` 是**同步**回读(会强制 CPU 等 GPU),所以只在"正在下雨"时做、
间隔 10 秒、且**不在下发绘制的同一帧读**(下一帧读,此时 GPU 已完成那一帧)。

**③ 天气配置的 XML 往返怎么验**(2026-09-27 新增;§10.2g 合并后按新路径)

**(a) 看文件在不在** —— 进一次有大气行星的飞行场景,然后看:
`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\UserData\VolkenConfig\PlanetConfigList.xml`
应当**已经存在**(它同时是云层的清单,所以本就有),关键变化是每条第记录里**多了 `<Weather>` 子节点**。
`volkenWeather` 命令的 `WeatherStatus.config:` 行会直接打出这份文件的完整路径。

**(b) 看内容是不是内联结构** —— `<PlanetConfig>` 里应当是**属性 + 一个 `<Weather>` 子树**:

```xml
<PlanetConfigList>
  <Configs>
    <PlanetConfig PlanetName="Droo" CloudConfigName="Default" ExtraCloudConfigName="Atmospheric">
      <Weather>
        <Overall>
          <enabled>false</enabled>
          <dynamicWeather>true</dynamicWeather>
          …
        </Overall>
        <CloudLinkage>
          <enabled>false</enabled>
          <coverageGain>0</coverageGain>
          …
        </CloudLinkage>
        <Rain>…</Rain>
        <Fog>…</Fog>
        <Lightning>
          <enabled>false</enabled>
          <minDelay>6</minDelay>
          …
        </Lightning>
      </Weather>
    </PlanetConfig>
    …
  </Configs>
</PlanetConfigList>
```

**(c) 读写两个方向都要试**(只试一个方向不够 —— 序列化与反序列化是两段独立代码):

| 方向 | 操作 | 预期 |
|---|---|---|
| **读** | 手改 XML(如 `<Lightning><enabled>true</enabled>` 与 `<maxDelay>8</maxDelay>`)→ 重载飞行场景 | 面板「⑤ 雷」的开关与间隔跟着变 |
| **写** | 面板上调一个值 → 点「保存当前配置」 | XML 里该字段跟着变,**且云层的 `CloudConfigName` 等属性没被改乱** |
| **换行星** | 进另一颗有大气行星 | 清单里**多一条**记录(或那条记录补上 `<Weather>`),各自带自己的天气参数 |
| **重置** | 点「重置为默认」 | 内存配置回全关(需再点"保存"才落盘) |
| **云回归** | 用云面板的"另存为/加载配置" | 云的行为**与合并前一致**(天气合并不该动到云) |

⚠️ **失败是静默的**:`XmlSerializer` 读到无法解析的节点会**当未知元素忽略**,
字段回落默认值(全关),**不报错**。所以"面板上的值没跟着 XML 变"本身就是失败信号 ——
别只凭"日志没有红字"判断通过。若真失败,先查 `Mod.Log` 里的
`Failed to load planet config '…'`(这条只在**抛异常**时才打)。
