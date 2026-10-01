# Volken 雷声真实化可行性分析(2026-09-28)

> 状态:⏸ **已转 proposals(暂停)** —— 代码 / 配置 / UI / 文案已落地(C# 0 错误),**Unity 侧打包与真机验收未做**(未做完雷声仍静音);暂不排期。**母计划**:[`sp2-weather-port-2026-09-27.md`](sp2-weather-port-2026-09-27.md)。

需求(用户原话拆成三条):①确定"雷声来源"与 **craft** 之间的距离;②根据 craft 当前处的**声速**确定延迟;③根据某个**阈值**去 `Assets/Scripts/Volken/Weather/Audio` 里选 near 或 far 播放。
结论:**三条都可行,游戏 API 已现成提供声速。**但**动手前必须先修一条已有的致命故障** —— 雷声资源在 bundle 里指向的是**已被删除的 5 个 .ogg**。
**实施状态(2026-09-28)**:§5.2 的代码 / 配置 / UI / 文案**已全部落地**,`dotnet build Volken.csproj` = **0 错误**(仅仓库原有 5 条既有警告)。**§5.3 的 Unity 侧打包(导入 WAV + 设导入设置 + 改 `_otherAssets` + 重建)尚未做** —— 完成前雷声仍是**静音**的。素材已由用户补齐为 **9 条**(`near-1~4` / `far-1~5`),`far-1` 与 `far-2` 的重复问题已解决(§3.1 的提醒作废)。

---

## 0. ⛔ 前置故障:现在的雷声在游戏里完全不会响

| 事实 | 证据 |
|---|---|
| 代码读的是旧 OGG 路径 | `LightningModule.cs:35` `ThunderPathFormat = ".../enviro_thunder_{0}.ogg"`,`ThunderClipCount = 5` |
| 那 5 个 OGG **已被删除** | `git status` 显示 ` D Assets/Scripts/Volken/Weather/Audio/enviro_thunder_1~5.ogg`(含 `.meta`) |
| 新素材是 8 个 WAV(当时),**从未被导入过 Unity** | 目录里只有裸 `.wav`,**没有任何 `.meta`** → Unity 还没为它们生成 GUID |
| 打包清单里仍是 5 个已删的 OGG | `ModAssetBundles/StandaloneWindows64/volken.manifest` 的 `Assets:` 段含 `enviro_thunder_1~5.ogg` |
| 权威清单 `_otherAssets` 里那 5 个 GUID **全部悬空** | `Assets/ModData.asset` 的 `f84ba73f…`/`28e67960…`/`b294e2e8…`/`26002a38…`/`fe540ed2…` 在 `Assets/` 下**已无任何 `.meta` 引用**(逐条验过) |

**后果**:`Mod.LoadVolkenAsset<AudioClip>(..., required:false)` 必然返回 null → `_thunderClips.Count == 0` → `PlayThunder` 直接 return。日志里只会有一句 `loaded 0/5 thunder clips (silent …)`。
**所以"真实化"的第一步不是改延迟,而是把音频真正打进包**:导入 WAV → 生成 `.meta`/GUID → 把新 GUID 加进 `Assets/ModData.asset` 的 `_otherAssets`、**删掉**那 5 条悬空 GUID → 重新构建 mod → 用 dev 命令 `volkenAssets` 复核台账全是 `ok`。

---

## 1. 需求① 雷声来源 ↔ craft 的距离:**可行**

### 1.1 现在的代码算错了

`LightningModule.cs:252-259`:

```csharp
bolt.OnBoltLanded = distance => { ... PlayThunder(landing, volume, delay); };
```

`LightningBolt.cs:323` 给回来的是:

```csharp
OnBoltLanded?.Invoke(Vector3.Distance(transform.position, Target));
```

`transform.position` = **bolt 的起点(云里)**,`Target` = 落点。传出去的是 **bolt 自身长度(云底到地面的那几公里)**,**不是到玩家的距离**!闪电落在离你 300 m 还是 3 km,这个数几乎一样(取决于 `spawnRange` 调参)。

### 1.2 正确来源

`CastBolt(Vector3 from, Vector3 to, ...)` 里 `landing` 就是落点,是**局部变量现成的**:

```text
observerPos = craft 位置(ReferenceFrame.PlanetToFramePosition(craftNode.Position))
            ≈ 相机位置(相机挂在 NearCamera 上,与飞船同参考系)
strikeDist  = Vector3.Distance(observerPos, landing)
```

- **不需要改 `LightningBolt` 的签名**。把 `OnBoltLanded(float)` 的语义从"bolt 长度"改成"无关"即可 —— 更干净的做法是**删掉这个回调**,距离在 `LightningModule.CastBolt` 自己算,少一层会算错的间接层(`LightningBolt.cs:57` 注释里写的"落点到相机的距离"本来就是**错的**,要一起改)。
- 取 `craftNode.ReferenceFrame.PlanetToFramePosition(craftNode.Position)` 的模式在 `VolkenWeather.cs:744` 已有先例,拿来即用。

### 1.3 一个会影响真实感的细节:远雷的"声源"不在落点

物理上,雷声是**整条放电通道**(云底↔地面)同时发声,听到的是各路到达时间不同的一串隆隆声。用"到落点的直线距离"当声源距离,对**近雷正确**,对**远雷偏大**:3 km 外一道 2 km 长的闪电,到落点 3 km(≈8.7 s),到通道最近端可能只有 1.3 km(≈3.8 s),真实感知更接近后者(先到的是近端);同理 2.6 MB 的 far 素材时长 13~14 s,本身就"自带"这种扩散感。
**建议**:加一个 `thunderSourceBlend`(0=只用落点距离,1=用 min(落点距离, 云底水平距离)),默认 ~0.5。属于锦上添花,不影响①②③的成立。

---

## 2. 需求② 按 craft 处的声速算延迟:**可行,而且 API 现成**

### 2.1 API 事实(已在反编译源码里核实,不是推测)

| 事实 | 位置 |
|---|---|
| `ICraftFlightData.AtmosphereSample` | `<JNO_CODE>/ModApi/Craft/ICraftFlightData.cs` |
| `AtmosphereSample.SpeedOfSound` → **`float`,单位 m/s** | `<JNO_CODE>/ModApi/Planet/AtmosphereSample.cs:75` |
| 取值口径 = `CalculateSpeedOfSound(MeanSurfaceTemperature, MeanGamma, MeanMassPerMolecule)` | `PlanetAtmosphereData.cs:920` |
| 公式 = `sqrt(γ·1.38e-23·T / m_molecule)` | `PlanetAtmosphereData.cs:548-551` |
| `MeanSurfaceTemperature = (MeanDay + MeanNight) / 2`;**过 0 时 = 0** | `PlanetAtmosphereData.cs:887` + `SampleAltitude():905` 的 `HasPhysicsAtmosphere && altitude < Height` 卫语句 |

取得方式(与 `Water/ForceSetting.cs:29` 同一写法;注意 `AtmosphereSample` 是 **struct**,按值返回,没有空引用问题):

```csharp
var flightData = Game.Instance?.FlightScene?.CraftNode?.CraftScript?.FlightData;
float c = flightData != null ? flightData.AtmosphereSample.SpeedOfSound : 0f;   // 无大气/超大气顶 → 0
```

**`SpeedOfSound` 是按"行星大气成分 + 平均表面温度"算的,不随飞行高度变化**(`CalculateTemperature` 的高度剖面**没有**参与声速;游戏自己算 Mach 数用的就是它 —— `DragPhysics.cs:184` `MachNumber = |v| / speedOfSound`)。所以:这不是"craft 处的声速"的严格值,而是**游戏口径的声速**,与机身阻力/马赫表完全一致,**建议就用它**(出现不一致,如你算 340、HUD 显示 330,会显得是 bug)。若要更严格,可自己用 `PlanetAtmosphereData.CalculateSpeedOfSound(sample.Temperature, data.MeanGamma, data.MeanMassPerMolecule)` 把温度换成**当前高度的温度**,但**不建议**:会与游戏 Mach 数打架,收益只有几 %。

### 2.2 各行星实算(用 `StreamingAssets/CelestialDatabase` 的 XML 参数)

| 行星 | T_surf(K) | meanMassPerMolecule | γ | **声速 m/s** | 3 km 水平距离的延迟 |
|---|---|---|---|---|---|
| **Droo** | (283+293)/2 = 288 | 28.97 | 1.4 | **340** | 地面 8.8 s → 云底高度 9.7 s |
| Cylero | (184+242)/2 = 213 | 43.34 | 1.28 | **233** | 12.9 s → 14.2 s |
| Tydos | 165 | 2.22 | 1.41 | **931** | 3.2 s → 3.6 s |
| 真空/无大气 | — | — | — | **0** → 必须兜底 | 用固定值 |

**延迟实算**(水平 3 km、云底 2 km ASL、`thunderSourceBlend = 0.5`):

| 观测者 AGL | 落点距离 | 云底声程 | 混合后声程 | Droo 延迟 |
|---|---|---|---|---|
| 0 m(地面) | 3.00 km | 3.61 km | **3.00 km** | 8.8 s |
| 1000 m | 3.16 km | 3.16 km | 3.16 km | 9.3 s |
| 2000 m(≈云底) | 3.61 km | 3.00 km | **3.30 km** | 9.7 s |
| 3000 m(云内) | 4.24 km | 3.16 km | 3.70 km | 10.9 s |

结论:云底混合对**地面观测者不缩短**(云底比落点更远),对**云层高度附近的观测者缩短约 0.3 s(8%)**。修正幅度不大但方向正确(远雷该"先到"),且零额外代价 —— 保留,不是关键项。

> ⚠️ 量的**水平/垂直分解必须沿地表法线**(`GetRadialUp`),不能拿 world Y/Z 当"水平" —— 参考系可能被旋转(行星坐标系里 Y 才是"上")。这是本题里最容易踩的一个坑。

→ **303/343 这种硬编码是错的**(Tydos 上差 2.7 倍)。现在的 `SpeedOfSound = 343f`(`LightningModule.cs:37`)要删掉,换成运行时取样 + **`c <= 1 m/s` 时回退 343(或配置值)**。

### 2.3 延迟公式与"过 0"策略

```text
delay = lerp(thunderDelay, strikeDist / c, thunderDistanceAttenuation)   // 现有形状可保留
```

现在 `thunderDistanceAttenuation = 0.6` 默认值会让延迟**只有真实值的 60%**(3 km 处 ≈5.3 s 而非 8.8 s),这正是"不够真实"的一个来源。**真实化 → 默认改成 1.0**,并把该字段在 UI 上说明是"0 = 原版 0.05 s 固定延迟"。曾考虑再加 `thunderMaxDelay`(秒,默认 20)封顶,实测不需要(见 §5.1),故未实现。

### 2.4 ⚠️ 时间加速:唯一的解释器性风险

`LightningModule` 的协程用 `Time.deltaTime` + `WaitForSeconds`,而 `PlayScheduled` 用的是 **`AudioSettings.dspTime`(真实时间,不受 `Time.timeScale`/游戏倍速影响)**。

- 游戏自带的**快进/慢动作**(`TimeManager._fastForward/_slowMotion`)只改 `TimeManager.DeltaTime`(见 `VolkenWeather.cs:613` 的注释),**不改 Unity 的 `Time.timeScale`** → 对协程和音频都**无影响**,可以不管。
- 但闪电在**真实飞行时间**里播完后,若飞船本身很快,等雷声到达时飞船已跑远,声源方向会很怪。**缓解手段**:远雷 `spread = 160°`(见 §4)—— 声源张角大,方位就不那么"指着一个点"。

---

## 3. 需求③ 按阈值选 near / far:**可行,素材实测如下**

### 3.1 实测(48 kHz / 16 bit / **立体声**)

**以下是最初 8 条的实测数据**(包络用脚本抽样算的,可直接复算):

| 文件 | 时长 | 峰值位置 | 0–1 s RMS | 4–8 s RMS |
|---|---|---|---|---|
| `volkenThrunder-near-1.wav` | 3.85 s | 0.28 s | 0.187 | 0.000 |
| `volkenThrunder-near-2.wav` | 8.45 s | 0.62 s | 0.183 | 0.011 |
| `volkenThrunder-near-3.wav` | 16.29 s | 0.86 s | 0.084 | 0.033 |
| `volkenThrunder-near-4.wav` | 5.49 s | 0.24 s | 0.180 | 0.014 |
| `volkenThrunder-far-1.wav` | 13.51 s | 2.68 s | 0.049 | 0.075 |
| `volkenThrunder-far-2.wav` | 13.51 s | 2.68 s | 0.049 | 0.075 | ← **与 far-1 完全相同**
| `volkenThrunder-far-4.wav` | 14.08 s | 0.45 s | 0.163 | 0.083 |
| `volkenThrunder-far-5.wav` | 3.84 s | 0.04 s | 0.259 | 0.000 |

**当时的两条素材问题(现状)**:

1. ~~`far-1` 与 `far-2` 是同一份素材~~ → **✅ 用户已修正**:`far-2` 已换成新素材,并补入了 `far-3`,现为 `near-1~4` + `far-1~5` **共 9 条、MD5 互不相同**。代码按 `near 1..4` / `far 1..5` 直读,与现状一致。
2. **分类不完全干净(仍需真机试听)**:`far-5` 只有 3.84 s 且起始就是峰值(更像近雷),`near-3` 有 16.29 s 的持续隆隆(更像远雷),`far-4` 峰值在 0.45 s 也很靠前。包络只能给线索、不能定论 —— **按文件名分类是可用的默认,但要在游戏里听**;若发现反了,改文件名或调 `thunderNearDistance` 即可,不用改代码。

### 3.2 阈值参数(已定为配置项)

| 候选 | 结论 | 理由 |
|---|---|---|
| `thunderNearDistance`(米) | ✅ **采用,默认 2000** | 当前 `targetRange` 默认 3000 m → 落点大体在 0~3.6 km,阈值落在这个区间中位偏近处,能真的两种都听到;对应延迟约 4~7 s(Droo) |
| 按延迟而非距离判 | ❌ 未采用 | 更"物理直觉",但不同行星声速不同会让实际分界漂移(同样 6 s 在 Droo 是 2 km、在 Tydos 是 5.6 km),反而更难解释 |

**问题:阈值应该跟 `targetRange` 联动吗?** 不联动。`targetRange` 是"落点散布"(视觉),阈值是"听感分类",两者独立更可控 —— 但 UI 上放在一起,方便对照调。

---

## 4. 连带必须改的"真实感杀手" —— ✅ 本轮已一并处理

| 问题 | 原状 | 本轮处理 |
|---|---|---|
| **距离衰减算了两次** | `DistanceVolume()`(手工 `near/d`,地板 0.25)+ AudioSource 3D 对数衰减叠乘 | ✅ 公式改为 `sqrt(near/d)`(线性音量 ↔ 能量 1/d),尺度改成"阈值的一半以内不衰减";**保留**它作为远雷额外软化 —— 直接删掉会让远雷只剩 rolloff 一条曲线,反而不好调 |
| **3D 定位对远雷太"尖"** | `spatialBlend = 1`,`spread = 60°`,固定 | ✅ `ConfigureVoice()` 每次播放按近/远刷新:近 `min = 阈值×0.15`/`spread = 60°`;远 `min = 阈值`/`spread = 160°`;**这条比阈值本身更影响"真实"** |
| **只有一个 AudioSource** | 单个 `_audioSource`,长素材互相打断 | ✅ 改为 **4 通道轮转 + 0.6 s 最小冷却** |
| **立体声素材做 3D 声源** | 9 条全是 2ch | ⏳ 需在 Unity 导入设置里勾 `forceToMono`(代码侧无法解决),见 §5.3 第 2 步 |
| **包体** | 9 条 ≈ 17 MB 裸 PCM(当前整包 5.8 MB) | ⏳ 同上:必须显式设 Vorbis(q≈70)+ `CompressedInMemory`,预计 → 2~3 MB |
| **`PlayScheduled` 与 `preloadAudioData`** | 旧 OGG 的 `.meta` 是 `preloadAudioData: 0` | ⏳ 同上:一律设 `preloadAudioData = 1`,避免"调度到未来时间点、数据还没就绪" |

---

## 5. 落地清单

> **本节 = 实施记录(2026-09-28)**:§5.1 / §5.2 已完成并编译通过;§5.3 待做;§5.4 是真机验收判据。

### 5.1 配置字段(`Core/VolkenWeatherConfig.cs` → `LightningSection`)

| 字段 | 默认 | 语义 | 兼容性 |
|---|---|---|---|
| `thunderDistanceAttenuation` | 0.6 → **1.0** ✅ | 0=固定延迟,1=真实声速延迟 + 距离衰减 | 已有字段,改默认值 |
| `thunderNearDistance` | **2000** ✅ | near/far 分界(米) | **新增** |
| `thunderFallbackSpeedOfSound` | **343** ✅ | 取不到声速时(真空/API 失败)的兜底 m/s | **新增** |
| `thunderSourceBlend` | **0.5** ✅ | 落点距离 vs 云底声程混合(§1.3) | **新增** |
| ~~`thunderMaxDelay`~~ | — | ❌ **实测不需要**:落点在 ≤3.6 km、最慢声速(Cylero 233 m/s)下延迟也只有 ~15 s,加封顶只会引入一个多余的旋钮 | 不新增 |

⚠️ 按仓库兼容约定:新字段初始值必须 = 关闭/恒等,**否则会改变老玩家现有的听感**。上表 `thunderDistanceAttenuation` 的默认值变更**会**改变老玩家行为 —— 这是有意的(需求就是"更真实")。新增字段已同步补 `CopyFrom` + `ClampAll`。

### 5.2 代码改动点(全部已完成 ✅)

1. `LightningModule`:路径模板换成 `volkenThrunder-{near|far}-{i}.wav`(`near` 1..4 / `far` 1..5),拆成 `_nearClips` / `_farClips` 两个列表;
2. `LightningModule.CastBolt()`:删掉 `const SpeedOfSound = 343f` 与"用回调传距离"的做法,改由 `PlayThunderForStrike(落点, 观测者位置, 云底海拔, cfg)` 统管;观测者位置在**回调里现取**(bolt 动画要跑约 0.2 s,相机可能已移动);
3. 新增 `GetSpeedOfSound()`:读 `CraftScript.FlightData.AtmosphereSample.SpeedOfSound`,≤1 m/s 时回退兜底值;
4. `PlayThunderForStrike()`:① 落点距离 → ② 声程混合 → ③ 声速延迟 → ④ 阈值选组 → ⑤ 音量(衰减改 `sqrt` 以免与 3D rolloff 叠乘过度);
5. `PlayThunder()`:`AudioSource` 改 **4 通道轮转 + 0.6 s 最小冷却**;`ConfigureVoice()` 按近/远刷新 `minDistance/maxDistance/spread`(近 60° / 远 160°);每次播放打印 `thunder [near|far] dist=… path=… c=… delay=… vol=… clip=…`;
6. `LightningBolt.OnBoltLanded`:`Action<float>` → **`Action`**,并改正那条写错的注释(它原来说参数是"落点到相机的距离",实际是 bolt 自身长度);
7. `WeatherPanel`:新增 3 个滑块(`ThunderNearDistance` / `ThunderFallbackSpeedOfSound` / `ThunderSourceBlend`),并把 `ThunderDistanceAttenuation` 的默认值同步为 1.0;
8. 三语言 `EN-US` / `RU-RU` / `ZH-CN.xml`:改 2 条已有文案 + 新增 3 条(各 5 行)。

**这次**没有**改 `DistanceVolume` 的距离尺度为"完全删除"**:保留它作为"远雷额外软化"(以阈值为尺度),把公式从 `near/d` 换成 `sqrt(near/d)` —— 原因是 AudioSource 的对数 rolloff 已经压了一轮,再用 `near/d` 会把远雷压到听不见,阈值切换就听不出差别了。若实听仍觉得远雷太轻/太重,调 `thunderVolume` 即可。

### 5.3 打包(必须在 Unity 里做,且分两轮)—— ⏳ 待做

> 这是**唯一还没做、且不做就依然静音**的一步。需要 Unity 编辑器焦点。

1. 焦点切回 Unity → 让它导入 9 个 WAV(此时才生成 `.meta`/GUID);
2. 逐条设导入设置:`forceToMono=1`、`loadType=CompressedInMemory`、`compressionFormat=Vorbis`、`quality≈70`、`preloadAudioData=1`、`3D=1`;
   > **实测现状(2026-09-28,Unity 导入后自动生成的 `.meta`)**:9 条 WAV 都已经是 `loadType: 1`(CompressedInMemory)、`compressionFormat: 1`(**PCM**)、`forceToMono: 1` ✅、`3D: 1` ✅、`preloadAudioData: 0`。需要手工改的是两处:**`compressionFormat` 1 → 0(Vorbis)** 与 **`quality` 1 → 70**、`preloadAudioData` 0 → 1。只改前者即可把 16.1 MB 压到 2~3 MB。(注意 `compressionFormat` 是位标志枚举:0=PCM、1=Vorbis、2=ADPCM;旧 OGG 素材的 `.meta` 里写的就是 1 = Vorbis。)
3. 把 9 个新 GUID 加进 `Assets/ModData.asset` 的 `_otherAssets`,**删掉 5 条悬空的旧 OGG GUID**(`fe540ed2…` / `28e67960…` / `f84ba73f…` / `b294e2e8…` / `26002a38…`,已逐条验过全部悬空);
4. 重新构建 mod;复核 `Temp/ModManifest.xml` 与 `ModAssetBundles/StandaloneWindows64/volken.manifest` 含 9 条新 `volkenThrunder-*.wav` 路径、不含 OGG;
5. 进游戏用 dev 命令 `volkenAssets` 看台账全 `ok`,日志里应出现 `loaded thunder clips: near=4/4 far=5/5 voices=4`(不再是 `SILENT`)。

**包体预估**:9 条裸 PCM ≈ 17 MB(48 kHz/16bit/立体声),当前整包才 5.8 MB。按 Vorbis(q≈70)+ `forceToMono` 导入,预计 → 2~3 MB;若误用 PCM / `DecompressOnLoad`,运行时会就地解成 ~34 MB 内存。**必须显式定导入设置,不能吃默认值**。

### 5.4 验证判据

- 每次雷击的日志应打印 `thunder [near|far] dist=…m path=…m c=…m/s delay=…s vol=… clip=…` —— 一眼能看出①②③都生效;
- 把 `thunderDistanceAttenuation` 拉到 0 → 听感应回到原版 0.05 s(延迟)与恒音量;
- 在 Droo / Tydos 各劈一道:同样的落点距离,**延迟应差约 2.7 倍**(340 vs 931 m/s)—— 这是"按声速"最硬的证据;
- 手动触发多次(间隔 < 素材时长)→ 雷声**不互相打断**;
- 调 `thunderNearDistance` 跨越当前落点距离 → near/far 两条日志与音色**同时**切换。

---

## 6. 结论与剩余待决

**可行性:三条全部成立,已实施完毕(代码层)。** 其中②因为游戏 API 直接给了 `AtmosphereSample.SpeedOfSound`,实现难度比预期低。

**已被用户确认的决策**:

1. 范围 = 代码 + 配置 + UI + 文案(✅ 已做),Unity 侧打包由用户操作(⏳ 待做,见 §5.3);
2. 阈值**做成一个新的雷的配置项** `thunderNearDistance`(✅ 默认 2000 m);
3. `far-1` / `far-2` 重复素材 —— 用户已自行修正(现有 9 条互不相同)。

**仍需真机确认的点**:

1. **素材分类是否符合听感** —— `far-5`(3.84 s、起始即峰值)与 `near-3`(16.29 s 长隆隆)按实测包络与文件名相反,**只有试听才能定论**;若反了,只需在面板上调 `thunderNearDistance` 或交换文件名。
2. 远端 `spread = 160°` 与 `sqrt` 音量曲线是否合适(这两条是"真实感"的主观项,需要耳朵定);
3. 时间加速下闪电与雷声的错位是否可接受(§2.4:JNO 的快进/慢动作只改 `TimeManager.DeltaTime`,**不改** `Time.timeScale`,故对协程与音频均无影响)。

---

## 7. 附带修复:闪电不消失、永久残留(2026-09-28)

> 症状(用户报):某些情况下生成的闪电不会消失,会一直存在。

### 7.1 根因(两层,叠在一起才致命)—— 有 Player.log 实证

```text
Coroutine couldn't be started because the the game object 'VolkenLightningBolt' is inactive!
```

**① 协程启动会静默失败。** 旧实现把 bolt 挂在 `NearCamera` 下,并且在**bolt 自己身上** `StartCoroutine` 跑主协程与 `CreateSplit` 分叉协程。Unity 在 `activeInHierarchy == false` 时**不启动协程、只打一行警告**(上面那行),而 `CastBolt()` 里 `_playing = true` 已经置上了 —— 于是主协程从未运行。

**② 自毁链是单点故障。** `Update()` 的第一行是 `if (!_fadeOut) return;`,而 `_fadeOut = true` 只在**主协程跑完最后一句**时才赋值。协程因①(或任何其他原因:场景卸载、相机被换掉/销毁、父物体被置非激活)没跑到最后,**就再没有任何代码路径能销毁这个对象** —— `Update()` 在 inactive 时也不运行,连"兜底"都没有。相机恢复激活后,残留物永远停在全亮状态;分叉线更明显:主干没了、几根叉还挂在天上。

### 7.2 修法(结构性,不是打补丁)

| 改动 | 为什么这样改 |
|---|---|
| **动画改由 `Update` 驱动的时间状态机**(`Grow`→`Flash`→`Fade`) | 去掉"必须有活着的协程"这个前提。`Update` 天然只在 active 时运行 → 被藏起来时动画自己暂停、恢复时接着播,**不可能出现"协程死了但对象还活着"**。顺带干掉了那行警告 |
| **bolt 改为根物体**(`Create` 不再收 `parent`) | 挂相机下换来的唯一好处是"随相机销毁",而那正是①的成因。LineRenderer 本来就是 world space,父物体毫无必要 |
| **分叉改用独立的 `SelfDestruct` 组件计时** | 分叉原来是 bolt 身上的子协程,同一故障源;现在自带计时器,被藏起来也只是暂停、恢复即销毁 |
| **三条独立销毁路径** | ① 正常播完;② **僵死判定**:连续不可见 > `MaxHiddenSeconds`(1.5s)→ 认定不会恢复;③ **硬性寿命** `HardLifetime`(3.5s,自出生起算)→ 无条件上限。任一条独立成立即可销毁 |
| **`LightningBolt.DestroyAll()` 整批清理** | `LightningModule.SetActive(false)`(含离开飞行场景)时调用,不留任何一道雷到下一个场景 |
| **闪烁用"时刻表重算"而非"翻面"** | 每帧推进必须能容忍 `dt` 跨过多个时间点(帧率抖动),按 `_phaseElapsed` 重算当前该亮还是该灭 |

**两个时间量必须分清楚**(最容易改错的地方):`_lastActiveTime` = 最近一次还在 `Update` 的时刻,随时间**前移**(用于僵死判定);`_bornTime` = 出生时刻,固定不变(用于硬性寿命)。混用会让僵死判定退化成"活满 1.5s 就杀",正常雷全被误杀。

### 7.3 副作用 / 与雷声的关系

- **雷声不受影响**:雷声是 `PlayScheduled` + `dspTime` 独立定时的,即使 bolt 被提前清掉,已排好队的雷声照样在预定时刻响 —— 两者生命周期本来就是解耦的。
- 僵死判定取 1.5s 是**有意的取舍**:误杀(视角切换时那道雷消失)的代价 ≈ 0(它本来就看不见了),漏杀的代价 = 天上挂一道永远不灭的雷。
- 正常一道雷的总时长没变(生长 → 4 次闪烁 → 淡出),观感与原版一致。

### 7.4 真机验证要点

- 反复进出飞行场景 / 切换相机视角,**不应**再看到任何残留闪电;
- 日志若出现 `LightningBolt hidden for …s (phase=…) — cleaning up` 或 `hard lifetime 3.5s exceeded` → 说明兜底路径真的被触发过(正常时应很少见);
- `storm loop stopped (cleared N in-flight bolt(s))` 的 N 一般应为 0~1;
- Player.log 里**不应再出现** `Coroutine couldn't be started … 'VolkenLightningBolt' is inactive`。

---

## 8. 附带修复:闪电偶发**紫红色**(2026-09-28)

> 症状(用户报):雷的 shader 还会出现紫色的情况。

### 8.1 先排除掉的两种可能(有证据)

| 猜测 | 排除依据 |
|---|---|
| shader 编译失败 | Player.log 里**没有** `Shader error in 'Hidden/Volken/LightningBolt'`(只有游戏本体自带 shader 的 fallback 警告) |
| shader 没打进 bundle / 没加载到 | `LightningModule.LoadBoltShader()` 失败会打 `bolt shader NOT FOUND` —— 日志里没有;而且闪电平时画得出来 |

**结论:紫红是运行时材质被销毁** —— Unity 在渲染器拿不到可用材质时回退 **Error shader**,颜色正是淡紫/品红。

### 8.2 成因链

1. 分叉线**共享**主干的材质实例:`sr.material = _boltMat`;
2. 主干收尾 `OnDestroy` 里 `Destroy(_boltMat)`;
3. 分叉的存活计时原来用 `Time.realtimeSinceStartup`(**失焦/暂停时仍前进**),但 `Update` 在分叉被隐藏时**不运行** —— 于是分叉完全可以比主干活得久;
4. 渲染那条线时材质已销毁 → 紫红。

### 8.3 修法(三条一起,缺一不可)

| 改动 | 作用 |
|---|---|
| 分叉改持**自己的**材质实例(`new Material(_boltMat)`) | 不会被别人的销毁牵连 |
| 主干在 `Finish()` 里**连分叉一起销毁**(`DestroySplits()`) | 保证"渲染器 ↔ 材质"同生共死;同时回收材质(否则每次落雷漏几份) |
| 分叉计时改 `Time.unscaledTime` | 暂停时跟着停,不会"暂停期间偷偷把分叉耗死" |

### 8.4 shader 侧的两处加固(以及一个真 bug)

**① 主干与落点闪光拆成两个单 Pass shader —— 这里藏着一个真 bug。**

原先两者共用一个双 Pass shader(`Bolt` / `Flash`),靠 Pass 名区分。但 Unity 的规则是**一个 Material 只用 Shader 的第一个匹配 Pass**,而这两个 Pass **都没有 `LightMode` 标签** —— 它们对"Pass 选择"而言是不可区分的。后果:

- 落点闪光球其实**一直在跑主干那套画法**(`_CoreWidth` 与 halo 计算从来没生效过);
- C# 里试图"按名字启用某个 Pass"(`SetShaderPassEnabled`)在无 `LightMode` 时并不可靠 —— 这条路走不通,所以本轮改为**拆成两个各含单一 Pass 的 shader**:`LightningBolt.shader`(`Hidden/Volken/LightningBolt`,主干/分叉)+ `LightningFlash.shader`(`Hidden/Volken/LightningFlash`,落点闪光)。
- 顺带删掉了主干 shader 里那对没有对应 `#pragma multi_compile_instancing` 的 instancing/stereo 宏,并显式加了 `#pragma target 3.0`(顶点色 `COLOR` 需要 ≥3.0,不写会落到默认 2.5)。

**② `Fallback Off` → `Fallback "Hidden/Internal-Colored"`。**

`Fallback Off` 的含义是"没有任何后备 shader",所以 shader 一旦不可用就直接是 Error shader(紫红),**既难看又掩盖真因**。换成 Unity 内置、任何构建里都存在的最简着色器后,最坏情况是"画得不对但能看出是什么",便于定位。

### 8.5 ⏳ 打包影响(重要)

新增了 `Assets/Scripts/Volken/Weather/LightningFlash.shader` —— **它也必须加进 `Assets/ModData.asset` 的 `_otherAssets`**,否则打进 bundle 的只有主干 shader。没加也不会崩:`LoadFlashShader()` 载入失败时会让闪光球退化用主干 shader(画法不对但可见),日志里会有一行 `flash shader NOT FOUND — strike flash will fall back to the bolt shader`。

> 与 §5.3 合并后的完整打包清单:`LightningBolt.shader` + **`LightningFlash.shader`** + **9 条 `volkenThrunder-*.wav`**,并删掉 5 条悬空的旧 OGG GUID。

### 8.6 真机验证要点

- 反复劈雷,闪电不应出现紫红/淡紫;
- Player.log 不应出现 `Shader error in 'Hidden/Volken/LightningBolt'` 或 `Shader error in 'Hidden/Volken/LightningFlash'`;
- 落点闪光球的观感应与之前不同(现在它才真正跑 halo 那套逻辑)—— 如果感觉过亮/过弱,调 `VolkenWeatherConfig.LightningSection.flashIntensity`(代码里 flash 用的是 `flashIntensity * 0.4`)。
