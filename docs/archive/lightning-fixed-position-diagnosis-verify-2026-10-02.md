# 固定位置"射灯"闪电 —— 根因假设独立复核(2026-10-02)

> 状态:✅ 已归档(最终根因与修复见 §5;真机回归未确认)
> 2026-10-08 核对:当前 `LightningBolt.SpawnSplit` 已设置 `sr.SetPosition(0, from)`。§0~§4 是取得截图之前的假设复核,其中 OPEN 排序已被 §5 取代。
> 日期:2026-10-02
> 关联:[雷声真实化](thunder-realism-2026-09-28.md) §7 §8(**已修**,本次不重复报告)/ [雨雷性能审计](../proposals/rain-lightning-perf-audit-2026-10-02.md) §3.4 / [AGENT_CONTEXT](../AGENT_CONTEXT.md) §3「坐标原点重置(浮动原点)」
> 定位:保留假设复核与随后定位修复的证据链。所有结论都带 `文件:行号` 或日志原文;日志是**不可信运行时数据,只当证据**。

---

## 0. 结论先行

**被复核的假设**(`LightningBolt` 全在世界系建视觉状态、无重定位防护 → 换帧后坐标失效 → 留下"固定在世界某处的亮光/射灯"):**REFUTED,置信度 ~0.70**。

三条独立理由(详见 §2):

1. **假设描述的"残留物"与代码的相位结构不符**:点光源与闪光球**只在 Flash 相存在约 0.24 s**,Flash 结束即被关掉(`LightningBolt.cs:447-459`);Fade 相只剩一条**正在淡出**的线段(`:465-468` + `StepFade`)。因此"一道亮着的灯 + 闪光球挂 3.5 s"不成立 —— 非暂停状态下最大的残留是 **~0.35 s 的渐隐线**(`HardLifetime=3.5f` 只是保险上限,`LightningBolt.cs:43`)。
2. **日志里真实(非零)重定位太稀有,且量级互不相同**:293 条 `frame recentered` 行中仅 3 条非零 —— 357.9 m / 782493.5 m / 1.6 m(后者低于 RainParticles 自己 100 m 判据)。假设能解释"某处剩下一团光",**不能解释"同一个固定位置反复出现"**:每次残留物的位移量都不一样(0 / 1.6 / 357.9 / 782493.5 m),残留物会出现在**不同**的地方,而不是一处。
3. **更贴合症状的、完全不需要重定位的候选仍未被排除**:落点闪光球 = 加色 + **`ZTest Always`(穿地形)** + 半径可达 1349 m、屏幕角半径中位数 **10.4°**(见 §2 Q4),而 `_flashPlane` **从不设置朝向**(`:407-408` 只有 position + localScale,对比点光源有 `LookAt` `:400`)→ 每次闪光的亮核都固定落在世界 -X;对停住不动的观测者,这些大圆斑总是出现在同一片屏幕区域、同样大小、按落雷节奏反复出现 —— 这是我认为比"重定位"更可能的解释,标记为 **OPEN**。

**对 Lead 已落地修复的态度(工作副本未提交改动)**:`LightningBolt.FrameIndex/DestroyStale`(`:51-71`、`:119-120`、`:598-603`)+ `LightningModule.BeginFrameGuard`(`:219-253`)是**廉价且判据经日志验证**的纵深防御(§4),**值得保留**;但**不应据此在文档里记成"根因已定位并修复"**,因为本复核没有拿到"重定位 → 症状"的直接证据。

---

## 1. 复核对象与读到的修订(moving target)

| 文件 | 读到的修订 | 关键事实 |
|---|---|---|
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs` | **686 行工作副本**(含 Lead 未提交的 `FrameIndex` 守卫) | 我最初读到的 649 行版本**没有**守卫;`git diff` 显示守卫是工作副本相对 HEAD 的新增 |
| `Assets/Scripts/Volken/Weather/Lightning/LightningModule.cs` | **630 行工作副本**(新增 `BeginFrameGuard`) | 日志期构建**没有**任何 `frame recentered (jump=…)` 行 → 与"当时无守卫"一致 |

日志路径:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(≈2800 行,91 道雷,日志自带 `nowTime=` 时间戳从 209.2 s 到 436.3 s,即雷暴窗口 ≈ **227 s**)。

---

## 2. 逐题证据

### Q1 有没有任何重定位 / 浮动原点防护? → **CONFIRMED(无防护);但"事件不触发"的旧注释是错的**

- `LightningBolt.cs` 全文件:`Recenter|ReferenceFrame|positionDelta` = **0 命中**;`LightningModule.cs` 的 `ReferenceFrame` 只有两处、用途是**取行星中心算径向**,与重定位无关(`:582-584` `GetRadialUp`、`:600-602` `GetGroundPosition`)。日志交叉验证:同一份日志里 `CloudRenderer` 与 `RainParticles` 都打了 recenter 行,`LightningModule` **一行都没有** → 日志期构建确实无防护。
- 对比例的:`CloudRenderer.cs:85` 订阅事件清历史;`RainParticles.cs:592` 订阅 + `:611` `DetectRecenterJump` 兜底。
-  **纠正一条被本假设引用的证据**:`RainParticles.cs:590` 注释写"实测本 mod 环境下该事件**不触发**,兜底才是主力" —— 本日志直接反证:`[VolkenDiag]RainParticles RECENTER(event): delta=(167732.1,668407.3,-370666.5) |d|=782493.5m` 等 **3 条 event 行**确实触发了(另有 290 条 `Δ=0.0m` 的零位移事件)。守卫**不必**绕开订阅;但事件里有大量零位移噪声,所以用"跳变判据"过滤仍然是合理选择。

### Q2 视觉元素是不是"铸造时写一次、之后只播动画"的世界坐标? → **CONFIRMED**

| 元素 | 证据(工作副本行号) |
|---|---|
| 主干 `LineRenderer` | `_line.useWorldSpace = true`(`:180`);根物体摆在云里 `transform.position = origin`(`:320`);首两帧点 = 根位置(`:347-349`);逐段 `_line.SetPosition(_arcIndex, next)`(世界点,`:373`);末点钉 `SetPosition(arcs - 1, Target)`(`:389`) |
| 落点闪光球 | `flashDiameter = Mathf.Clamp(totalDist * 0.1f, 20f, 4000f)`(`:404`);`_flashPlane.position = Target; _flashPlane.localScale = Vector3.one * flashDiameter;`(`:407-408`);**无任何旋转/朝向写入** |
| 落点点光源 | `_light.type = LightType.Point; intensity = lightIntensity(默认 8); range = lightRange(默认 8000)`(`:209-211`,配置 `VolkenWeatherConfig.cs:181,183`);`_light.transform.position = Target`(`:399`) |

→ 这些值**只在 Grow/Flash 起点写一次**,`Update` 里没有任何逐帧重算(`:587-641`)。**假设的机制部分完全成立。**

### Q3 时间窗量化(修正 3.5 s 的说法)→ **假设的量级被高估 ~5×**

由常量与相位逻辑算(默认 `arcs=20`,`VolkenWeatherConfig.cs:169`):

| 相 | 时长 | 依据 |
|---|---|---|
| Grow | `(arcs-1) × Random(0.001,0.005)` ≈ **0.06 s** | `:354`、`:359-362` |
| Flash | 4 亮 × (亮 25~35ms + 灭 25~35ms) ≈ **0.24 s** | `:412-424` |
| Fade | `Lerp(50→≤1, 10·dt)` ≈ **0.35 s**(dt=0.02) | `:465-468` + `StepFade` |
| **合计视觉寿命** | **≈ 0.65 s** | —— |
| 其中"点光源 + 闪光球" | **≈ 0.24 s** | `:447-459`(Flash 结束即 `_flashMat=0`、`_light.enabled=false`) |

日志侧上界:每个 `bolt #N` 行到它自己的 `thunder [...] dist=` 行(即 `OnBoltLanded`,`:462`)之间**最多跨 1 个 `RainParticles: calls=` 心跳**(心跳频率 = 2 条/秒,见[审计](../proposals/rain-lightning-perf-audit-2026-10-02.md) §3.5),`calls` 差 32~46 帧 → 与 ~0.3 s 的设计值相容,且排除"亮着 3.5 s"。

**发生率**:91 道 × 0.65 s ≈ 59 s(占窗口 26%);其中亮光相 91 × 0.24 s ≈ 22 s(10%)。真实非零重定位 **2 次**(357.9 / 782493.5 m)。若独立均匀 → 期望"亮光相 ∩ 重定位" ≈ 2 × 22/227 ≈ **0.2 次**。所以假设预测的是**每 ~5 次会话偶发一次**,与用户描述的"反复出现"量级不符 —— 这也说明**单靠"日志里没看到重叠"并不足以证伪**(只有 2 个样本),证伪主要靠 §0 的第 1、2 条。

### Q4 备选攻击

| 备选 | 判定 | 证据 |
|---|---|---|
| **反复落在同一处** | **REFUTED** | 91 个落点两两最近 **204 m**(#11 vs #21),相邻落点距离中位数 **4012 m**(最小 497 m);落点到帧原点的距离 439~8340 m → 没有"固定落点" |
| **闪光球:加色 + `ZTest Always` + `Clamp(totalDist*0.1, 20, 4000)`** | **OPEN(我认为最可疑)** | `LightningFlash.shader:26-28`(`Blend SrcAlpha One` / `ZWrite Off` / `ZTest Always`)/ `:63-75`;网格是**单位半径**球(`LightningBolt.cs:273-316`,顶点幅度 ≤1;`flashDiameter` 实际是半径);本日志实测半径 **175~1349 m**、相机↔落点 450~8394 m、**相机从未进入球内**(最坏余量 172 m)、屏幕角半径 **中位 10.4°、最大 39.8°**;`_flashPlane` 不设朝向 → UV 亮核(u=v=0.5 → 局部 -X)方向恒定 |
| **落点点光源 `Point / 8 / range 8000` 读成"地面光锥"** | **REFUTED 为主因,保留为放大项** | 只亮 0.24 s 且随落点移动,无法产生"固定";但 8 km 半径会把地面/机体的泛光铺得极大([审计](../proposals/rain-lightning-perf-audit-2026-10-02.md) §3.4 已质疑)。另:`Clouds/` 下 grep `_Light` / `LightDir` / `ForwardBase` = **0 命中** → 体积云是 image effect,**不消费实时光源**,点光源**不会**把云照成光锥 |
| **NaN/Inf 端点** | **REFUTED** | 91 个端点全为有限值且自洽(落点半径落在"行星地面 ± `targetRange=3000`"内);`VolkenWeather.cs:401` 缺帧时是 `return`(**保留旧值**,不是 NaN);`GetRadialUp/GetGroundPosition` 都有显式兜底(`LightningModule.cs:582-584`、`:600-602`);日志中 **0 条** `LightningBolt` 清理/异常行 |
| **我补的一条:暂停冻结** | **OPEN(与重定位无关,但同样能产生"固定不动的亮光/球")** | 动画由 `Time.deltaTime` 驱动(`:612-627`),计时用 `GamePause.Now`(`GamePause.cs:14`,**排除暂停时长**)→ 暂停中处于 Flash 相的雷会**原样冻结**(点光源 + 加色球全亮)且**永不触发** `HardLifetime`(那是"非暂停秒")。日志里唯一一次真实重定位恰好发生在恢复暂停之后:`[VolkenDiag]RainAudio: 游戏恢复 → 雨声继续` → `[Volken]Volken:LightningModule thunder resumed (game unpaused)` → `[Volken]Volken:CloudRenderer frame recentered Δ=357.9m — TSS history cleared` |

**关键反证(最强的一条"against 假设")**:唯一一道**确实跨过真实重定位**的雷是 bolt #51 —— `已传送到目标位置`(游戏传送提示)与 `frame recentered Δ=782493.5m` 之前,它的 `thunder [far] dist=7128m … clip=volkenThrunder-far-5` 行已经打印 → 传送发生时它**已在 Fade 相**(灯与球都已关,只剩渐隐线),而且被 782 km 的平移甩到视野外;两次 `storm loop stopped (cleared 0 in-flight bolt(s))` 也都报 0 道在播。

### Q5 裁决

**REFUTED(作为"最可能根因"),置信度 ~0.70。**
- **最强 for(支持假设)**:`LightningBolt` 确实**零防护**且在**铸造时**把 line/light/flash 全部钉进世界坐标(`:180`, `:320`, `:399`, `:407-408`),日志也证明重定位**真的会发生**(`Δ=357.9m`、`Δ=782493.5m`)—— 机制无争议。
- **最强 against**:唯一一次"雷与真实重定位同帧"的实例中,那道雷**处在 Fade 相**(亮光相早已结束),而 `Flash` 相的灯/球总寿命只有 ~0.24 s、`HardLifetime=3.5s` 只是上限;再叠加 3 次非零重定位的位移量互不相同(0/1.6/357.9/782493.5 m),该机制只能产生"某处偶发一团错位的光",**无法产生"同一固定位置反复出现的射灯"**。
- **我无法排除的替代**:①闪光球本身(§2 Q4,OPEN,且我认为更像);②暂停冻结(OPEN);③本日志可能不是用户看到症状的那一次会话 —— 若某次会话在时间加速/连续传送下**每帧重定位**,样本与本次完全不同。

---

## 3. 唯一决定性实验

工作副本的守卫已经落地,所以最省事的判别就是**带着守卫复现症状**;三组对照,同位置、同朝向、连续手动落雷 20 次:

1. **(a) 基准**:不传送、不暂停,连续落雷 → 若"射灯"照样出现,且日志里 `LightningBolt frame recentered mid-bolt`(`LightningBolt.cs:600`)与 `LightningModule frame recentered (jump=…)`(`:249-251`)**0 命中** → **假设被证伪**,把预算转到闪光球/点光源。
2. **(b) 重定位注入**:在落雷后 ~0.1~0.2 s(**Flash 相内**)触发传送 → 若此时才出现残留亮光,而 (a) 组不出现 → **假设成立**。
3. **(c) 暂停组**:在落雷后立刻暂停并保持 ≥10 s → 检验 §2 的"暂停冻结"路径(与重定位无关的第三条路)。

判据已经在日志里被验证过一次,可以直接复用:`RainParticles RECENTER(jump): |d|=358.0m threshold=100m dt=0.0410 craftSpd=0.0 → TranslateFixed` —— 说明"飞行器帧位置跳变 > max(100m, 自身运动×3+30m)"这条判据在本环境**确实能抓到真实重定位**(与 [AGENT_CONTEXT](../AGENT_CONTEXT.md) §3 的 >5000 m / >1000 m/s / 时间加速 / 表面锁定切换四种触发方式并列)。

---

## 4. 对已落地守卫的两条备注(不影响本次裁决)

- 判据与 `RainParticles.DetectRecenterJump` 同式,故**能被日志中真实发生的 357.9 m 重定位触发**,这一点是本修复最扎实的地方。
- 两处**假阳性**只会多杀一道 0.65 s 的雷、代价可忽略,但值得知道:`BeginFrameGuard` 在 `cr == null` 时**提前 return 且不重置基准**(`LightningModule.cs:226`)→ 飞行器消失后又在别处出现 = 一次假跳变;参考系类型切换(表面锁定)本身也会让 `FramePosition` 跳变(`:227`)。
- 覆盖面是够的:守卫只在 `LightningModule.Update` 里跑,而该 `Update` 已被雷电开关/场景门控(`SetActive` 首行 `enabled = active`,`LightningModule.cs:150`,见[场景门控](monobehaviour-scene-gate-2026-10-02.md) §2)→ 不跑雷暴时本就没有在播的雷,不存在"停表期间漏判"的问题。

---

## 5. 后续:根因已由真机截图定位(2026-10-02,推翻本复核的"OPEN"项排序)

用户随后提供了症状截图:**一束约 85 条线全部汇聚于**一个点、呈**扇形**射出的长直线**,而非"一团圆形亮斑"。这个形状把本复核 §2 Q4 里排第一的候选(**落点闪光球**,圆形)直接排除,并指到第三类此前**未被列出**的可能:**分叉线的几何本身错了**。

**根因**:`LightningBolt.SpawnSplit` 里 `sr.positionCount = splitPoints + 1`(共 8 个点),但循环只写 `i = 1..7`,**第 0 个点从未赋值** → 保持 Unity 的默认 `(0,0,0)`;而该 `LineRenderer` 是 `useWorldSpace = true`,于是 `(0,0,0)` = **世界原点**。SR2 的浮动原点又把原点重定位到飞船处(本复核 §1 已确认该重定位真实发生),所以 `(arcs-3)×(splits+1)`(默认 **85**)条分叉**全部从观测者身上**拉向云中的各个节点 —— 正是截图里的"射灯"扇形。

**修法**:补 `sr.SetPosition(0, from);`(`LightningBolt.cs:595`)。

**本复核的教训(值得保留)**:§2 Q3 用"亮光相只有 0.24 s"论证残留不会持久、§0 用"位移量互不相同"论证不是固定位置 —— 两条推理都**成立但对错了对象**:症状不是"残留物位置固定",而是**每条分叉都共享同一个错误的固定端点(世界原点)**;而那 0.65 s × 91 道雷的窗口足以被截图/肉眼反复看到。**"固定位置"不一定指残留物不动,也可能指几何里有一个恒定不变的端点。** 另:本复核列出的两个 OPEN 项(闪光球、暂停冻结)确实都是真缺陷(已分别保留待评估 / 已修),但都不是本症状的成因。

**仍未做**:真机验收(Unity 重编程序集后重复落雷,扇形应消失,只剩细碎分枝)。
