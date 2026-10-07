# 附加相机(PIP)下雨 —— 每相机一套雨实例

> 状态:🚧 实施中 —— C# 侧已落地(`dotnet build Volken.csproj` = 0 错误),**未做 Unity 真机验收**
> 日期:2026-10-04
> 关联:[场景门控](monobehaviour-scene-gate-2026-10-02.md)(额外相机扫描的出处)/ [雨开关](rain-toggle-scene-switch-2026-10-02.md)(同类的"每实例一套资源"铁律)/ [反射相机适配](archive/reflection-adaptation-2026-09-02.md) / [TSS 移植](archive/ksa-temporal-upscale-port-2026-08-24.md) §299(单一主相机挂载的原始决策)

## 0. 现象与结论

用 PIP mod 时,**多余的相机只会渲染雷电与云层,不渲染雨滴**。原因是三个子系统的视觉载体天生不同,而与"相机"的关系决定了它是否跟着额外相机走:

| 子系统 | 视觉载体 | 到额外相机的方式 |
|---|---|---|
| 雷电 | 世界空间根物体(`LightningBolt`:不 `SetParent`,任何相机看得见就渲染) | 与相机无关 → 自动有 |
| 体积云 | 相机上的 `CloudRenderer`(屏幕空间后处理) | `VolkenUserInterface` 的 1 Hz 额外相机扫描自动挂 |
| 雨 | 相机上的 `RainParticles`(GPU 粒子 + `Graphics.RenderMeshIndirect`,绘制参数 `camera = 本相机`) | **原来只在主视图相机上(单实例)** → 额外相机没有 |

即:这不是某个相机的渲染故障,而是"单一主相机挂载"(见 [TSS 移植](archive/ksa-temporal-upscale-port-2026-08-24.md) §299 的原始决策)在雨这一侧的必然表现。

## 1. 方案 A:跟随既有扫描挂雨(【决策:2026-10-04,用户】)

| 文件 | 改法 | 为什么 |
|---|---|---|
| `Core/VolkenUserInterface.cs` | 1 Hz 额外相机扫描里,通过 `IsExtraWorldCamera` 的相机**除了挂云也挂雨**(`RainParticles.AttachExtra`);开关 `ModSettings.ExtraCameraRain`(默认开),关掉时顺手 `DestroyExtraInstances()` | 复用已验证的相机识别与 1 s 节流;两个开关独立(云便宜、雨贵) |
| `Weather/Rain/RainParticles.cs` | 新增 `Live` 实例表(`Awake` 加 / `OnDestroy` 删)+ `_isMainView` 身份;`AttachExtra(cam)` 幂等挂载(不碰 `_current`,主视图仍归 `SyncToCurrentView`) | 每相机一套实例资源(compute / buffer / 材质);主视图实例的身份用于分清"谁的诊断量算数" |
| `Weather/Rain/RainParticles.cs` | `ApplyConfig` 的实例侧重建从"只作用于当前视图实例"改成**遍历 `Live`**(删掉只找一台相机的 `FindCurrentInstance`) | 容量 / 雨丝长宽是**实例资源**:改了配置,额外相机上的实例也要重建,否则 PIP 里的雨保持旧容量与旧雨丝尺寸 |
| `Weather/Rain/RainParticles.cs` | 每实例用自己的相机:软粒子深度取**本相机**的 `CloudRenderer.LinearSceneDepth`,`_InvFade` / `_LinearSceneDepth` 用本实例的局部判据(不再读共享静态) | 静态量只反映主视图;读它会让额外相机套上主视图的深度状态 |
| `Scripts/ModSettings.cs` + `Content/Languages/{ZH-CN,EN-US,RU-RU}.xml` | 新设置 `ExtraCameraRain`(默认开)+ 三语说明 | 每相机都要付显存与 GPU(见 §3),必须能单独关掉 |

## 2. 铁律:**共享诊断量归主视图实例**

`RainParticles` 里有一批 `static` 输出量(`LastAltitudeFade` / `LastWaterFade` / `LastSubmerged` / `LastAltitude` / `LastCeiling` / `SoftDepthReady` / `LastCamSpeed` / `LastCraftJump` / `LastStretch` / `LastAxis*` / `PreCullCalls` / `DrawCalls` / `Recenter*` / `AssetsStatus`)。它们是**"最后绘制的那台相机"的值** —— 多实例后必须只由主视图写:

- `RainAudio` 的门控读 `LastAltitudeFade × LastWaterFade`(`RainAudio.cs`):让 PIP 相机去写,主视图的雨声会被 PIP 相机的水下 / 海拔状态压掉或不恢复。
- 天气面板与心跳日志读其余量:被顶掉后看到的是"某个相机的数",排障会误判。

实现 = `IsMainViewCamera(cam)`(`GameCamera.NearCamera`;独立模式 / 判不出时按"是"处理,宁可多写也不能让面板 / 雨声失去数据源)。实例自己的绘制只用**局部变量**(`stretch` / `softReady` / `effR` / `_lastCamVel`),不读那批静态。

配套:每帧 GPU 回读(位置分布 / 复活计数)只让主视图做 —— 额外相机每 10 s 再排一次回读纯属浪费。

## 3. 代价(为什么要有独立开关)

- **显存**:每实例 `_positions` + `_randomData` = `amount × 16 B × 2` → 配置 `amount=100000` 时**每台额外相机约 3.2 MB**,再加雨丝贴图 / 网格 / 材质各一份。
- **GPU**:雨的规模是 **2 次 compute dispatch + 1 次实例化间接绘制**,**与视口大小无关** → 320×240 的 PIP 窗口也按满额再跑一遍;只有填充率那部分变小。多一台额外相机 ≈ 雨的 compute 成本 ×2。
- 因此:只在主开关(`Enabled`,即配置里 `rain.enabled` + 面板勾选)打开时才挂;设置关掉会立刻清理已挂实例。

## 4. 验收判据

- 编译:`dotnet build Volken.csproj` = 0 错误,警告仍是既有 3 条。
- 真机(未做):
  1. 飞行中开雨 + 打开 PIP:**PIP 画面里也有雨**,且日志出现 `RainParticles: 附加相机挂载 camera='...'`,随后该实例自己的 `assets ready` + `buffers created`(与主视图各一套)。
  2. **主视图雨声不被 PIP 顶掉**:贴着水面 / 高空各试一次,雨声淡出淡入行为与没有 PIP 时逐条一致(验证 §2 的静态量归属)。
  3. 面板改容量 / 雨丝尺寸 → 主视图与 PIP **同时**跟着变(验证 `ApplyConfig` 遍历 `Live`)。
  4. 设置里关掉「附加相机雨」:PIP 里的雨在 1 s 内消失(日志 `附加相机雨已关闭 → 移除`),主视图不受影响;关掉「附加相机云层」同理只影响云。
  5. 退到设计器 / 主菜单:两处都不残留(场景门控对每个实例同样生效)。
  6. 编辑器预览台(`RainPreview`)不受影响(独立模式,门控短路)。

## 5. 已知限制(刻意没做)

- 雨的**高度 / 水下闸门参考系 = 玩家飞行器采样 + 本相机海拔**:PIP 看同一飞行器时正确;若 PIP 看的是**别的**飞行器或远处,闸门会按玩家的 craft 采样算(要正确就得给每个实例独立的 craft 采样,收益低、改动大)。
- 未走"单实例多相机绘制"(方案 B):那能省 N 倍 compute,但雨丝朝向必须**按目标相机重设** → 要每相机一份材质实例,正是 [雨雾移植复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) ㈦ 出过事故的那条线;当前方案 A 用每相机独立实例天然绕开它。
