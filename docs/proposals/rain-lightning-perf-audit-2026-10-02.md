# 雨 / 雷性能审计(2026-10-02)

> 状态:📋 **规划** —— C# 侧低风险项**已落地**(`dotnet build Volken.csproj` = 0 错误),GPU 侧(`FillArgs` 原子加归约、分叉合批)与阈值型改动**未排期**,且**全部未做真机 / 编辑器验收**。
> 日期:2026-10-02
> 关联:**雨**[雨计划](../sp2-rain-particledomain-port-2026-09-28.md) §10(实施记录唯一事实源)/ **雷**[雷声真实化](thunder-realism-2026-09-28.md) / [雨雾复盘](../archive/weather-rain-fog-postmortem-2026-09-27.md)(跨界移植铁律)。
> 定位:对新增的雨(`Rain/`)与雷(`Lightning/`)代码做一次"高开销语句"审计,给出**收益排序 + 具体改法 + 验收判据**。

---

## 0. 结论先行

雨侧真正的开销在 **GPU 规模**(2 次 compute dispatch + 1 次实例化间接绘制,`amount` 可到 20 万 × 8 顶点),C# 侧那几条每帧语句是"每帧都发生的垃圾",消掉不亏但**不改变帧率量级**;雷侧的浪费是**结构性**的 —— 一次落雷会造出几十~几百个 GameObject / Material,**落雷瞬间的卡顿主要来自这里**。

| 位置 | 高开销语句 | 每次 / 每帧量级 | 处置 |
|---|---|---|---|
| `LightningBolt.SpawnSplit` | `new Material(_boltMat)` **每条分叉一份** | 默认 **85 份** / 上限配置 **549 份** 一次落雷 | ✅ 已改(整道雷共享 1 份) |
| `LightningBolt.SpawnSplit` | `BuildBoltGradient()` 每条分叉一个渐变 | 85 个 Gradient + 170 个 key 数组 | ✅ 已改(静态共用) |
| `LightningBolt.Build` | `BuildSphereMesh(8, 12)` | 每道雷 1 个网格(内容恒定) | ✅ 已改(静态共用) |
| `LightningBolt.DestroySplits` | 收尾时 `go.GetComponent<LineRenderer>()` | 85 次原生查找 | ✅ 已改(不再需要) |
| `RainParticles.Tick` | `_cam.GetComponent<CloudRenderer>()` | 每帧 1 次原生组件查找 | ✅ 已改(缓存 + 1 s 重探) |
| `RainParticles.Tick` | `Game.Instance?.FlightScene?.CraftNode?.CraftScript` 链 | 每帧穿透 **4 次** + `PlanetToFramePosition` | ✅ 已改(每帧 1 次采样) |
| `RainParticles.Tick` | `_args.SetData(ArgsReset)`(5×uint) | 每帧 20 B 上传 | ✅ 已改(每帧只写 `instanceCount` 4 B) |
| `RainParticles.Tick` | `_camRight/_camUp/_camFwd/_testRowSpacing` | 每帧 12 个 float,正规运行**根本没人读** | ✅ 已改(仅 `_testRow` 时上传) |
| `RainParticles.compute` `FillArgs` | 每颗粒子一次**同地址** `InterlockedAdd` | 20 万粒 = 20 万次全局原子(≈数十 µs 量级) | ⬜ 未做(§3.1) |
| `LightningBolt` 分叉 | 85 个 `LineRenderer` + 85 份独占材质 | **85 个 draw call**,存活 0.2~0.5 s | ⬜ 未做(§3.2) |
| `arcs` × `splits` | 无乘积上限 | `arcs=64,splits=8` → **549** 个对象 / 次 | ⬜ 未做(§3.3) |

---

## 1. 审计口径

- **纯静态审计 + 数量级估算**,没有 profiler 捕获;数字都是"由代码结构必然成立"的计数(对象 / 材质 / 原子次数),不是实测耗时。
- 离线环境**编译不了 HLSL、跑不了 Unity**,所以 shader / compute 的改动一律只给方案不落地(一个 kernel 编译失败会毒掉整个 compute)。
- 默认口径:`RainParticles.Capacity = 10000`(面板默认 `rain.amount = 20000`)、`LightningSection.arcs = 20`、`splits = 4`、`ThunderVoices = 4`。
- 分叉条数公式(读 `LightningBolt.StepGrow` 得出):`(arcs − 3) × (splits + 1)` —— `_arcIndex < arcs - 2` 落在 `_arcIndex ∈ [1, arcs−3]`,每个节点生 `splits + 1` 条。`arcs=20/splits=4` → **85**;`arcs=64/splits=8`(配置上限)→ **549**。

---

## 2. 已落地(2026-10-02)

改动文件仅两个:`Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs`、`Assets/Scripts/Volken/Weather/Rain/RainParticles.cs`。shader / compute **未动** → 资源包不用重打,只需重编程序集。

### 2.1 雷电:分叉资源从"每分叉一份"改为"整道雷一份"

【决策:2026-10-02】分叉材质改由 `LightningBolt` 持有**整道雷共享的一份拷贝**(`SplitMaterial`),不再每条分叉 `new Material(_boltMat)`。

| 每道雷(默认 85 条分叉) | 改前 | 改后 |
|---|---|---|
| `Material` 构造 | 85 + 主干/闪光 2 = **87** | **3** |
| `Gradient` 构造 | 85 + 1 = **86** | **1**(静态) |
| 落点闪光网格 | 1 | **0**(静态共用) |
| 收尾 `GetComponent` | 85 | **0** |

- 旧写法"分叉必须各自持有材质实例"的**理由仍然成立**(直接共享主干材质 → 主干 `Destroy(_boltMat)` 后分叉变品红/淡紫),但**共享一份独立拷贝同样安全**:所有权归 `LightningBolt`,统一在 `DestroySplits` 里、所有分叉销毁之后回收。原实现还有个所有权不清的隐患 —— `SelfDestruct.OnDestroy` 与 `DestroySplits` 会**重复销毁同一份材质**,已收敛为单点销毁。
- **观感差异(需人工确认)**:分叉亮度由"每条各自随机冻结"变成"整道雷共享一个冻结值"(取第一条分叉生成时的 `_boltMat` 参数)。理论上更一致,但这是唯一可感知的视觉变化。
- **未解决的仍是结构性问题**:85 个 `LineRenderer` 各自一份材质 → **合不了批**,85 个 draw call + 85 个 `SelfDestruct.Update` 照旧(§3.2)。

### 2.2 雷电:程序化网格 / 渐变改静态共用

落点闪光球网格(117 顶点经纬球)与主干颜色渐变的**内容恒定**,改为静态懒加载资源;网格打 `HideFlags.HideAndDontSave`,避免被场景切换的 `UnloadUnusedAssets` 回收。`LineRenderer.colorGradient` 是**拷贝语义**,共享同一份 `Gradient` 实例安全。

### 2.3 雨:每帧一次的 craft 链采样 + `CloudRenderer` 缓存

- 径向"下"(`GravityNormal`)、本体速度(`FrameVelocity`)、参考帧位置(`FramePosition`)、相机海拔(`PlanetToFramePosition` + `PlanetData.Radius`)原本**各查一次** `Game.Instance?.FlightScene?.CraftNode?.CraftScript` → 合并为 `SampleCraft` 一次采样(`CraftSample` 结构)。
- 顺带修掉一个小健壮性问题:`GravityNormal` 为 NaN 时旧写法会把 NaN 一路传进 compute(靠 compute 内兜底),现在直接回退世界"下"。
- `_cam.GetComponent<CloudRenderer>()` 每帧一次 → 缓存 + **每秒重探**:云渲染器是后装配的,缓存空 / 深度图未建时必须能自愈,但不该每帧做原生查找。
- 行为等价性:独立模式(`StandaloneMode`)仍完全不碰游戏 API;`down`/`axisVel`/海拔的各种降级路径逐条对齐旧实现。

### 2.4 雨:每帧 uniform 上传瘦身

- 间接参数只保留 `args[0] = indexCount = 12`(建 buffer 时写一次),每帧只复位 `instanceCount`(4 B)。
- `_camRight/_camUp/_camFwd/_testRowSpacing` 只在 `Positioning` 的 `_testRow == 1` 分支里被读 → 只在等距排探针开启时上传。正式运行每帧少 4 次 `SetVector/SetFloat`(含 4 次 uniform 名字解析)。

---

## 3. 未落地(按收益排序)

### 3.1 `FillArgs` 单地址原子加 → 组内归约(compute)

`RainParticles.compute` 的 `FillArgs` 对**同一个地址** `_ArgsBuffer[1]` 做 `InterlockedAdd`,每颗粒子一次。20 万粒 = 20 万次同地址全局原子。改成 `groupshared` 归约后,全局原子降到 `amount / 64`(20 万 → 3125):

```hlsl
groupshared uint _groupCount;

[numthreads(64, 1, 1)]
void FillArgs(uint3 id : SV_DispatchThreadID, uint gi : SV_GroupIndex)
{
    if (gi == 0) _groupCount = 0;
    GroupMemoryBarrierWithGroupSync();
    if (id.x < _amount) { uint o; InterlockedAdd(_groupCount, 1u, o); }
    GroupMemoryBarrierWithGroupSync();
    if (gi == 0 && _groupCount > 0) { uint o; InterlockedAdd(_ArgsBuffer[1], _groupCount, o); }
}
```

- **不要**改成"CPU 直接写 `instanceCount`"绕过 —— compute 头部已注明阶段 4 会做剔除、`amount < capacity` 时 `FillArgs` 是必需的。
- 未落地的原因:**离线编不了 HLSL**,而"一个 kernel 编译失败毒掉整个 compute"是这个项目的实测结论(见 `RainParticles.compute` 头部铁律)。改完必须在 `RainTests` / 编辑器预览台先跑一次。

### 3.2 分叉合批:85 个 `LineRenderer` → 1 个 Mesh

85 个 `LineRenderer`(各自独占材质) = **85 个 draw call**,持续 0.2~0.5 s。治本是放弃 `LineRenderer`,把一道雷的全部分叉**按同一套 billboard 规则烘焙成一个程序化 Mesh** + 一个 `MeshRenderer` → 1 个 draw call。
代价:要用 `LightningBolt.shader` 现有的 `COLOR` 约定自己生成面向视线的 ribbon(宽度轴由视线决定,见复盘 §5 铁律①),约百行几何代码,视觉需重调。**收益最高但风险也最高的一项。**

### 3.3 `arcs × splits` 无乘积上限

分叉条数 = `(arcs−3) × (splits+1)`,`arcs` 上限 64、`splits` 上限 8 → 单次落雷最多 **549** 个 GameObject + 549 个 draw call。建议给乘积加软上限(超出部分按比例抽稀)并在日志里留一行说明。**注意这会改变极端配置下的观感**,所以没擅自加。

### 3.4 `Light.range = 8000` 的实时光源

`lightRange` 默认 8000 m 的实时点光源(闪光期约 0.2 s 内亮灭)。BIRP 前向渲染下,超大 range 会把**极大范围**的物体纳入逐物光照。建议评估:是否只保留屏幕空间闪光 / 缩小 range。

### 3.5 `Mod.Diag` 是 always-on 日志

`Mod.Diag` 走 `Debug.unityLogger.LogFormat`,**不受 `DevMode` 控制**且会抓托管堆栈。雨心跳 **2 条/秒**(心跳 + 轴自检行),雨声 1 条/10 秒。量很小,但真要抠帧时间,给心跳加一个 `static bool VerboseDiag` 开关即可(排查时用户仍能开)。

### 3.6 其余微项

- `RainAudio.Tick` 每帧 4 次 `AudioSource.volume` 写(两组乒乓各两条),静音时也没省 → 可加"值未变则跳过"。
- `VolkenWeather.UpdateCameraMetrics` 每帧**也**走同一条 craft 链(海拔 / AGL / 速度),与雨的新采样重复 → 可考虑共用一份采样(注意天气在 `IsActive == false` 时仍在跑)。

---

## 4. 验收与回归判据

已做的验证:`dotnet build Volken.csproj` → **0 错误**,5 条警告全部是仓库既有(`ForceSetting` / `PlanetRingsZWriteFix`);两个改动文件编码 = **UTF-8 无 BOM + LF**。

真机 / 编辑器还要看:

1. **分叉不再紫红**:连劈多次,分叉在自毁后不应变成品红/淡紫(共享材质所有权改动的直接回归点)。
2. **落雷瞬间帧时间**:改前每次落雷要构造 87 份 `Material` + 86 个 `Gradient`,真机上应能看到该尖峰显著变矮(Frame Debugger 或 Profiler 的 `Material` 构造调用)。
3. **软粒子仍生效**:进飞行场景后 `softDepth=ready`(雨状态行),云装配晚于雨时应在 1 s 内自愈;`_InvFade` 不为 0 时雨的边缘仍被云遮挡。
4. **换帧重定位不变**:`rec(ev…/jp…)` 计数与整片雨不跳的现象与改前一致(craft 采样合并的直接回归点)。
5. **等距排探针仍可用**:`TestRow = 1` 时那排 20 根仍按相机基排布(相机基改为按需上传)。
6. **GPU 侧(若采纳 §3.1)**:先确认 compute 全 kernel 编译通过,再看 `diag respawns/s` 与 `readback instanceCount` 是否与改前一致。

---

## 5. 参考(文件:行号,均为改动后)

| 位置 | 内容 |
|---|---|
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs:191` | `SharedFlashMesh` 静态共用网格 |
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs:205` | `SharedGradient` 静态共用渐变 |
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs:215` | `SplitMaterial` 整道雷共享的分叉材质 |
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs:478` | `DestroySplits` 单点回收材质 |
| `Assets/Scripts/Volken/Weather/Lightning/LightningBolt.cs:503` | `SpawnSplit` 分叉生成 |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs:445` | `CraftSample` 结构 |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs:456` | `SampleCraft` 一帧一次采样 |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs:827` | 每帧只复位 `instanceCount` |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs:845` | 相机基仅在等距排时上传 |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs:881` | `CloudRenderer` 缓存 + 1 s 重探 |
| `Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.compute` | `FillArgs` 原子加(§3.1 的对象) |
| `Assets/Scripts/Volken/Weather/Lightning/Shader/LightningBolt.shader` | 分叉 ribbon 的 `COLOR` 约定(§3.2 的依据) |
