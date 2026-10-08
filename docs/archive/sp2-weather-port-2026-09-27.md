# Volken —— SP2 天气系统移植计划(原生 BIRP)

> 状态:✅ 已归档(被后续方案取代的母计划与反编译 / 素材底账)
> 日期:2026-09-27;归档核对:2026-10-08
> 当前工作:[雨计划](sp2-rain-particledomain-port-2026-09-28.md) §10、[雷声](thunder-realism-2026-09-28.md)、[解耦](weather-cloud-decoupling-2026-10-01.md) §8。
> 适用范围:正文记录当时的调查和决策,保留 §10 的证据及编号供引用。“只剩雷电”、旧天气状态机、旧配置路径和 dev 命令均不代表当前项目。
> 当前代码已有雨视觉 / 雨声 / 闪电 / 雷声,雾仍占位;云 / 天气预设独立,无全局天气值。现行约束与调试入口见 [会话上下文](../AGENT_CONTEXT.md)。

## 1. 背景与目标

- Volken 当前 = SR2/Juno 的 **raymarch 体积云 mod**(自研 `CloudRenderer` + `Clouds.shader`,BIRP `[ImageEffectOpaque]` 合成链,Unity 2022.3.62f3)。
- 需求:在有大气的行星上增加**雨、雾、雷电**,由逐行星天气配置驱动,与云层/大气联动。
- 移植来源:SP2 反编译工程 `<SP2_CODE>` + 游戏安装 DLL 反编译产物 `analysis\dll\` + 素材提取工程 `<SP2_D4>`。
- 目标工程:本仓库 Volken2(Unity 2022.3.62f3, BIRP, SR2 mod)。

## 2. SP2 天气系统本质(反编译实证)

> 全部结论来自对游戏安装目录 DLL 的反编译:`SimplePlanes 2_Data\Managed\Enviro3.Runtime.dll`、`Jundroo.Common.dll`。反编译源码见 `<SP2_CODE>\analysis\dll\`。

### 2.1 雨 = Jundroo ParticleDomain(GPU 计算粒子域)+ Enviro 雨粒子(次要)

SP2 主雨是 `Jundroo.Common.ParticleDomain`(游戏侧子类 `ParticleHandler`):

- **渲染**:单一 ComputeShader(`BillboardParticles`,6 内核 `Positioning / Randomize / TranslateFixed / CullAndOccludePoints / CullPoints / FillArgs`)+ `Graphics.RenderMeshIndirect` 间接实例化;粒子 = 程序生成十字交叉雨丝网格(8 顶点/12 三角),billboard 纹理,沿「风 + 重力 15m/s + 玩家速度 − 相机速度」拉伸对齐;
- **域**:以相机为 `Target` 的 50m 半径球域,默认 10 万粒子,随画质档缩放(0.25/0.5/1.0);
- **遮挡**:`OcclusionDepthCam` 正交深度相机(256×256 Depth RT,从相机上方沿雨下落方向俯拍)+ **URP `RTCameraRendererFeature`**(`RTCameraRenderPass`,RenderGraph,`DepthOnly` pass,`AfterRenderingOpaques`)渲染深度,`CullAndOccludePoints` 用上一帧 VP 矩阵(`_previousOrthoVP`)采样 `_OcclusionDepth` 剔除被遮挡雨滴;
  -  **RTCamera 部分是 URP 专属,是移植到 BIRP 唯一必须重写的组件**;
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

 注意:sp2d4 是 SP2 的资产提取工程,其 Unity 版本也是 2022.3,compute shader 资产可直接拷入本工程重新编译;但**雨滴 shader 源码缺失**,需按 §5.2 重写。

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

- `<SP2_CODE>\analysis\dll\Jundroo.Common.ParticleDomain.decompiled.cs`(698 行,雨域全源码)
- `<SP2_CODE>\analysis\dll\Jundroo.Common.RTCameraRendererFeature.decompiled.cs` + `RTCameraRenderPass.decompiled.cs`(遮挡 URP 实现,供 BIRP 改写参照)
- `<SP2_CODE>\analysis\dll\Enviro.EnviroLightningModule.decompiled.cs` + `Enviro.Lightning.decompiled.cs`(闪电全源码)
- `<SP2_CODE>\analysis\dll\Enviro.EnviroFogModule.decompiled.cs`(728 行,雾全源码)
- `<SP2_CODE>\analysis\dll\Enviro.EnviroAudioModule.decompiled.cs`(雷声播放)
- 游戏侧:`<SP2_CODE>\Game\Game\Assets\Scripts\Flight\ParticleHandler.cs`、`Assets\Scripts\Environment\VolumetricEnvironment.cs`、`Assets\Scripts\Environment\WeatherTypes.cs`

---

## 10. 实施进展与计划更正(2026-09-27 实测)

> **本节 2026-10-02 做过一次编号合并**(原 §10.2b·§10.2c0·§10.2c2·§10.2d·§10.2e~§10.2h 合并为现 §10.2~§10.6)。对照:**原 §10.2b → §10.2** · **原 §10.2c0 / §10.2c2 → §10.3** · **原 §10.2d → §10.4** · **原 §10.2e → §10.5** · **原 §10.2f / §10.2g / §10.2h → §10.6** · 原 §10.3 → §10.7 · 原 §10.4 → §10.8。
> **条目标记(①~㊿、A~J)未变**,全文引用已按上表更新;其中 **㉔ / ㉝ / ㉞** 三个标记位于 §10.4(原 §10.2d 段内)。
> 本节是**雨/雾实施期的实测记录**,不是当前代码说明;已删除的 API 名(`Rain.*`、`FogRenderer.*`、`RainParticles.*` 等)在工程里**已不存在**。

### 10.1 已完成 / 当前状态(2026-09-27 末)

| 阶段 | 状态 | 落地文件 |
|---|---|---|
| 0 骨架 | ✅ 编译通过 | `VolkenWeather.cs`;配置类并入 `Core/PlanetConfigList.cs`(`WeatherTypes.cs` 后删,见 §10.6) |
| 1 闪电 | ✅ 编译通过(真机验收见 §10.7) | `LightningBolt.cs`、`LightningModule.cs`、`LightningBolt.shader`、`Weather/Audio/enviro_thunder_1~5.ogg` |
| 2 雨 | ❌ **已整体移除**(配置字段保留占位)—— 见 §10.5 | ~~`RainParticles.compute`、`RainParticles.shader`、`Rain.cs`~~ |
| 3 雾 | ❌ **已整体移除**(配置字段保留占位)—— 见 §10.5 | ~~`HeightFog.shader`、`FogRenderer.cs`~~;`CloudRenderer.LinearSceneDepth` 保留 |
| 4 打磨 | 🟡 部分完成 | 天气配置(见 §10.6)、`WeatherPanel.cs` 分组、三语言文案;**未做**:雨/雾重做、云层联动、水花、联机核对 |

改动过的既有文件:`Mod.cs`(初始化 + dev 命令 + `LoadVolkenAsset<T>` + `LogThrottled`)、`ModSettings.cs`(2 个天气设置)、`VolkenMod.cs`、`VolkenUserInterface.cs`、`CloudRenderer.cs`、`Assets/Content/Languages/{EN-US,ZH-CN,RU-RU}.xml`、`Assets/ModData.asset`(资产清单)。

**落盘位置**:见文首「天气配置形态」与 §10.6。

**dev 命令**:`volkenWeather`(状态:行星 / 天气值 / 雷电 / 配置文件路径)、`volkenSetWeather <float>`(强制天气值)、`volkenBolt`(手动劈雷)、`volkenAssets`(资源台账)。~~`volkenRainProbe` / `volkenRainAxis` / `volkenRainLean` / `volkenRainAuto`~~ 已随雨删除。

**编译验证**:`dotnet build Volken.csproj`(见 §10.8)。

### 10.2 与原计划 · 与 SP2 的实测差异(逐条更正)

>  **整体性说明**:⑨~㉛、㉟~㊳ 与 §10.7 / §10.8 全部是"雨/雾实施期"的实测记录;雨与雾已于 §10.5 整体移除,这些记录是"重做时的参考资料",不是当前代码的说明。其中被删除的 API 名(`Rain.*`、`FogRenderer.*`、`RainParticles.*` 等)在工程里**已不存在**。
>
> 📌 另 §2/§5 的"移植 SP2 天气标度(`WeatherTypes`)"路线**已作废**(该文件已删除),改为"各子系统参数由玩家逐项设置",见 §10.6。

**① 命名空间不能叫 `Volken.Weather` —— 编译期硬错误 CS0101。** 工程里已有全局命名空间下的类 `Volken`(`Clouds/CloudRenderer.cs` 等以裸 `Volken.` 引用),再声明 `namespace Volken` → `CS0101: already contains a definition for 'Volken'`。
→ 天气系统统一用 **`VolkenMod.Weather`**;后续新增天气文件必须沿用此命名空间。

**② `DepthCapture` 是死代码,不能"复用"。** 全工程 `grep DepthCapture` **零引用**;依赖 `Shader.Find("Hidden/DepthLinear")`,而工程里没有这个 shader。真正可用的深度是 **`CloudRenderer.combinedDepthTex`**(实例私有 RFloat,远+近深度合并后的线性深度)。
→ 阶段 3 的雾必须取 `CloudRenderer.combinedDepthTex`,或直接写进 `CloudRenderer.OnRenderImage` 内部。两个独立 `OnRenderImage` 组件之间的执行顺序由挂载顺序决定、不可靠;若走独立 `FogRenderer`,必须用 `[DefaultExecutionOrder]` 显式钉死顺序。

**③ `BillboardParticles.asset` 是编译产物,读不出 HLSL 源码。** 该文件是 YAML `!u!72 ComputeShader`,`code:` 字段是 hex 编码的 DXBC 字节码;反射信息可读全,已确认 6 个内核:

| 内核 | 输入/输出绑定 | threadGroupSize |
|---|---|---|
| `Positioning` | out `_Positions`;uniform `_domainRadius/_domainPos/_frameVel/_particleVel/_InstanceCount` | 64,1,1 |
| `Randomize` | out `_Positions`;uniform `_domainRadius/_domainPos/_InstanceCount` | 64,1,1 |
| `TranslateFixed` | out `_Positions`;uniform `_translation/_InstanceCount` | 64,1,1 |
| `CullAndOccludePoints` | in `_Positions`;out `_CulledPositions/_CulledCount`;tex `_OcclusionDepth`;uniform `_FrustumPlanes/_OcclusionNear/_OcclusionFar/_InstanceCount/_OcclusionVP/_OcclusionView` | 64,1,1 |
| `CullPoints` | in `_Positions`;out `_CulledPositions/_CulledCount`;uniform `_FrustumPlanes/_InstanceCount` | 64,1,1 |
| `FillArgs` | in `_CulledCount`;out `_ArgsBuffer` | 1,1,1 |

→ **决策(已确认)**:阶段 2 按上表把 6 个内核重写为 `.compute` 源码,不搬运编译字节码;绑定与线程组尺寸照抄上表即可与 `ParticleDomain` 的 C# 侧 `SetBuffer`/`SetTexture`/`Dispatch` 对齐。Buffer 元素布局(来自 `ParticleDomain.SetupBuffers`,须一致):`_Positions` stride 12(`float3`)、`_CulledPositions` stride 16(`float4`:xyz = 位置、w = 随机种子/尺寸)、`_CulledCount` stride 4(`uint`)、`_ArgsBuffer` 20 字节(`GraphicsBuffer.IndirectDrawIndexedArgs`)。

**④ SR2 没有 `WindManager`,雨的风向来源要换。** SP2 的 `ParticleHandler.GetWindVelocity()` 取 `_windManager.WindVelocity`;SR2/JNO ModApi 无此入口。
→ 复用 **`CloudConfig.windSpeed / windDirection`**,经 `CloudRenderer.AdvanceGlobalCloudState` 的"行星北/东向量 → 世界向量"换算取得(保证云雨同向)。

**⑤ SP2 的浮动原点事件名在 SR2 不存在。** SP2 用 `GameWorld.Instance.FloatingOriginChanged`;SR2/JNO 没有。
→ 雨粒子域平移改用 **`IGameView.ReferenceFrameRecentered`**(委托 `(IReferenceFrame, Vector3d, Vector3d)`),即 `CloudRenderer.OnReferenceFrameRecentered` 的既有写法。

**⑥ `ParticleHandler` 的抽象成员在 SR2 侧的对应实现。**

| SP2 抽象 | SP2 实现 | SR2 对应 |
|---|---|---|
| `GetAltitudeASL()` | `GameWorld.FloatingOriginOffset.y + target.y` | 相机位置到行星中心距离 − `PlanetData.Radius`(`VolkenWeather.CameraAltitudeAsl`) |
| `GetPlayerVelocity()` | 联机 `FlightScenePlayer.Velocity` | `ICraftNode.SurfaceVelocity` / `ICraftFlightData.SurfaceVelocity`,或 `VolkenWeather.CameraVelocity` |
| `GetWindVelocity()` | `WindManager.WindVelocity` | 见 ④ |
| `IsCameraSubmerged()` | `WaterRenderer.ViewerHeightAboveWater < 0` | `CameraAltitudeAsl < 0 && PlanetData.HasWater`(`VolkenWeather.CameraSubmerged`) |
| 云内淡化 `CameraCloudFadeVal` | Enviro 的 `bottom/topCloudsHeight` | Volken 主层 `layerHeights/layerSpreads/layerStrengths` 推出的 [云底, 云顶−400](../proposals/`VolkenWeather.CameraCloudFade`) |
| `TimeOfDay`(黎明起雾) | Enviro 时间模块 | 由太阳方向与地表法线夹角现算局部太阳时(`VolkenWeather.ComputeLocalSolarHour`),见 §10.7 |

**⑦ mod 自己的资源加载器没有 `LoadAudio`,也没有 `Load<T>`。** `Mod.Instance.ResourceLoader` 是 `IModResourceLoader`,只暴露 **`LoadAsset<T>(path)`**;`Load<T>(path, logErrors)` 与 `LoadAudio(path, logErrors)` 在游戏的 `IResourceLoader` 上,而 `LoadAudio` 内部是 `Resources.Load`(读不到 mod bundle 素材)。
→ 统一用 `Mod.LoadVolkenAsset<T>(path, required)`;雷声自建 `AudioSource` 播放(`PlayScheduled` 定时),音量由 `WeatherConfig.thunderVolume` 单独给。

**⑧ `Shader` 上没有 `FindPass`**(`FindPass` 是 `Material` 的方法)。`LineRenderer`/`MeshRenderer` 会自动选 pass,本 shader 两个 pass 分属不同材质、各只有一个可用 pass,不需手动指定索引。

**⑨ 雨必须用"相机相对"坐标系,且视锥平面必须手工构造。**(此条前后改过结论,最终版如下。)参考系空间下 `GeometryUtility` 给的平面与粒子存的"域局部偏移"只在相机位于参考系原点附近时一致,相机飞远后剔除判据整体错位。
→ 最终方案:粒子位置 = 相对相机的偏移;`_domainPos` = 相机世界位置(世界 = `_domainPos` + 偏移);视锥平面由 `Rain.BuildCameraPlanes` 解析构造(近/远平面法线 = ±forward、距离 = ∓near/±far;四个侧面过相机原点故 d = 0,法线由半 FOV 与宽高比推出),约定 `dot(n,p) + d >= 0` 为内侧;**不要**再用 `GeometryUtility.CalculateFrustumPlanes`。副产品:域中心 == 相机,浮动原点重置天然不需补偿,`_frameVel` 恒 0 是刻意的。

**⑩ compute shader 没有 `_Time` 内置量**(编译报未定义)。回绕重生需掺入时间,故新增 `float _time` uniform 由 C# 每帧传 `Time.time`。

**⑪ 雨的绘制必须放在渲染回调里,且回调类型是 `Camera.CameraCallback`。** 在 `Update` 里调 `Graphics.RenderMeshIndirect` 会得到"帧开始前的游离渲染命令";雨是独立网格,需排进该相机的透明队列。另 .NET 4.x 下 `Action<Camera>` **不能**隐式转成 `Camera.CameraCallback`(CS0029)。
 **真机更正(2026-09-27):`Camera.onPreCull` 这个静态委托在本工程里从未被调用**(证据:前置条件全满足 `populated=True active=50000 pendingDraw=True`、fade 爬到 0.765,但 `draws/s` 恒 0、`everDrew=False`,且 `RenderForCamera` 里三条 `LogThrottled` 一条没打)。→ 主路径改为 **`RainCameraRenderer : MonoBehaviour` 的实例 `OnPreCull`**(挂游戏相机上),静态订阅保留作双保险;心跳新增 `renderer: host= preCullCalls=`。

**⑫ 实例索引用 `SV_InstanceID`,不要用 UNITY_VERTEX_INPUT_INSTANCE_ID 那一套。** `Graphics.RenderMeshIndirect` + 结构化 buffer 与"带实例属性的传统 Instancing"不是同一条路径,`unity_InstanceID` / `UNITY_SETUP_INSTANCE_ID` 混用易拿到恒 0 的实例号。`SV_InstanceID` 与 `_CulledPositions` 的原子累加写入顺序一一对应。
 **这一条是推断 + 设计选择,尚未在游戏里跑过**(见 §10.7)。

**⑬ 雨丝 UV 不做平铺。** 长度已由 `AlignStreaks` 的基向量列 1(`dir × stretch × streakLength`)表达,贴图纵向本身是"头亮尾淡"渐变;再按拉伸量平铺会在 `wrapMode = Clamp` 下夹到边缘形成糊块。故 UV 直接用网格 UV。

**⑭ 高度雾用解析积分,不能用 `1 − exp(−density × distance)`;Unity 的 HLSL 没有 `expm1`。** 照 `EnviroFogModule` 的指数高度雾解析积分:`∫₀ᴰ ρ dt = density · ρ(y₀) · H · (1 − exp(−f)) / f`,`f = D·dy/H`。初版用 `-expm1(-f)/f` 触发 `Shader error ... undeclared identifier 'expm1' ... (on d3d11)`(见 §10.8),**Unity 的 HLSL 编译目标没有 `expm1`**。现改为:对 `|f| < 1e-3` 用泰勒展开 `1 − f/2 + f²/6`,其余用 `(1 − exp(−f))/f`(后者在 f 很小时有灾难性抵消,泰勒分支必须保留)。

**⑮ `InspectorModel` 不是 `GroupModel`。** 两者是各自独立的顶层模型,`InspectorModel` 只有 `AddGroup(GroupModel)` / `Add<T>(T)`,**没有** `Add(ItemModel)`。→ 阶段 4 天气面板必须自建顶层 `GroupModel` 再 `AddGroup`;`GroupModel.Add(T)` 泛型不带约束,组套组可工作。

**⑯ 云层联动必须从基线重算,不能叠加。** `ApplyCloudCoupling`:进行星时记下 `coverage/windSpeed/cloudColor` 基线,之后每帧 `值 = 基线 + 天气值 × 增益` 纯函数重算;三个增益默认**全 0** = 一个字段都不写 = 与未实现本特性逐字节一致。

**⑰ 雨的 buffer 容量与"生效粒子数"必须分离。** SP2 的 `ParticleDomain.ParticleAmount` setter 在数量变化时 `FreeBuffers() + SetupBuffers()` 重建全部 GPU buffer 并重新 Dispatch `Randomize`(其调用时机是低频天气事件,安全);本实现从每帧 `Tick` 算粒子数,重建会在上一帧绘制命令未执行完时释放其 `GraphicsBuffer`(未定义行为)。→ 拆为:**容量** `_capacity` 按画质档上界分配一次(High = 100000 / Medium = 50000 / Low = 25000),仅在"需要超过现有容量"时重建,且仅在**首次分配**时 `Dispatch(Randomize)`;**生效数量** `_activeCount` 每帧只改 `_InstanceCount` 与 draw 实例数,内核线程开头有 `if (i >= _InstanceCount) return;`。

**⑱ 非下雨区间的粒子数必须是 0。** SP2 的 `GetRainStrengthFactor` 在 `weatherValue <= 1.5` 时返回当前值,而 `ParticleHandler._currentRainStrengthFactor` 初始值就是 1.0,照搬会让晴天常驻 5 万粒子空转。→ `Rain.ResolveParticleAmount` 在 `!IsRaining` 时直接返回 0。

**⑲ 方案 B 的"半接通"状态必须有硬护栏。** 遮挡深度图为空时 `CullAndOccludePoints` 会把每个粒子判为被遮挡而剔除 → 雨完全消失且无报错。→ 走遮挡内核需 **`OcclusionEnabled`(配置开关)且 `OcclusionDepthRendered`** 同时成立;后者在阶段 4 接通渲染前恒为 false,故现在打开 `OcclusionEnabled` 只会退化为方案 A。

**⑳ SP2 雨滴 shader 的"源码"在提取工程里存在,但内容是占位模板。** `sp2d4/Assets/Shader/Jundroo_ParticleConstantNormal_Soft.shader` 头部标着 `//DummyShaderTextExporter`,`vert` 只是 `mul(unity_MatrixVP, mul(unity_ObjectToWorld, pos))`、`frag` 只是 `_MainTex.Sample(...)`,无任何实例化/朝向/软粒子逻辑,也没有 `#pragma target`,**不能用它反推 SP2 的真实实现**。唯一可靠收获是 Properties 块里的属性名(第一手信息):

| 属性 | 实际类型 | 用途 |
|---|---|---|
| `_MainTex` | `2D` | 雨滴贴图(**RGB 有效、A 是 alpha**,与计划里写的 `_Tex` 不同名) |
| `_Emission` | `Float` | 自发光强度 |
| `_MainColor` | `Vector` | 染色(**不叫 `_Color`**) |
| `_InvFade` | `Range(0.01, 3)` | **软粒子因子**(计划 §5.2 猜的 `_InvFade` 名字是对的) |

本实现当时的 `RainParticles.shader` 用自己的一套名字(`_Tex` / `_Color` / `_SoftParticleFade`);后来重做时已改为 SP2 原名(见雨计划)。

**㉑ 淡变重入守卫只看目标值 —— "雨完全看不见"的真正根因(真机抓到)。** `Rain.SetTargetFade` 原守卫为 `if (Mathf.Approximately(_targetFade, to) && Mathf.Approximately(_fadeMultiplier, to)) return;`:淡入中 `_fadeMultiplier` 永远不等于 to → 每次调用都把 `_fadeElapsed = 0` → 淡变卡死,`_fadeMultiplier` 恒 0。触发者是 `VolkenWeather.UpdateRain` **每帧**调 `Rain.OnWeatherValueChanged(WeatherValue)`。→ 正确语义:**只有目标变了才重新开始淡变**,`if (Mathf.Approximately(_targetFade, to)) return;`。

**㉒ 心跳日志必须放在守卫之后,否则 `pendingDraw` 会误导。** `_pendingDraw` 由 `RenderForCamera`(渲染阶段 onPreCull)消费置 false,心跳在 Tick 读它;若心跳排在守卫之前且本帧被提前 return,打出的就是上一帧残留值。→ 现在守卫分支里显式 `_pendingDraw = false` 再打心跳,正常分支打心跳后继续。

**㉓ 相机切换分支里不能清 `_FadeAmount`。** 原实现在 `cam != _cam` 时 `_material.SetFloat("_FadeAmount", 0f)`,相机解析每帧返回不同实例时雨会被永久清零。→ 淡入淡出统一由 `_fadeMultiplier` 一条路径管理,分支里只更新 `_cam`。

**㉕ `Shutdown` 必须幂等 —— 第二个 fade 重置源。** `VolkenWeather.UpdateRain()` 在闸门关闭的每一帧都调 `_rain.Shutdown()`,旧 `Shutdown` 每次都 `ResetFade(0f)` + `FreeBuffers()`。→ Shutdown 只在首次转入关闭时生效(内部 `_shutdownRequested` 标志),且**不**把 fade 硬置 0,而是 `SetTargetFade(0f, rainFadeOutDuration)` 自然淡出,buffer 留到淡出结束后由 `FinishShutdown()` 释放。

**㉖ 凡是"每帧都会调到的路径"里,禁止出现"重置动画进度"的副作用。**

| 位置 | 每帧触发的原因 | 副作用 | 修法 |
|---|---|---|---|
| `SetTargetFade` 守卫 | `UpdateRain` 每帧调 `OnWeatherValueChanged` | `_fadeElapsed = 0` | 守卫只看 `_targetFade` |
| `Shutdown` | `UpdateRain` 闸门分支每帧调 | `ResetFade(0f)` | `_shutdownRequested` 幂等 |

`ComputeOcclusionActive` 里的"穿出地面 1s 快速重入"(`ResetFade(0f)` + `SetTargetFade(1f,1f)`)只在 `aboveGround != _isAboveGround` 的**跳变沿**执行一次。自检标准:**"这段代码一帧里会被调用几次?重置进度能不能被调用多次?"**

**㉗ 淡变状态的写点必须只有 3 处,且都要留痕。** `Rain` 里能改 `_fadeMultiplier/_targetFade/_fadeFrom/_fadeElapsed` 的路径只有 `SetTargetFade` / `ResetFade` / `AdvanceFade`,三处都带 `[VolkenDiag]` 追踪:`SetTargetFade:` 每次目标变化打一行(带 from/cur);`ResetFade:` 把 fade 从 >0.001 压到 0 时打一行并附调用栈;`AdvanceFade:` 淡变中每 2 秒打一行进度(带 elapsed/duration/dt),写在 `_fadeMultiplier == _targetFade` 的提前 return 之后 —— "日志里完全没有 `AdvanceFade:`"即强信号:`_targetFade` 从没被设成 1。

**㉘ 心跳必须带淡变内部状态(`fadeState:`)。**

| 现象 | 成因 |
|---|---|
| `fade` 为 0 且 `fadeState.target=0` | 淡入从没被触发 → 查 `OnWeatherValueChanged` 的 `_isRaining` |
| `fade` 为 0 但 `fadeState.target=1` | 淡入触发但被反复重置/进度不涨 → 看 `Rain.ResetFade:` 调用栈、或 `dt` 是否为 0 |

→ `Rain:HB` 行尾追加 `fadeState: target= from= elapsed=/ dt= shutdown=`。

**㉙ 【架构缺陷】`UpdateRain()` 只在 `ApplyPlanet` 里调用,不在每帧的 `Tick` 里。** 即"天气值 → 雨"的同步只发生在行星加载/SOI 切换那一刻:若初始化时 `WeatherValue` 还没到 2.25(或配置未加载好),`OnWeatherValueChanged(2.75)` 这唯一一次调用会用旧值走 else 分支 → `_targetFade` 停在 0,之后天气怎么变雨都不会淡入;`Tick` 里也没有把新天气值推给 `Rain` 的机制。

**㉚ `Tick` 的步骤顺序:`改状态的逻辑`必须在`画不画的判定`之前。**

```csharp
ComputeOcclusionActive(...)   // ← 它会 ResetFade/SetTargetFade(穿出地面重入)
GetWindVelocity/AlignStreaks
AdvanceFade(dt)               // ← 无条件推进,不依赖自己的输出
// ...最后才:
if (_activeCount <= 0 || !_isPopulated || _fadeMultiplier <= 0.001f) return;  // 纯判定,不改状态
```

旧顺序把 `ComputeOcclusionActive` 放在守卫之后,fade 卡 0 时恢复逻辑永不执行,状态机被自己的守卫锁死。一般规则:**守卫只能读状态,不能挡在"改状态的逻辑"前面**。

**㉛ `_shutdownRequested` 必须在闸门重开时复位(否则雨永久失效)。** `Shutdown()` 置 `_shutdownRequested=true`,只由 `FinishShutdown()`(淡出结束)清除;若闸门在淡出完成前又打开,标志会一直留着,下一次 `Tick` 的 `FinishShutdown` 释放 buffer 而雨对象不会被重建 → 雨永久失效。→ `UpdateRain()` 在闸门打开分支里调 `Rain.ClearShutdownRequest()`。

**㉟ 【真机踩坑】`RWStructuredBuffer<uint>` 既没有 `InterlockedAdd` 也没有 `Load4/Store4` —— 计数器与间接参数 buffer 都必须是 `RWByteAddressBuffer`。** compute 编译直接失败,是两个独立的错:`RWStructuredBuffer<uint> object does not have method 'InterlockedAdd' at RainParticles.compute(243) (on d3d11)`、`... method 'Load4' at RainParticles.compute(308) (on d3d11)`。结构化 buffer 只支持元素级 `[]` 访问,`Interlocked*` / `Load/Store/Load4/Store4` 只对 `RWByteAddressBuffer` 可用。
→ `_CulledCount` 与 `_ArgsBuffer` 两个都改成 `RWByteAddressBuffer`:`_CulledCount.InterlockedAdd(0, 1u, slot)` 不变(0 是字节偏移);`_CulledCount[0]` → `_CulledCount.Load(0)`;`_ArgsBuffer.Load4/Store4(0, …)` 不变。C# 侧无需改动(`new GraphicsBuffer(Target.Structured, count, stride)` 对两者都兼容)。
 **教训(这条最值钱)**:两个错是同一根因的两次发作,只修 `InterlockedAdd` 会紫色照旧。**正确做法:一次性把该类资源的所有用法都换成同一套语义**,并把日志里的错误**按消息去重**(`Group-Object`)—— 4728 行同一条错误会把别的错误淹掉。

**㊱ 【真机踩坑】compute 编译失败的连带后果:品红 + 严重掉帧。** `FillArgs` 是唯一写 `_ArgsBuffer.instanceCount` 的地方,它一失败:间接绘制参数停留在 C# 初始化时写的值 = 容量 → 每帧硬画 `容量 × 12` 个顶点(实测容量 400000 → **480 万顶点/帧**);剔除结果 buffer 未被写入 → 顶点着色器读到垃圾/零位置 → 退化几何 + 品红。
→ **看到"能画但品红 + 掉帧",先查 compute 编译错误**,`Editor.log` 里搜 `Shader error` 一次即可定性,**并且一定要按消息去重**(同一错误对 6 个 kernel × 4 个平台各报一遍,实测最多 4728 行)。

**㊲ 粒子上限从 SP2 的 100000 下调到 20000(真机实测)。** SP2 的 `_baseAmount = 100000` 是实例数,每粒子 2 个四边形 = **12 个顶点**,顶点数 = `实例数 × 12`;4 倍粒子缩放下容量 400000 → 480 万顶点/帧。20000 实例(24 万顶点)视觉密度已够(SP2 粒子域半径仅 50m,本项目域半径更大、雨丝更长更粗)。需要更密时调 `WeatherConfig.rainStrength`。

**㊳ GPU 回读自检默认关闭。** `GraphicsBuffer.GetData` 是同步回读,会强制 CPU 等 GPU 跑完这一帧,常驻会造成周期性顿挫。→ 默认关,需要时用 dev 命令 **`volkenRainProbe 1`** 打开 / `volkenRainProbe 0` 关掉。

### 10.3 定量结论:域半径自适应 · 密度标定

**域半径必须随相机速度自适应**

> 真机日志:`Rain: 相机速度 162 / 348 m/s 超过域半径 50m —— 雨会表现为间歇/闪断`;同时 `Rain.Shutdown` / `Rain.ResetFade` 都是 **0 次** → 不是淡入淡出在打断(上一轮加的日志自己把问题指出来了)。

- **根因 1 停留时间**:域是**相机相对**的(粒子存相机偏移、`_domainPos` = 相机位置),粒子相对域的速度 ≈ **相机速度**;`域直径 / 相机速度` = 停留时间,50m 半径 + 348 m/s → **0.29 秒**。
- **根因 2 密度**:域是**球**,体积 ∝ r³;粒子数固定时半径翻倍 → 密度掉到 1/8 → **"把 radius 调大"反而更稀**,必须**同时**提高粒子数。

```csharp
needed = camSpeed * 0.6f;                          // 停留 ≈ 1.2s
radius = clamp(max(configured, needed), configured, 400f);
densityScale = (radius / 50)^2.5f;                 // 见下面的 EVE 标定
amount = BaseParticleAmount × quality × rainStrength × densityScale;
```

- **顺序要求(踩过)**:域半径必须在**算完相机速度之后、算粒子数之前**确定,否则粒子数慢一帧、首帧用错值。`Tick` 顺序:`_spectatorVel`(相机速度)→ `EffectiveDomainRadius`(域半径)→ `ApplyParticleAmount`(粒子数,含密度补偿)→ `ComputeOcclusionActive` → `AdvanceFade` → 绘制判定。
- **开关**:`volkenRainAuto 1|0`(默认 1);关掉则完全按 `weather.xml` 的 `rainDomainRadius` 走(高速时必然失败,仅用于对照)。

**粒子密度标定:EVE(blackrack 体积云)的实测配置**

> 参照物 `<RVM_EA>`,即 EVE 体积云预览版(KSP mod,含完整降水系统);雨配置在 `GameData/StockVolumetricClouds/Clouds/particleFields.cfg`:

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

→ **"雨时有时无"的真正原因是密度太低,不是"闪断"**;稀疏粒子阵在移动时肉眼就是时有时无。

**EVE 的其他可借鉴点**

- **域固定 70m 的 3D 粒子场,粒子数不随域变** → 密度天然稳定;本实现的域**随相机速度自适应放大**,必须**显式补偿密度**。
- **`particleSize=0.03` + `particleStretch=20` 得到细长雨丝**(小四边形 + 沿速度方向强拉伸),与本实现"四边形长度 = 拉伸 × 雨丝长度"同思路。
- **`particleSheetCount`**:雨滴贴图是**图集**(6×1 / 12×12 等),用 UV 分格做每粒子随机变体;本实现目前是单张贴图 + 随机种子(种子已从 compute 传进 shader 的 `o.seed`,但**尚未用于 UV 分格**)—— 可作后续改进。
- **`randomDirectionStrength`**:雨滴方向随机扰动强度(雨 0.5 / 雪 2 / 沙尘 10)。
- **`minCoverageThreshold`**:与云覆盖度联动,低于阈值不下雨;本实现目前只按 `WeatherValue` 触发,可后续接上。
- **落地水花是独立 sheet**(`splashes{}`),不在雨丝 pass 里。

**本实现的标定结果**

- `BaseParticleAmount` = 40000(域半径 50m 基准)→ 0.076 /m³(EVE 的约一半;本实现粒子更大更亮,观感密度可略低);
- 密度补偿指数从 r² 提到 **r^2.5**(球体积 ∝ r³,r² 补偿不足;r^2.5 是折中);
- `MaxParticleAmount` 50000 → **400000**(400000 × 12 顶点 = 480 万顶点/帧,是本机 5060 Laptop 的可承受边界);性能由**画质档**显式控制;
- 新增密度诊断日志 `Rain.Density`,直接报出 `个/m³` 与**与 EVE 的比值** —— 判断"够不够密"的直接指标,不用再靠肉眼。

**修正与验收**

-  **修正(2026-09-27)**:"闪断"归因后来被密度标定修正为**主因是密度不足**(固定 10000 粒子在自适应放大的域里密度低到 EVE 的 1/3500);两个原因都真实存在,所以两处都修了(半径自适应 + 密度补偿指数提到 r^2.5)。
- `0.6` 的来历:`直径/速度 = 2r/(r/0.6) = 1.2s`,保证停留 ≥ 1 秒级;另一处日志注释写作 `// 0.6 → 停留约 1.6s,不再闪断`(实测以 1.2s 为准)。
- 密度用 **r²** 而不是 r³:粒子只填相机前方的一个视锥壳层,可见数量 ∝ 屏幕覆盖 ∝ r²(经验折中)。
- **`MaxParticleAmount` 从 50000 提到 200000**:高速时半径 200m+ 会算出十几万粒子,上限太小会**静默截断**并表现成"雨突然变稀";性能改由**画质档**(`ModSettings.RainQuality` 的 0.25/0.5/1.0)显式控制,而不是靠隐藏钳制;撞上限时打一条明确日志(否则"雨变稀"没有线索)。
- **为什么不在 `Update` 里放**:半径必须在**算完相机速度之后、算粒子位移之前**确定(位移钳制要用它),所以放在 `Tick` 里 `_spectatorVel` 之后。
- **验收判据(心跳)**:`domainR=(eff)` 应随 `camSpd` 变化;`Rain:` 不应再出现"域太小导致闪断"的告警(除非撞到 400m 上限)。

### 10.4 JNO 坐标系与朝向结论

> 起因:用户反馈"JNO 是航天游戏,导致雨滴间歇性 + 方向横向"。以下是读 `<JNO_CODE>` 得到的**事实**(不是推测),以及由此定下的修法。

**A. JNO/SR2 没有任何风系统。** `WindManager` / `WindVelocity` 在整个 `jnoCode` 仓库**零命中**;`IPlanetAtmosphereData` 只提供 `CrushAltitude / Height / MeanGamma / MeanMassPerMolecule / MeanSurfaceTemperature / HasPhysicsAtmosphere / PressureCurve` 等,`PlanetAtmosphereData` 有按高度算密度/气压的方法,**没有任何气流速度接口**。→ 风只能来自 `CloudConfig.windSpeed/windDirection`(好处:云雨同向)。

**B. `FlightData.North/East` 是"飞船局部"的北/东,不能当风的地理方向用。** `ICraftFlightData` 文档写明:方向量在**行星位置坐标系**里,但它是**飞船所在处、随飞船姿态**的北/东(`CraftForward/CraftRight/East/North` 是同一组"飞船局部方向")。后果:飞船一俯仰/滚转/偏航,**风的方向跟着转** → 雨丝方向随姿态乱摆(用户报的"横向雨");极点附近 `up` 与自转轴平行 → `cross` 退化 → 方向变 NaN 或跳变。

**修法**:在行星位置坐标系里**解析构造地理北/东**,与飞船姿态完全解耦:

```text
planetNorth = PlanetToFrameVector(0,1,0)          // 行星不自转,北轴就是 +Y
up          = normalize(framePos − frameCenter)
north       = normalize(projectOnPlane(planetNorth, up))     // 极点退化时取 up 的任意垂线
east        = normalize(cross(up, north))                    // 校验:up=+Y,north=+Z → east=+X ✓
windDir     = normalize(cos(dir)·north + sin(dir)·east)
```

副产品:风随行星自转一起走(行星坐标系本就随行星转),符合"地面上的风"的直觉。

**C. 风量级的换算原本没有依据,且会把雨吹成水平。** 旧代码 `windSpeed = cloudCfg.windSpeed * 4000f`(这个 4000 是编的);`windSpeed` 到 0.01 量级就得到 40 m/s 侧风,**与下落速度同量级**,雨丝自然接近水平。→ 改为 `× 1000` 并**硬钳到 25 m/s**:0.01 → 10 m/s。原则:**下落方向必须是雨丝的主方向,风只负责让雨丝倾斜**。

**D. 【已被 I 条修正】"朝向不能掺速度"曾经是错的结论 —— 但"两个速度要分开"这个结论是对的。**  本条最初写的是"雨丝朝向绝不能掺入相机/飞船速度",**这个结论后来被推翻了**:按 SP2 权威源码,朝向**应该**减**玩家速度**(见 I 条);保留本条是因为其中"两个速度各司其职"的拆解仍然有效,而且记录了一次错误判断的来龙去脉。

当时现象:旧代码 `AlignStreaks(_combinedVel - _spectatorVel)`,而 `_spectatorVel` 来自 `Camera.velocity` —— **相机被瞬移/跟随时它会给出不真实的值**,导致雨丝乱摆;据此误判为"任何速度都不能掺",实际根因是**用错了速度源**(该用玩家速度,不是相机速度)。

**仍然成立的部分 —— 这个向量承担了两个不该合并的职责:**

| 职责 | 正确取值 |
|---|---|
| **雨丝朝向**(雨往哪下) | 空气相对运动 − **玩家速度** = `_combinedVel − _playerVel` |
| **粒子在域内的平移**(雨幕不粘在原地) | 空气相对运动 − **相机速度** = `_combinedVel − _spectatorVel` |

拆成两个量分别使用(见 I 条):`AlignStreaks(_combinedVel - _playerVel, cam)` + `_particleVel = dt × (_combinedVel − _spectatorVel)`。

**E. 间歇性的成因:域尺寸与相机速度不自洽。** 域是**相机相对**的(粒子存偏移、`_domainPos` = 相机位置);相机速度一旦远超域半径,粒子每帧被推移的距离接近/超过域直径 → 几帧内穿过整个域并回绕 → **雨幕闪断**;航天器速度动辄几百 m/s 而默认域半径只有 50m,必然出现。→ 两处处理:

1. **粒子位移硬钳到"半域/帧"** —— 保证粒子在域内至少停留 2 帧,回绕不变成闪烁;
2. 相机速度 > 域半径时打一条**明确的诊断日志**,直接建议把 `weather.xml` 的 `rainDomainRadius` 调到相机速度的 2~3 倍。

心跳新增 `| vel: camSpd= combined= wind= playerVel=` —— 一眼看出相机速度与飞船速度的量级,用来确认"横向"和"间歇"是不是速度引起的。

**F. 【真机第二轮】"雨还是横着 + 随相机朝向位移" → 雨丝四边形必须面向相机(billboard 十字)。** 修完 A~E(物理量全部正确:长度轴严格竖直、不含任何相机量)之后,用户反馈**雨仍是横向**且**随相机朝向变化** → 问题不在物理量,而在**几何/基向量**。

根因:旧实现用两个**世界空间固定**的平面搭"十字":

| 片 | 宽度轴 | 长度轴 | 平面法线 |
|---|---|---|---|
| 0 | `side` | `dir` | `other` |
| 1 | `other` | `dir` | `side` |

`side`/`other` 都由 `dir` 与世界 up/forward 推出,**随每帧的 dir 抖动**。后果两个,正好对上反馈:相机视线一旦接近某一片的**平面内方向**,那片就**退化成正对边缘 → 完全看不见** → "雨一闪一闪"(间歇);两片的可见性随视角此消彼长 → "雨滴随相机朝向变化/位移"。

**正确做法:把宽度轴建立在垂直于视线的平面里 —— 两片四边形都正对相机。**

```csharp
lengthAxis = normalize(fallDir)                    // 长度轴:只由物理量决定
side  = normalize(cross(lengthAxis, cam.forward))  // 宽度轴:垂直视线 → billboard
other = normalize(cross(lengthAxis, side))         // 第二片:与 side 正交,真正的十字
```

**一般规则:"长度/朝向"轴只由物理量决定,"宽度"轴由视线决定 —— 这两个绝不能混。混了就是"横向雨";全都用世界固定基就是"视锥边缘消失/闪烁"。**

**G. 【配套】基向量必须在每次下发绘制前重设,不能在 Tick 里写一次。** 实测 `drew≈142/s` 而 `Tick≈20/s` —— 雨会在**多台相机**上各画一次(`OnPreCull` 每台相机都触发);材质 uniform 是**全局共享**的,只在 Tick 写一次的话,后画的相机会用上被别的相机覆盖过的基向量 → 又一层"随视角漂移"。→ 拆成 `AlignStreaks()`(算,存字段)+ `ApplyStreakBasis()`(写材质),在 `RenderForCamera` 里**每台相机各调一次**;`_DomainPos`/`_CamPos` 同理改成绘制时按当前相机设置。

**H. 【真机第三轮 · 关键】雨的"长"必须在屏幕平面内表达 —— 这是"还是横的"的真正根因。** F 改成 `cross(dir, 视线)` 之后不再闪烁,但用户反馈**"还是横的"**;真机日志给出了决定性的数:

```text
lengthAxis=(0.01,-1.00,-0.01)      ← 世界空间里是竖直的 ✓
side=(0.75,0.00,0.66)  other=(-0.66,-0.02,0.75)   ← 两者 Y≈0,都**水平**
dot(lenAxis,view)=0.988            ← 视线几乎正对下落方向(俯视)
```

物理量全对,问题在**投影**:雨丝长度 2.5m 是**垂直**的、宽度 0.1m 是**水平**的;当 `dot(dir,view)→1`(俯视/仰视),垂直方向的长度**在屏幕上被透视压缩到 ≈ 0**,而水平方向的宽度是**完整投影**的 → 屏幕上得到"0.25m × 0.1m 的一小横块" —— **观感就是横向雨**;附带 `cross(dir,view)` 在两者接近平行时数值退化,`side` 会在近水平面里乱转。

**正确做法(第 4 版,标准屏幕对齐 billboard)**:

```csharp
lengthAxis = normalize(fall − view · dot(fall, view))   // 下落方向在**屏幕平面内**的分量
side       = cam.transform.right                        // 宽度严格横跨屏幕
other      = cam.transform.up                           // 第二片,构成真十字
// 视线与下落方向近平行时投影退化为 0 → 用 -camUp 兜底(屏幕上仍是朝下的线)
```

俯视时雨丝在屏幕上仍会**变短**(真实的透视缩短),但它**永远竖直地躺在屏幕上**,不会再变成横块。

**一般规则(这条最通用)**:**任何"细长"的 billboard(雨丝/拖尾/激光)——"长"必须在屏幕平面内表达。** 用世界空间长度轴 + 任意宽度轴,视线一旦接近长度轴就必然退化成"横块"。观感由**投影**决定,不由世界空间几何决定。

心跳/`Rain.Streaks` 诊断现在直接给出判据 **`长宽比` = 屏幕长 / 屏幕宽**:> 3 才像雨丝(可直接判断"横不横",不用靠肉眼)。

**I. 【真机第四轮 · 决策落定】雨丝朝向按 SP2 语义(减玩家速度),但用屏幕空间表达。** 用户明确选择"SP2 语义(雨丝随飞行速度向后倾)"。

**先把两个速度概念彻底分开**(之前混用是多次返工的根源):

| 用途 | 正确来源 | 理由 |
|---|---|---|
| **雨丝朝向** | `_combinedVel − 玩家速度` | SP2 `ParticleHandler.GetPlayerVelocity()` → JNO 对应 `ICraftNode.Velocity` |
| **粒子在域内平移** | `_combinedVel − 相机速度` | 域是相机相对的,不减相机速度粒子会被甩出域 |

 `Camera.velocity` **不能**当玩家速度用:相机被瞬移/跟随时它会给出不真实的值(实测导致雨丝乱摆)。

**为什么不能照搬 SP2 的世界空间方向**:`风+重力−玩家速度` 在 100 m/s 前飞时几乎水平(与竖直约 67°,**物理正确**);但把这个方向投影到屏幕平面后,竖直分量只剩 ~1/10 → 屏幕上就是"横向雨"。**观感由投影决定,不由世界空间几何决定**(这是 H 条的结论)。

**折中实现**(`Rain.LeanGain`,默认 **0.5**,dev 命令 `volkenRainLean` 可实调): **第一版数学写错了,而且真机表现极具迷惑性**:写成 `hUp·dot(rel,hUp) + hRight·dot(rel,hRight) + hFwd·(dot(rel,hFwd)·g)`;因为 `hUp/hRight/hFwd` 是**一组正交基**,前两项加上 `hFwd·dot(rel,hFwd)` 恒等于 `rel` —— **那个增益根本没抑制前后分量**,方向仍然是 `normalize(rel)`。后果:`rel = 风+重力−玩家速度` 在高速飞行时几乎**沿视线**,于是所有雨丝都指向同一个消失点,屏幕上呈现**从中心径向爆散的星芒**(用户截图确认,第一眼会被误当成"横向")。

**正确的拆法**(屏幕内竖直 / 屏幕内横向**按原值**,前后分量**单独乘增益**):

```csharp
rel    = 风 + 重力 − 玩家速度                          // SP2 语义
upComp    = dot(rel, −camUp)      // 屏幕内"向下"分量
rightComp = dot(rel,  camRight)   // 屏幕内横向分量
fwdComp   = dot(rel,  view)       // 前后分量(沿视线)
lengthAxis = normalize( −camUp·upComp + camRight·rightComp + view·(fwdComp · LeanGain) )
```

注意 `lengthAxis` 必须是 `−camUp` 与 `view` 的**线性组合**(而不是 `hUp+hFwd·g` 那种写法),这样增益才真正改变屏幕上的倾角。

| LeanGain | 前后分量权重 | 与"屏幕下方"夹角 |
|---|---|---|
| 0 | 0 | **0°(永远竖直)** |
| 0.5(默认) | 0.5 | **≈26.6°** |
| 1(照搬 SP2) | 1 | **45°** |

**J. 隔离测试开关必须是 `static`。** `DebugFixedVertical` 原本是实例字段 → dev 命令写的是 `VolkenWeather._rain` 那个实例,而实际绘制可能发生在**另一个** Rain 实例上 → **"开了等于没开"**(真机日志证实:`SetRainFixedVertical` 打印了 True,但 `Rain.Streaks` 全是普通模式的值)。和 `_activeInstance` 那次是**同一类坑**:调试开关/全局状态做成静态,天然对全部实例生效。同时加了 `DebugFixedVerticalHits` 计数器 + **绕过节流**的即时日志,用于立刻确认开关生效。

**㉝ 【真机定位】实例回调工作正常,但被一条多余的静态守卫 100% 挡掉。** 换成 `RainCameraRenderer` 的实例 `OnPreCull` 之后,日志第一次出现 `实例 OnPreCull 首次触发` + `preCullCalls` 持续增长(≈20/s)—— 证明**实例回调这条路是通的**(静态 `Camera.onPreCull` 不触发,见 ⑪);但 `draws/s` 仍然 0。逐守卫计数器一行定位:`rfc/s: calls=82 notActive=82 notPending=0 camMismatch=0 noBuffers=0 stateSkip=0 drew=0`,`calls == notActive`(100% 命中),即每次都死在 `RenderForCamera` 开头的 `if (_activeInstance != this) return;`。

**根因**:`_activeInstance` 是**静态字段**,那条守卫是为**静态**转发写的;而 `RainCameraRenderer` 挂在相机上、持有某个 `Rain` 实例的引用;当 `VolkenWeather` 重建 `Rain`(新实例)后,`_activeInstance` 指向新实例,相机上的渲染器却还指着**旧**实例 → 旧实例的每次回调都被这条守卫挡掉(是"第二个实例在给自己开火")。

**修法**:1. **删掉** `RenderForCamera` 里的 `_activeInstance != this` 守卫 —— 实例回调靠下面的 `_cam` 配对就够了;静态路径的防重入判断留在 `OnPreCullStatic` 里;2. `AttachCameraRenderer` 即使相机没变,也要**校正 `Owner` 指向当前实例**(原来只在"相机变了"时才重建,所以 Owner 会一直留在旧实例上)。

**方法论**:`LogThrottled` 的 5 秒节流让守卫日志在 1 秒的心跳窗口里可能完全看不到 —— **排查"函数到底进没进、被哪条挡了"时,累计计数器 + 按秒输出差值才是可靠手段**,不要依赖节流日志。

**㉞ 【真机验证通过】淡变链路已确认正常(2026-09-27 日志)。** 日志:`Rain.SetTargetFade: 0.000 → 1.000 用时 20.00s`、`Rain.AdvanceFade: cur=0.000 target=1.000 from=0.000 elapsed=0.00/20.00s dt=0.0219`,心跳 `fade=0.050 … elapsed=1.01/20.00` 与 `fade=0.765 … elapsed=15.30/20.00`,均 `pendingDraw=True draws/s=0 everDrew=False shutdown=False`。

`fade` 从 0 单调爬到 0.765,`ResetFade` **0 次**(没有东西再压它),`target=1.000`。**至此"淡变卡死"这一类问题全部解决**,剩下的唯一断点是绘制回调 —— 后来由 ㉝ 定位到是实例回调被多余的静态守卫挡掉,已修。这也说明 ㉘ 的追踪方案有效:一条 `fadeState:` 就把"淡入从没触发(`target=0`)"和"触发了但被反复重置(`target=1` 但 `fade` 不涨)"分开了 —— 本次是前者(㉙ 的架构缺陷),一眼定性,没有再来回猜。

**㉔ 【决策】天气系统不联动云层 —— 云厚度/覆盖度/浓度/颜色/风速全部由云自己决定。** 2026-09-27 用户决定;曾实现过 `ApplyCloudCoupling`(覆盖度 / 云色暗化 / 风速,从基线纯函数重算,增益默认 0),代码 + UI + 本地化 + `WeatherConfig` 字段**已全部移除**。理由:联动会让玩家在云面板上调好的云形被天气悄悄改掉 —— 调参结果不稳定,而且分辨不出"看到的是我调的,还是天气改的";**云的观感归云,雨的观感归雨**。如果将来真要联动,必须做成面板里能明确关掉、且默认关的显式选项,不能是隐式行为。(注:`WeatherConfig` 里留了两行注释说明这三个字段为何移除、以及万一旧 XML 带同名节点该怎么处理 —— 与 `CloudConfig.low/mid/highAltitudeThreshold` 那个"死配置不可删"的坑同源。)

### 10.5 【重大变更】雨与雾整体移除,只保留雷电

**移除原因(2026-09-27,用户决定)**:多轮真机调试后雨的方向问题(横向 / 径向爆散 / 随视角漂移)始终未能收敛 → 移除雨与雾的**全部组件**,只保留雷电,准备重新开始。

**已删除的文件**( **2026-10-02 更正:stash 位置 `%TEMP%\volken-rain-fog-stash` 已被清理、不存在**(见雨计划 §10);下表仅作历史记录):`Assets/Scripts/Volken/Weather/Rain.cs`(雨系统主体,约 2000 行,含 GPU 粒子域/间接绘制/淡变/风/密度自适应)、`Assets/Scripts/Volken/Weather/RainParticles.compute`(6 内核计算管线)、`Assets/Scripts/Volken/Weather/RainParticles.shader`(雨丝 billboard,程序化绘制)、`Assets/Scripts/Volken/Weather/FogRenderer.cs`(全屏高度雾)、`Assets/Scripts/Volken/Weather/HeightFog.shader`(高度雾解析积分)、`Assets/Scripts/Volken/Weather/Audio/enviro_rain_1~3.ogg`(环境雨声)。

**被清理的引用**:`Assets/ModData.asset`(打包清单权威来源,最关键、容易漏)—— `ModTools` 的 `ModData._otherAssets` 存**资产 GUID 列表**,删文件不会让 GUID 自动消失,实测删除后重生成的 manifest(`Temp\ModManifest.xml`、`ModAssetBundles\StandaloneWindows64\volken.manifest`)里**仍带这 6 条**:`enviro_rain_1/2/3.ogg`、`HeightFog.shader`、`RainParticles.shader`、`RainParticles.compute`(GUID `f34a10ca…` / `ce009423…` / `98bbe0bd…` / `f0ec7a8f…` / `9e3e2564…` / `f4593250…`);已手工从 `_otherAssets` 删除这 6 条,`_otherAssets` 现为 **20 项**、每个 GUID 都能在 `Assets/` 解析到真实文件(0 个悬空); 重做雨/雾时**要把 GUID 加回来**。其余:`Mod.cs`(移除 `volkenRainProbe`/`volkenRainAxis`/`volkenRainLean`/`volkenRainAuto` 四个 dev 命令与 `SetRainAuto`/`SetRainLean`;`WeatherStatus` 去掉 rain/fog 两行)、`ModSettings.cs`(`RainQuality`/`FogEnabled` 与 `FormatRainQuality`)、`WeatherConfig.cs`(全部 rain*/fog* 字段共 21 个及 `ClampAll`/`CopyFrom` 条目)、`WeatherPanel.cs`(`BuildRainGroup`/`BuildFogGroup`)、`VolkenMod.cs`(`MountFogRenderer` 与两处调用)、三语言文件(`RainQuality`/`FogEnabled` 设置文案与 `Rain*`/`Fog*` 面板文案,各约 30 行);`CloudRenderer.LinearSceneDepth` —— **保留但已无消费者**(当初为雾加的,注释已说明)。

**保留的东西**:**雷电全链路** `LightningBolt.cs` / `LightningModule.cs` / `LightningBolt.shader` / 5 条雷声;**天气状态机**(`VolkenWeather` + `WeatherConfig` + `WeatherTypes`;其中 `WeatherTypes` 后由 ㊿ 整个删除,见 §10.6)。`WeatherTypes.RainTrigger` / `IsRaining` / `foggyDawn`(黎明起雾)**现在没有渲染消费者**,只是天气标度里的分档与状态机行为,保留是因为它们是天气系统的一部分且重做雨/雾时是最自然的触发判据(SP2 也是 2.25); 别误以为"改了 `foggyDawn` 会起雾" —— 雾的渲染已删除。

**经验教训**:完整复盘已成文 [`weather-rain-fog-postmortem-2026-09-27.md`](weather-rain-fog-postmortem-2026-09-27.md)(4 层根因 / 27 条铁律 / 8 条已证伪思路 / 量化基线 / 重做起步清单),重做雨/雾前必读;三条通用结论:**观感由投影决定,不由世界空间几何决定**(细长 billboard 的"长"必须在屏幕平面内表达);**"长度/朝向"轴只由物理量决定,"宽度"轴由视线决定**;**每帧都会调到的路径里禁止出现"重置动画进度"的副作用**(重入守卫只能看目标值)。

### 10.6 天气配置沿革:多预设 → 并入 PlanetConfig → 删除全局档位

> 本节按时间顺序记三个阶段:**多预设方案(㊴~㊸,已被取代)** → **并入 `PlanetConfig`(㊹~㊾,后被取代)** → **删除全局档位(㊿,现行方向)**。
>  现在**现行**的形态是"**天气参数按预设名独立存**"(`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`,见文首),㊹~㊾ 记载的内联 `<Weather>` 形态也已作废 —— 但三阶段的经验(尤其 ㊽ 那个真 bug 与 ㊾ 的兼容兜底)仍然有效。

- **㊴【重构(已部分撤销)】天气配置的落盘方案一度换成云层那一套。** 改后与云层一一对应:配置类 `VolkenWeatherConfig`、清单类 `PlanetWeatherConfigList`(已删除)、清单文件 `UserData/VolkenWeatherConfig/PlanetWeatherConfigList.xml` 与预设文件 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`(已不读不写)、默认预设名 `Default`(已不存在)、`VolkenWeatherConfig.GetAllConfigNames(planet)` 与 `SaveToFile(planet, name)`/`LoadFromFile(planet, name)`、`VolkenWeather.CurrentConfigName`(均已删除);面板「配置管理」曾与云层同形,现只保留 **保存 / 重置 / 路径**。改前形态是每行星固定一份 `UserData/VolkenConfig/{行星}/weather.xml`(与云配置共用目录、**没有清单文件、没有多预设**)。当时另开 `VolkenWeatherConfig/` 的理由:云与天气都"按文件名枚举预设",同目录会让两套预设名互相污染(给云起名 `Stormy` 会出现在天气下拉框里,反之亦然)。**→ 后续结论:只要天气不再有自己的预设名,这个问题就自动消失。**
- **㊵【重构】配置类按「5 个 Section」组织,XML 节点 = 面板分组 = 数据块,三者同名同序。** `<Overall>`(① 总体:总开关/动态天气/天气节奏/黎明起雾)、`<CloudLinkage>`(② 云层联动机制,占位,默认全 0 = 恒等)、`<Rain>`(③ 雨,占位;实现已移除)、`<Fog>`(④ 雾,占位;实现已移除)、`<Lightning>`(⑤ 雷,唯一在跑的子系统);每块是一个 `[Serializable]` **嵌套类**(`OverallSection`/`CloudLinkageSection`/`RainSection`/`FogSection`/`LightningSection`),各自带一个 `CopyFrom(同类)`,好处是"新增一个字段要同步改哪些地方"收在离字段最近的一处。 `CopyFrom` 用**逐块委派**而不是 `MemberwiseClone`:嵌套类实例是**引用**,浅拷贝会让新旧配置共享同一个 Section 对象,`CopyFrom` 之后改一个等于改两个。
- **㊶【重构】面板与配置保持一致的分组,雨/雾/云层联动三组整组禁用。** 面板顺序 = XML 节点顺序:状态(只读:行星/天气值/运行状态/海拔+太阳时,~~预设名~~ 已撤销)、① 总体(活)、配置管理(活 → 最终只保留 **保存当前 / 重置为默认 / 配置路径(只读)**)、② 云层联动(未实现,`enabled`/`coverageGain`/`darkenGain`/`windGain`,默认 0)、③ 雨(已移除占位,10 个参数:数量/域半径/自适应/落速/强度/风影响/雨丝长宽/雨声音量)、④ 雾(已移除占位,8 个参数:底高/厚度/密度/高度衰减/最大不透明度/起雾距离/雾色混合)、⑤ 雷(原有 15 个参数 + 立刻劈一道 + 落雷状态)。**为什么"禁用"而不"隐藏"**:用户明确要求雨雾保留占位,且 `ItemModel.Enabled = false`(**基类属性**,`TextModel`/`SliderModel`/`ToggleModel` 都有)比"不画这一组"更诚实,每组第一行是灰字说明("雨系统已整体移除待重做,以下参数为占位,修改不会有任何效果")。
- **㊷【API(本节方案;多预设相关的几个后来被裁掉)】** ❌ 已删:`static PlanetWeatherConfigList ConfigList`、`CurrentConfigName`/`AvailableConfigs`、`LoadConfigPresetInPlace(name, resetState)`、`SaveConfigAs(newName)`;✅ `SaveCurrentConfig()` **保留**;✅ `ResetCurrentConfigToDefault()` **新增**(为"就地重置"补的);✅ `PlanetConfigList.GetWeather(planet)` **新增**(取/补这条记录的天气参数);`ApplyPlanet` 改为"从清单那条记录上取 `Weather` 引用";面板侧 `WeatherPanel.Build(inspectorModel)` —— 最终形态**去掉了第二个 `Action` 参数**。
- **㊸ 旧文件无兼容负担 —— 但这是"运气",不是"设计"。** `VolkenConfig/{行星}/weather.xml`(旧方案)在真机上**从未产生过**(改之前一次都没落盘,实测 `Get-ChildItem -Recurse -Filter weather.xml` 零命中),因此本次重构**不需要迁移逻辑**; 但这也意味着**没有任何东西验证过"旧扁平 XML 能否被新嵌套类读出来"** —— 若真有玩家手上有旧的扁平 `weather.xml`,症状会是 `XmlSerializer` 把整段当未知元素忽略、所有字段回落到默认值(全部关闭),**不会报错**。编译验证:`dotnet build Volken.csproj -t:Rebuild` → **0 错误**(6 个既有警告);`WeatherPanel` 引用的 **88 个本地化 key 在 EN-US / ZH-CN / RU-RU 三份文件里全部存在**。
- **㊹【当时的最终方案】天气参数内联进 `PlanetConfig`,与云层共用一份清单文件。** `UserData/VolkenConfig/PlanetConfigList.xml` 是唯一的行星配置文件:`<PlanetConfig PlanetName="Droo" CloudConfigName="Default" ExtraCloudConfigName="Atmospheric">` 内的 `<Weather>` 子树含 `<Overall>`/`<CloudLinkage>`/`<Rain>`/`<Fog>`/`<Lightning>`;云层参数本体仍是 `UserData/VolkenConfig/{行星}/{预设名}.xml`(保持不变)。职责划分:云层**参数本体** = 独立文件 `{行星}/{预设名}.xml`(一层可有多套具名预设);云层**用哪套预设** = `PlanetConfig` 的属性(`CloudConfigName`/`ExtraCloudConfigName`);天气**参数本体** = **内联在 `PlanetConfig.Weather`**(当时认为一颗行星只有一套天气)。 **此形态后来又被取代** —— 现行是天气自己的预设文件(见文首)。
- **㊺ 类型与文件的重排**:`Core/PlanetConfig.cs` **删除**(其内容 `PlanetConfig` 类并入下者);`Core/PlanetConfigList.cs` **新增**(`PlanetConfigList` + `PlanetConfig` 同处一个文件,因为"一份记录同时记云与天气"必须在一处才看得清);`Weather/VolkenWeatherConfig.cs` **删除**(内容并入 `Core/PlanetConfigList.cs`,仍保留 `VolkenMod.Weather` 命名空间);`Weather/WeatherConfigList.cs` **删除**(`PlanetWeatherConfigList`/`PlanetWeatherConfig` 整套作废);`UserData/VolkenWeatherConfig/` 当时**不再创建、不再读取**。( 最后一个后来又被翻回来:现行正是用 `VolkenWeatherConfig/` 目录。)
- **㊻ 去掉的东西(㊴ 里加过、当时全部撤销)**:`PlanetWeatherConfigList` / `PlanetWeatherConfig` 两个类;`VolkenWeather.ConfigList`(静态清单)、`CurrentConfigName`、`AvailableConfigs`;`LoadConfigPresetInPlace` / `SaveConfigAs`;面板上的「当前配置」只读行 /「保存为新配置」按钮 /「加载配置」下拉框;`VolkenWeatherConfig` 上的 `CONFIG_FOLDER` / `DefaultFileName` / `SaveToFile` / `LoadFromFile` / `GetConfigPath` / `GetAllConfigNames`(**落盘职责移交 `PlanetConfigList`**);三语言里的 `Volken.UI.WeatherPreset`。**当时留下的**:面板「配置管理」组只剩 **「保存当前配置」/「重置为默认」/「配置路径(只读)」** —— 与云层语义一致(**改内存 → 手动保存**)。
- **㊼ `VolkenWeather.Config` 是「清单里那条记录上的实例本身」(引用,不是副本)。** `VolkenWeather.ApplyPlanet` 里 `Config = list.GetWeather(planetName);`;`PlanetConfigList.GetWeather(planetName)` 会**就地补全**(没这条记录 → 建一条 `CloudConfigName = "Default"`;记录没有 `<Weather>` 节点 → 挂一份默认(全关)),它**不落盘**,落盘只发生在两处:玩家点"保存当前配置",或云层的 `AddConfig`/`SetConfig` 顺带写整份清单。 代价:面板一改就**立刻改了清单内存对象**,所以「重置为默认」必须显式实现,`ResetCurrentConfigToDefault()` 用 `CopyFrom(CreateDefault())` **就地写**而不是换引用 —— 换引用会让 `Config` 指向一个游离对象,与清单脱钩。
- **㊽ `PlanetConfigList.AddConfig` 顺带修掉一个真 bug(合并后才会发作)。** 原实现是**无条件** `configList.Add(new PlanetConfig(...))`,对已存在的行星会**追加第二条同行星记录**;云的调用点都先用 `ExistsInConfig` 挡了所以一直没暴露,但天气合并进来后一条记录里带着整份天气参数,**再来一条同行星记录就会把天气参数分叉成两份**(改一条、读另一条 → 症状是"我的天气设置时不时自己变回去")。→ 改成已有记录就**只更新预设名**(`SetConfigName`)、不再 `Add`;只影响"已存在的行星再 AddConfig"这一种情形,而那种情形在改前是 bug,所以没有兼容负担。 **这条结论与现行形态无关,依然有效。**
- **㊾ 旧记录兼容:`<Weather>` 节点不存在时会怎样?** 改前实测(这台机器的 `UserData/VolkenConfig/PlanetConfigList.xml`,16 条记录):**没有 `<Weather>` 的记录是自闭合标签**(`… />`)而不是带子节点的元素;`XmlSerializer` 对缺失元素是"不碰"而非"置 null",但**不去赌** —— `PlanetConfigList.LoadFromFile` 里加了显式兜底(缺失就补一份默认 + `EnsureSections` + `ClampAll`),老玩家的清单不会读坏,只是天气部分是默认关闭。而且**实际会自愈**:进一次飞行场景,这份文件就会被重写成带 `<Weather>` 的形态,但**第一次进场景的那一瞬间**必须靠上面的兜底才不出 NRE。 **同一条思路现行仍适用**:读配置一律"缺就补默认 + 兜底",不依赖 `XmlSerializer` 的行为细节。编译验证:`dotnet build Volken.csproj -t:Rebuild` → **0 错误**;三语言文件用 `XmlReader` 严格校验**全部 well-formed**;面板/清单/状态机引用的本地化 key 在三份文件里全部存在。
- **㊿ 把 §2/§5 里"移植 SP2 天气标度"这条路线正式作废。** SP2 是一套**全局预设系统**:`WeatherTypes`(Clear / Few / Broken / Overcast / Rainy / Stormy / Heavy / Foggy)是**中间层**(天气档位先被统一决定,再由它去驱动云层、雨、雾各自的预设与阈值,玩家调的是"今天什么天气");Volken 去掉这个中间层 —— **每个子系统(云层 / 雨 / 雾 / 雷)的参数全部是玩家直接设置并序列化的数值**,面板上就是一堆直接的滑块;"天气值"这个连续量一度仍然存在(驱动状态机随机与淡变、各子系统自己的触发阈值),但**不再映射到任何档位、也不驱动任何"预设"**;云层**完全由 `CloudConfig` 决定,天气不碰**(见 §10.8 ㉔)。
  删除:`Weather/WeatherTypes.cs`(**整个文件**,连带 `.meta` 与 `.csproj` 条目);档位常量 `Foggy`/`Clear`/`Few`/`Broken`/`Overcast`/`Rainy`/`Stormy`/`Heavy`(不再需要,默认值改字面量);派生阈值 `RainTrigger`/`HeavyRainThreshold`/`LightRainCeiling`/`LightningTrigger`/`FogCeiling` → 变成配置字段;`Classify` → `VolkenWeather.DescribeWeatherValue(float)`(纯显示,无逻辑);`VolkenWeather.IsRaining` 里的 `RainTrigger` 常量 → 读 `Config.rain.triggerValue`。新增:`VolkenWeatherConfig.DefaultWeatherValue`(**常量**)`0.25f`(**没有档位含义**,原 `WeatherTypes.Few`)、`OverallSection.maxWeatherValue` `3f`(随机天气目标的**取值上限**,下限恒 0;原为 SP2 硬编码 `[0,3]`)、`RainSection.triggerValue` `2.25f`(原 `WeatherTypes.RainTrigger`)、`LightningSection.stormValue` `2.5f`(原 `WeatherTypes.Stormy` / `LightningTrigger`)。
   其中 `rain.triggerValue` / `lightning.stormValue` 与整个"天气值"标度**后来被整体删除**(各子系统只看自己的 `enabled` + 节奏参数;见 [`weather-cloud-decoupling-2026-10-01.md`](weather-cloud-decoupling-2026-10-01.md) §8)。保留 `VolkenWeather.DescribeWeatherValue` 时的判断依据是**它不驱动任何逻辑**(没有任何 `if (name == "Rainy")`),所以不构成"预设系统",只是个 `float → string` 的格式化函数; **不要在它上面加逻辑** —— 那一步就等于把 SP2 的中间层又建回来了。细节:`PickRandomWeather` 的"晴后必转云"偏置从硬编码 `0.4f` 改成**按上限的比例**(`ceiling * 0.133f`);面板「天气值(强制)」滑块范围从写死的 `[-1, 2.75]` 改成 `[-1, ceiling]`。编译验证:`dotnet build -t:Rebuild` → **0 错误**;全工程 `WeatherTypes` **只剩文档注释里的历史说明**,零代码引用。

### 10.7 待真机确认的开放问题(原 §10.3)

> 原 9 条中与雨/雾直接相关的 6 条已随 §10.5 的移除作废,记录保留在那里的经验教训中;以下为**移除后仍然有效**的条目。

1. **打包是所有天气功能的前置条件**:权威来源 `Assets/ModData.asset` 的 `_otherAssets`(GUID 列表),生成物 `Temp\ModManifest.xml` 与 `ModAssetBundles\…\volken.manifest`。缺 shader → 闪电静默失效并在日志打 `shader NOT FOUND` / `LoadVolkenAsset: '...' not found`;缺音频 → `loaded 0/5 thunder clips`。
2. **局部太阳时无法区分日出侧的 6 点与日落侧的 18 点**(SR2 侧拿不到行星自转轴的可靠朝向,用本地"北"推断会随飞行翻转)→ 实现把 0~12 一律当"上行"。雾删除后它只影响天气标度的分档(`foggyDawn` 的黎明档),无渲染副作用,优先级最低(见 `VolkenWeather.ComputeLocalSolarHour` 注释)。
3. **天气配置的 XML 往返** ——  现在是**独立预设文件**(`UserData/VolkenWeatherConfig/{行星}/{预设}.xml`,见文首与 §10.6),本节早期记录的"内联 `<Weather>` 子树"形态**已被取代**。本工程**没有离线验证手段**(跑不了 `XmlSerializer`),失败是**静默回落默认(全关)且不报错** → "面板上的值没跟着 XML 变"本身就是失败信号,读写两个方向都要试,并回归云的「另存为 / 加载配置」不受影响。
4. **落雷的地形贴合**按"行星半径的正球面"近似(忽略局部地形)→ 山峰上的落雷可能插进山体;方案 A 的可接受损失,二期可换 `Physics.Raycast`。
5. **雷暴循环的时间基准**用 `Time.deltaTime`(受时间加速影响)而非 `unscaledDeltaTime`:与 SP2 的"游戏时间驱动"一致,但极高时间加速下雷击会密集,待实测确认是否加钳制。
6. **`CloudRenderer.LinearSceneDepth` 现在没有消费者** —— 它是为雾加的公开属性(一行只读,零成本),雾删除后**保留**;重做雾时复用,或直接删掉。

### 10.8 验证手段(可复用)

**① `dotnet build Volken.csproj`** —— 直接编译整个 mod 程序集,**约 2~25 秒**,报错带行号,比等 Unity 刷新快得多。两点注意:

- Unity 未刷新时 `Volken.csproj` 不含新文件,需手工补 `<Compile Include="…" />` 再编(仅用于验证;Unity 下次刷新会重新生成,手工改动会丢);
- **它只编 C#,完全看不到 shader/compute 的错误**。

**② Unity 的 shader 编译器日志** —— `Editor.log` 里的 `Shader error in '<Shader 名>': … at <文件>(<行>) (on d3d11)` 是**唯一能离线抓到 shader 语法/内置函数错误**的途径(曾靠它抓到 `expm1` 不存在)。限制(实测确认):

- Unity **只编译被素材引用**的 shader;未被引用的(如当时的 `RainParticles.shader`)只有 import 记录、没有 compile 记录 → 能不能过要等游戏里加载才见分晓;
- `Camera` 相关 shader(`LightningBolt`)会因运行时 `Shader.Find` 被编译,所以有记录;
- `UnityShaderCompiler.exe` 是**端口式 worker 进程**,不能命令行单独调用去校验某个 shader。
-  **错误必须按消息去重后再统计**(同一错误对 N 个 kernel × M 个平台各报一遍,实测最多 4728 行,会把第二条**不同的**错误完全淹没)。

**③ 资源台账 `volkenAssets`** —— 打包清单是显式的,最容易出的问题是"资源没打进 bundle"。`Mod.LoadVolkenAsset<T>` 把每次加载结果记进一张表,`volkenAssets`(或 `volkenWeather`,它会顺带打印)输出形如 `ok …` / `MISSING(required) …` / `missing(optional) …`,**一条命令就能区分"代码 bug"和"打包漏了资源"**。

**④ GPU 回读自检(`VolkenRain:GPU`,每 10 秒)** —— 雨的整条链路(剔除 → `FillArgs` 写参数 → `RenderMeshIndirect` 取参数 → shader 按 `SV_InstanceID` 索引 `_Positions`)全在 GPU 上,CPU 侧只看得到"我下发了绘制命令",**无法区分**"画了但没显示"和"根本没准备画"。回读间接绘制参数 buffer 的 `_ArgsBuffer[1]`(instanceCount),判据:

| instanceCount | 结论 |
|---|---|
| `indexCount == 0` | mesh / index buffer 异常 |
| `instanceCount == 0` | **剔除或参数环节**有问题(视锥判定 / 原子计数 / buffer 绑定)—— 不是绘制环节 |
| `> capacity` | 计数器或 buffer 绑定异常 |
| `≈ active` | GPU 侧正常;若屏幕无雨 → 问题在**绘制/着色**(参数 buffer 没被 shader 用上 / 实例索引取错) |

 实现细节:`GraphicsBuffer.GetData` 是**同步**回读(会强制 CPU 等 GPU 跑完这一帧)→ 只在"正在下雨"时做、间隔 10 秒、且**不在下发绘制的同一帧读**(下一帧读,此时 GPU 已完成那一帧);**默认关闭**,需要时用 dev 命令开。
