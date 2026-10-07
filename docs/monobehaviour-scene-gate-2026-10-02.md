# MonoBehaviour 场景门控 + 每帧热点审计(2026-10-02)

> 状态:🚧 实施中 —— C# 侧已落地(`dotnet build Volken.csproj` = 0 错误),**未做 Unity 真机 / 编辑器验收**。
> 日期:2026-10-02
> 关联:[雨/雷性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md)(其 §3.6 的 craft 链重复、§3.2 分叉合批)· [天气↔云解耦](weather-cloud-decoupling-2026-10-01.md)(行星生命周期只归 `VolkenClouds`)
> 定位:回答"这些 `Update` 是不是只在 FlightScene 跑",把**确实跨场景空转**的几处按场景信号关掉,并清掉渲染路径里每帧的 LINQ 分配。

---

## 0. 结论

分化点不是"哪个类继承了 `MonoBehaviour`",而是**它挂在哪个物体上**:

| 宿主 | 成员 | 飞行外是否跑 |
|---|---|---|
| 相机 / 场景物体(随 Flight 场景卸载销毁) | `CloudRenderer`、`FarCameraScript`、`RainParticles`(面板开关才挂)、`LightningBolt` + `SelfDestruct` | ❌ 不跑 |
| `DontDestroyOnLoad` 常驻物体 · **本次已门控** | `WeatherTicker`、`RainAudio`、`LightningModule`(懒建后常驻)、`VolkenUserInterface` | ✅ 改成不跑 |
| `DontDestroyOnLoad` 常驻物体 · 刻意保留 | `ProfilerController`(每帧只读一个设置项,做双向同步)、`BootstrapRunner`(注册完即自毁) | 跑,量可忽略 |
| 编辑器 / 控制台专用 | `RainPreview`(仅编辑器)、`RainAxisProbe`、`NoiseVisualizer` / `RaymarchDebug`(全工程零实例化) | 玩家不受影响 |

量级:常驻那几项合计 **<1 µs/帧(估算)** —— 门控是"卫生",不是帧率;真正吃帧的是飞行内两条,已按 §2 的后两行改掉,其余剩余项见 §3。

## 1. 判据:SR2 的场景机制

- 场景名常量见 `SceneNames.cs`:`Menu` / `Design` / `Flight` / `PlanetStudio` / `TechTree` / `Transition`。
- 切换流程 `SceneManager.cs:506-563`:`LoadScene("Transition", Additive)` → **`UnloadSceneAsync(上一个场景)`** → `LoadScene(目标场景)`(单模式,销毁旧场景全部物体)→ `OnSceneLoaded(目标场景)` 才广播 `SceneLoaded`。
- 只有 `DontDestroyOnLoad` 的物体跨场景存活 —— 这就是"相机上的 Update 天然只在 FlightScene"的原因。
- `Game.InFlightScene` 是**提前写好的 bool 字段**(`SceneManager.cs:894-901`),事件回调里读它是准的;`SceneManager.CurrentScene` 走 `GetActiveScene()` 原生调用,**别用它做每帧判定**。
- 启停信号复用现成的:`VolkenClouds.OnSceneLoaded` 每次场景加载都广播 `PlanetEnvironment`(非 Flight → `InFlight == false`)。**不要再新增 `SceneLoaded` 订阅者**(解耦决策:行星生命周期只归 `VolkenClouds`)。

## 2. 实施记录(2026-10-02)

| 文件 | 改法 | 为什么 |
|---|---|---|
| `Weather/VolkenWeather.cs` | `WeatherTicker` 存字段;创建时与 `OnPlanetChanged(env)` 里按 `env.InFlight` 切 `enabled` | 常驻宿主:原来在所有场景每帧进 `Tick` 再靠"取不到 `GameCamera`"早退 |
| `Weather/Rain/RainAudio.cs` | `Update` 首行:非独立模式 + 非飞行 + `_fade <= 0` + **素材加载已定局**(全齐或重试用尽)→ 返回 | **保留离场淡出**(`_fade` 跑完再停表);加载重试窗口内仍在跑,`StandaloneMode`(编辑器预览台)不受影响 |
| `Weather/Lightning/LightningModule.cs` | `SetActive(bool)` 首行 `enabled = active`(放在 `_running` 早退**之前**);停用时一并 `StopThunderVoices()` | 组件懒建后永久留在常驻宿主上;`Update` 停表后没人再兜住暂停 → 已排程的雷声必须当场收干净(否则关雷电 / 离场后还在响) |
| `Core/VolkenUserInterface.cs` | `Start` / `OnSceneLoaded` 里 `enabled = 场景是 Flight` | 常驻组件:飞行外连托管 Update 调用都没有(原来是每帧进 Update 再早退) |
| `Core/VolkenUserInterface.cs` | `SceneLoaded` 订阅从 `Start` 移到 `Awake`(带 try/catch) | 启停只由这条事件链驱动 → 必须排在 `VolkenClouds` 之前:别家 mod 的处理器抛异常会中断整条链,排后面的订阅者被静默跳掉(本仓库已有同源事故) |
| `Core/VolkenUserInterface.cs` | 每秒扫描:`FindObjectsOfType<Camera>()` → `Camera.allCameras` | 前者是每秒一次的全场景扫描;只扫启用中的相机(禁用相机不渲染,也不需要云渲染器) |
| `ModUpdater.cs` | 等主菜单的 `while(...) yield return null` → `WaitForSecondsRealtime(0.5s)` + `IsInMenuScene()` | 版本落后且玩家没点"不再提醒"时,这条协程会在**所有场景**每帧判定一直到进主菜单 |
| `Water/ForceSetting.cs` | `OnEnable` / `OnDisable` 改 `protected override` 并调 `base`(先 `InvokeRepeating` 再 base) | 原来 `private` 静默覆盖基类 → `Game.Loop` 的 Register/Unregister **永不发生**;顺带消掉 CS0114 |
| `Clouds/VolkenClouds.cs` | 新增 `FillActiveLayers(List<>)`;判据收敛成一个 `IsActiveLayer` | 渲染 / 反射路径每帧各取一次,不能各自 LINQ + 临时 List |
| `Clouds/CloudRenderer.cs` | `OnRenderImage` / `BuildViews` 改用复用缓冲,去 `Where().ToList()` 与 `Select().Where().ToList()`;删 `using System.Linq` | 每相机每帧 3 处分配(2 个 List + 枚举器);没有 `System.Linq` 也让下一个人写不成每帧 LINQ |
| `Clouds/CloudRenderer.cs` | `TryResolveFarDepthSource`:另一处 `FindObjectsOfType<Camera>()` → `Camera.allCameras`;解析失败时把重试压到 1 s 一次 | 该方法由 `OnRenderImage` **每帧**调用:远深度源解析不出来(该相机没有可用的远深度相机)时,原本每帧都在 `GetComponentsInChildren` + 全场景找相机 |
| `Clouds/CloudReflectionRenderer.cs` | 同上(静态复用缓冲) | 水面反射云每次反射都调 |

## 3. 未做 / 剩余项

- **`LightningBolt` 分叉合批**(最多 549 个 `LineRenderer` + 各自 `Update`/`LateUpdate`)—— 收益最高、风险也最高,按 [性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) §3.2 继续挂起。
- **`VolkenWeather.UpdateCameraMetrics` 的按需门控**(`IsActive == false` 时也在跑完整 craft 链 + 三角函数)—— 要保留"面板打开时的实时读数"就得先有"面板可见"这个信号,现在没有;见审计 §3.6。
- **`CloudRenderer.OnRenderImage` 里每层 `new RenderBuffer[3]`** —— 每帧一个 3 元素数组,量小;没动是因为改法要在 GPU 路径上验证(不能离线判定 `Graphics.SetRenderTarget` 是否保留该数组引用)。
- **`ProfilerController.Update`**(常驻,每帧只读 `ModSettings.ShowProfiler` 做双向同步)与 **`BootstrapRunner`**(30 帧后自毁)—— 量可忽略,**刻意没动**。
- **死代码**:`Clouds/DepthCapture.cs`(全工程零 `AddComponent` / `Init` 调用)、`PlanetRing/PlanetRingsZWriteFix.cs` 与 `HarmonyPatches/PlanetRingsShaderPatch.cs`(`Postfix` 首行 `return` 且 `Apply` 在 `Mod.cs` 里被注释)。**只登记未删** —— 见 [索引 §四之三](README.md#四之三待办清单backlog)。

## 4. 验收判据

- 编译:`dotnet build Volken.csproj` = 0 错误;警告只剩 `PlanetRingsZWriteFix` 的 2 条 CS0162(死代码)+ 1 条既有引用冲突。
- 真机 / 编辑器(未做):
  1. 主菜单 / 设计器 / 行星工坊 / 过场里,Profiler 不再出现 `WeatherTicker.Update`、`RainAudio.Update`、`LightningModule.Update`、`VolkenUserInterface.Update`。
  2. 进飞行:云正常、面板正常、`ExtraCameraClouds` 仍自动挂载(1 s 内)、雨开关仍生效(开了 `ExtraCameraRain` 时额外相机也挂雨,见 [附加相机雨](extra-camera-rain-2026-10-04.md));PIP / 额外相机的远深度源仍在 1 s 内解析到(解析不到时不再每帧重试)。
  3. **离场雨声仍淡出**(不是硬切),淡出后不再有每帧开销;回场重新淡入。
  4. 开雷电飞行一次 → 回菜单:无残留闪电、**也无残留雷声**(`SetActive(false)` 已一并停掉雷声通道),`LightningModule.Update` 停表;飞行中在面板关掉雷电时,不该再听到已排程的远雷。
  5. 版本落后时:更新提醒仍只在**进主菜单**后弹(最多晚 0.5 s),飞行 / 设计场景不打断。
