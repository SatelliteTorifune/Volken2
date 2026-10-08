# 雨视觉开关:换场景后无法再打开(2026-10-02)

> 状态:✅ 已归档(实现已落地;真机回归未确认)
> 2026-10-08 核对:已对照当前源码确认实现存在;历史编译结果不等于本次复验。待验收见 §2、§4 及 [待办索引](../README.md#四之三待办清单backlog)。
> 日期:2026-10-02(§4 = 2026-10-04)
> 关联:[雨计划](../sp2-rain-particledomain-port-2026-09-28.md)(雨子系统唯一事实源)/ [解耦](weather-cloud-decoupling-2026-10-01.md) §3.3 C2(挂雨该归谁)/ [场景门控](monobehaviour-scene-gate-2026-10-02.md) §1(挂在相机上的组件随 Flight 场景卸载)
> 定位:诊断并修复"雨视觉开关异常、换场景后打开无效";范围只有**雨视觉**,雷(视觉 + 音效)与雨声本来就好 —— 这正是本诊断的旁证,见 §0。

---

## 0. 结论先行

两个缺陷叠加,症状 = "面板开关像是坏的 / 换场景后再也打不开雨":

1. **根因:资产就绪标志是 `static`,资产却在实例上。** compute / shader / material / mesh / buffer 全是 `RainParticles` 的**实例**字段,而门控它的 `AssetsReady` 曾是 `static`。雨挂在场景相机上,换场景 = 组件销毁 + 新相机上新建实例;新实例的 `EnsureAssets()` 看到 static 已为 true **直接 return** → `_compute` 永远 null → `OnPreCull` 每 5 s 打印一次 `enabled but NOT READY`,面板把开关拨到 ON 只改了静态 `Enabled`(雨声门控读同一个静态字段 → **声音照响**),视觉则永远不出现。
2. **加剧项:换场景后没有任何东西会重挂雨。** 2026-10-01 解耦删掉了 `VolkenMod.OnSceneLoaded` 里两处 `RainParticles.AttachToCurrentView()`,计划中的替代(解耦 §3.3 C2「改由 `VolkenWeather` 在 `PlanetConfigLoaded` 里挂雨」)**从未落地**(全仓库 `PlanetConfigLoaded` 只有声明与 `Invoke`,零订阅者)。于是全工程只剩"玩家拨面板开关"这一个挂载点:换场景后即使预设里 `rain.enabled=true`(开关显示 ON),也没有任何代码把雨挂到新相机上。

**日志证据**(`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`,本次约 2800 行、三次进飞行场景):

| 行 | 内容 |
|---|---|
| 767 / 769 / 776 | 第一次挂载:先是 `RainParticles assets ready` + `buffers created` + `SELFCHECK`,再 `attached to camera 'NearCamera'` |
| 1910 | 第二次进飞行场景后:`RainParticles: attached to camera 'NearCamera'`(新实例) |
| 1914 | 紧接着:`RainParticles: enabled but NOT READY — cam=True compute=False positions=False args=False` |

第二次、第三次挂载**再也没有出现过** `assets ready` / `buffers created` / `SELFCHECK` → 唯一解释就是资产加载被 static 标志挡在门外。同一份日志里雨声 `gate=True … master=0.50`、雷的 bolt / thunder 全程正常,与"只有雨视觉死掉"完全吻合。

> 判别细节:第二次挂载**也没有** `assets missing` / `加载异常` 行 —— 说明 `EnsureAssets` 是**根本没执行**(stale static 早退),不是"执行了但加载失败"。这两者的修法完全不同,日志能把它们分开。

---

## 1. 修法(2026-10-02)

| 文件 | 改法 | 为什么 |
|---|---|---|
| `Weather/Rain/RainParticles.cs` | `static bool AssetsReady` → **实例字段 `_assetsReady`**;`EnsureAssets` / `EnsureBuffers` / `Awake` 三处门控改读它;`AssetsStatus` 保留为诊断字符串 | 资产是实例资源 → 就绪标志必须每实例一份;static 会让换场景后的新实例跳过加载(本 bug) |
| `Weather/Rain/RainParticles.cs` | 新增 `SyncToCurrentView()` + `_current` 实例缓存(挂载时写入、`OnDestroy` 清空)+ `ResolveViewCamera()`(把原先 3 份"游戏近相机 → `Camera.main` 回退"收敛成 1 份) | 换场景后按配置自动重挂;命中缓存时开销 = 一次静态引用判空,未挂上时 0.5 s 限流重试(相机可能还没建好) |
| `Weather/VolkenWeather.cs` | `Tick` 里调 `RainParticles.SyncToCurrentView()`(在 `IsActive` 早退**之前**:雨有自己的 `rain.enabled`,不跟随天气总开关) | 复用已有的常驻每帧门控(`WeatherTicker` 只在飞行场景跑),**不新增 `SceneLoaded` 订阅者**(解耦决策:行星生命周期只归 `VolkenClouds`) |
| `Weather/Rain/RainParticles.cs` | `OnPreCull` 的 NOT READY 分支由"只打日志"改成**限次重试**(`AssetMaxRetries=6`、间隔 5 s,`ApplyConfig` 里也补了 `EnsureAssets`) | 原来的注释写"留到 NOT READY 心跳里等",但那段代码从不重试 → 资产晚到/一次失败 = 本实例永久无雨 |

配套:`SyncToCurrentView` 在 `rain.enabled == false` 时把静态 `Enabled` 显式清零,避免上一场景残留的 ON 被雨声门控读到。

---

## 2. 验收判据

- 编译:`dotnet build Volken.csproj` = 0 错误,警告仍是既有 3 条(2 条 `PlanetRingsZWriteFix` 的 CS0162 + 1 条引用冲突)。
- 真机 / 编辑器(未做):
  1. 场景 A 开雨 → 退到菜单 / 设计器 → 再进飞行:**面板开关保持 ON 且雨自己出现**(不需要先关再开);日志里**每次挂载**都能看到 `assets ready` + `buffers created`,全程没有 `NOT READY`。
  2. 把预设 **XML** 里 `rain.enabled` 改成 false 再进场景:无雨、`RainParticles.Enabled=false`、无 `NOT READY` 心跳(验证残留开关被清掉,且以文件为准)。
  3. 雨声与雷的行为**逐条不变**(它们本来就好,本次不应有任何观感差异)。

---

## 3. 配置语义:**XML 是唯一权威来源**(【决策:2026-10-02,用户】)

**跨场景读取数据的唯一权威来源 = `UserData/VolkenWeatherConfig/{行星}/{预设}.xml`,设计如此。** 由此推出的三条契约:

1. 面板里**未点"保存"**的改动**不跨场景保留**:真正跨过一次非飞行场景后,`ApplyPreset` 重读 XML,面板显示与运行时参数一起回到文件值。开关"自己变回文件里的值"是**预期行为,不是缺陷**。
2. 换场景后雨的开关状态必须以**重读后的 `Config`** 为准 —— 这正是本次 `SyncToCurrentView` 按 `Config.rain.enabled` 决定挂 / 不挂(而不是去记住"上一场景是开着的")的原因。
3. 因此 `ApplyPlanet` 的早退判据 `CurrentPlanet == planetName && IsActive` **不要**为了"保留未保存的面板改动"改成缓存或回写 —— 那会与第 1 条直接冲突。

**Flight → Flight 重载(快速读档 / 回退发射)不重读 XML —— 属正常,不处理**(【决策:2026-10-02,用户】):该路径不经过非飞行场景,`ApplyPlanet` 的同行星早退(`CurrentPlanet == planetName && IsActive`)保留内存状态(含未保存改动)。这是可接受的行为,不要为它加"场景加载信号",也不要改早退判据。

**另一条备注**:`Weather/Rain/RainParticles.cs` 的 `AssetsStatus` 仍是 static 诊断字符串,多实例时只反映"最近一次尝试",仅用于日志、不参与门控 —— **不要把它当就绪判据**(那正是本 bug 的形态)。

---

## 4. 回归:雨漏进设计器(2026-10-04)

§1 的 `SyncToCurrentView()` 每飞行帧重挂雨,**只按"相机是谁"决定挂/不挂,从不判场景** → 切到设计器后雨挂到了**设计器的相机**上,整个设计器会话都在下雨。

**根因 = 场景切换里有一条"事件还没到、`Game.InFlightScene` 已变 false"的窗口**(游戏源码 `Scenes/SceneManager.cs` 的 `LoadSceneCoroutine`):

| 步 | 位置 | 状态 |
|---|---|---|
| 1 | 开头 `UpdateCurrentSceneInfo("Transition")` | `Game.InFlightScene` **当场变 false** |
| 2 | `UnloadSceneAsync(上一个场景)` | 飞行相机与其上的 `RainParticles` 一起销毁 → `_current` 变 null |
| 3 | 加载新场景 → `yield return null` | 这一帧 `Camera.main` 已是**新场景**的相机 |
| 4 | 才 `OnSceneLoaded(sceneName)` | mod 这时才知道要关天气 ticker(收到 `PlanetChanged(InFlight=false)`) |

**第 2~4 步之间那一帧 ticker 还开着**(关它的通知在第 4 步),于是 `Tick → SyncToCurrentView` 的 `ResolveViewCamera()` 一路回退到 `Camera.main` = 刚加载好的设计器相机 → 挂载成功;等第 4 步到达时组件已经挂上,而 `OnPreCull` 没有任何场景门控 → 雨照画不误。

**修法 = 场景判据放在挂载与绘制两处**(而不是只挂在事件链上):

| 文件 | 改法 |
|---|---|
| `Weather/Rain/RainParticles.cs` | 新增 `IsGameInFlightScene()`(活体读 `Game.InFlightScene`;取不到 `Game` = 编辑器预览台 → 按"不在飞行");`SyncToCurrentView()` / `Attach(cam)` 开头**拒绝**在非飞行场景挂载;`OnPreCull` 开头非飞行场景**直接 `Destroy(this)` 并 return** —— 自毁是必需的:留着它 `_current` 非空,回到飞行场景就再也挂不上(`OnDestroy` 会清 `_current`) |
| `Weather/VolkenWeather.cs` | `WeatherTicker.Update` 加活体兜底:`Game.Instance == null \|\| !Game.InFlightScene` → return(写法同 `RainAudio.Update`)—— 场景门控靠 `SceneLoaded` 事件链,而别家 mod 的处理器抛异常会静默跳过后续订阅者(本仓库有同源事故) |

诊断:门控命中会打一行 `RainParticles: 场景门控拦下(...) —— 非飞行场景(scene=...)`(5 s 节流);真机上出现它 = 确实有"想在别的场景挂 / 画雨"的调用。

> 雷侧不受影响:`LightningModule.SetActive(false)` 已 `StopCoroutine` + `DestroyAll()`(在播的闪电一起清),没有残留物被带进下一个场景。编辑器预览台(`StandaloneMode=true`)全程短路这些门控。

**验收判据(真机)**:

1. 飞行中开雨 → 按「设计器」:设计器里**没有雨**(日志有 1 行「场景门控拦下(SyncToCurrentView)」,且新场景**不应**出现 `attached to camera`);回飞行场景后雨自己回来(仍以 XML 为准)。
2. 主菜单、行星工坊同样无雨;`[VolkenDiag]RainParticles:` 诊断行在非飞行场景**不再增长**。
3. 编辑器预览台照旧能画雨(独立模式,门控短路)。
