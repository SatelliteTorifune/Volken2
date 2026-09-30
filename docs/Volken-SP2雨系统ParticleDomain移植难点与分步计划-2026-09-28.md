# Volken —— SP2 雨系统 `ParticleDomain` 移植难点与分步计划(2026-09-28)

> 状态:📋 规划中(难点已核实,**未开工**;不改动任何现有代码)
> 日期:2026-09-28
> 关联:
> - [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) —— **本文档的必读前置**。本文所有"不能照抄"的判断都建立在它的铁律上(尤其 §3.1㈠㈡㈢、§5.1)。
> - [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md) —— 母计划(**已转 proposals**)。§10.2b/10.2c0/10.2c2/10.2d 是实施期 30+ 处实测更正,本文大量引用;资源均已据原始数据复核。
> - [`to-do.md`](to-do.md) 第 7 项「雨/雾重做(未排期)」—— 本文档是该条的执行细案。
> 主题定位:把「SP2 的 GPU 计算粒子雨域(`Jundroo.Common.ParticleDomain` + `Assets.Scripts.Flight.ParticleHandler`)移植进 Volken(BIRP / Unity 2022.3 / SR2 mod)」这件事,拆成**难点清单 + 可独立验收的 8 个阶段**。每个阶段都有**准入门槛、验收判据、停止条件**,目的是**不再出现"连续多轮无可信改善信号"**。

---

## 1. 一句话结论

**移植的阻力与 C# 逻辑无关** —— 状态机、淡变、画质档、配置序列化几乎可以照搬。
**真正的难点是 4 条,且第 1 条是"必须主动偏离 SP2"**:

1. **SP2 的雨丝朝向实现(`AlignStreaks`)正是本项目已判死刑的那条路线**(世界空间长度轴 → 视角相关退化)。它在本项目里会以**与上次相反的症状**复发(上次是"横块",SP2 是"细长斜线拉飞")。
2. **URP 遮挡专有物(`RTCameraRendererFeature` / RenderGraph)在 BIRP 里完全不存在**,必须重写,且**空深度图会让雨整体消失且无报错**。
3. **6 个 compute kernel 的 HLSL 源码在全安装中不存在**(只有 DXBC),且 **sp2d4 是 Unity 6000.2、本工程是 2022.3** → 跨版本导入 .asset 大概率失败 → **必须手写**。
4. **数量级必须重新标定**:SP2 的「固定 50 m 域 + 10 万粒子」在 SR2 的航天速度下**必然失败**(域半径必须随相机速度自适应,密度补偿指数 r^2.5)。

> 一句话给动手的人:**能抄的是"架构与接口",不能抄的是"朝向数学"和"默认数值"。**

---

## 2. 为什么这件事有"前车之鉴"(起点必须明确)

| 项 | 事实 | 出处 |
|---|---|---|
| 上次结果 | 雨(阶段 2)/雾(阶段 3)实现**整体移除**,当前只保留雷电 | [`to-do.md`](to-do.md) L11-14 |
| 代价 | **8 轮迭代 / 27 条更正 / 约 150 KB 代码被弃**(`Rain.cs` 113 KB + `RainParticles.compute` 14 KB + `RainParticles.shader` 10.5 KB + 雾相关);3 条环境雨声一并弃用 | 教训 §2 |
| 代码现状 | 全部 stash 在 `%TEMP%\volken-rain-fog-stash`(**可整体取回**) | 教训 §2 / 计划 §10.2e |
| 保留的净收益 | 教训清单(27 条)、诊断方法、量化基线;**配置层与 UI 占位已就位,重做时不用动** | 教训 结尾 / 计划 §10.2f㊶ |
| 失败的真正原因 | **不是代码不可修**(每轮都修对了当时认定的 bug),而是**诊断量错了对象**(用世界空间夹角当屏幕倾角)→ 信心被耗尽 → 在"两次修正之间没有任何一次可信改善信号"时被放弃 | 教训 §1 / §5.4 |

> ⚠️ **本文档的存在意义就是把第 5 条堵死**:阶段 2 与阶段 4 之间必须出现一次**可信的屏幕空间改善信号**,否则**不许进入下一阶段**。

---

## 3. 难点清单(按"会不会让整件事失败"排序)

### 3.1 难点 1【决定性】:SP2 的雨丝朝向实现就是本项目判定过的错误路线

**SP2 的实现**(`ParticleDomain.AlignStreaks`,已由反编译与字节级复核确认):

```csharp
Vector3 side = Vector3.Cross(Mathf.Abs(fwd.y) < 0.999f ? Vector3.up : Vector3.forward, fwd).normalized;
Vector3 up   = Vector3.Cross(fwd, side);
float   len  = Mathf.Clamp(dir.magnitude * _stretchAmount /*0.045*/, 1f, _stretchLimit /*3.5*/);
m.SetColumn(0, side);        // 宽
m.SetColumn(1, fwd * len);   // 长 ← 世界空间的速度方向
m.SetColumn(2, up);
```

**对照本项目铁律**(教训 §3.1㈠㈡ / README 决策速查「雨重做的方向铁律」):

> 细长 billboard 的**长度轴必须在屏幕平面内表达**;**长度轴只由物理量决定,宽度轴由视线决定**(`W = normalize(cross(L, viewDir))`);**世界空间长度轴 + 任意宽度轴 → 视角相关地退化成"横块"**。

SP2 是**世界空间长度轴 + 任意宽度轴** → 同一个坑。**退化表现与上次同源,且多一种死法**(不是"相反"):

| | 长轴 L | 宽轴 W | `L ∥ 视线` 时屏幕上是 | 实测/推证 |
|---|---|---|---|---|
| 上次失败实现 | 世界空间 | 任意世界向量 | **长度→0、宽度完整** → 满屏**横块** | 实测 `屏幕倾角=90.0° / 长 8.75m / 宽 0.05m / 长宽比 175`(教训 §3.1㈠) |
| **SP2 `AlignStreaks`** | **世界空间** | 正交化后的 `side` | **长度→0、宽度完整** → **横块**(与上次同源);另:当 `side ∥ 视线` 时宽度→0 → **细线** | 本文 §3.1 推证 + **阶段 1 探针版本 B 真机对照**(2026-09-28 已落地) |

> 为什么 SP2 的 `fwd ∥ 视线` 也是横块:`side = cross(up_or_forward, fwd)` 与 `fwd` 垂直,
> 当 `fwd ∥ 视线` 时 `side ⊥ 视线` → side 落在屏幕平面内 → **宽度完整投影**;
> 而长度轴 `fwd * len` 平行视线 → **投影为 0**。与上次失败实现的几何完全一致。
> "细线"死法则发生在 `side ∥ 视线` 时(如相机朝东、side 恰好东西向):宽度投影为 0,只剩一根长斜线。

> 讽刺点:SP2 的 `Mathf.Abs(fwd.y) < 0.999f` 分支本意就是防退化,但它**只对世界 up 防退化,对"视线"毫无防备** —— 因为 `AlignStreaks` 在 CPU 侧**根本不知道相机朝哪**(它只拿到速度与相机位置,不拿相机基向量)。

**⚠️ 已写入代码的推导(阶段 1 探针版本 B 负责实证)**:宽轴 `side = cross(up_or_forward, fwd)` 的退化条件不是"`fwd` 平行世界 up",而是 **`fwd` 平行于 `side` 的生成源**;真正决定屏幕观感的是 `fwd` 与**视线**的夹角。探针的"模式 2(朝相机,退化)"就是让版本 B 复现上述横块、同时验证版本 A 不退化。

**→ 移植决策(本文档的核心结论):不照抄 `AlignStreaks`,改用屏幕平面内构轴。**
算法骨架来自教训 §3.1㈢(**该文档自注"尚未在真机验证",所以阶段 2 的全部意义就是验证它**):

```hlsl
// 输入:airDir = 风 + 重力 − 玩家速度(不归一化也可);camRight / camUp / viewDir 来自相机
float2 d2 = float2(dot(airDir, camRight), dot(airDir, camUp));   // 屏幕平面内的气流分量
if (dot(d2, d2) < eps) d2 = float2(0, -1);                       // 退化 → 竖直下落,绝不交给浮点噪声
float2 L = normalize(d2);                                        // 屏幕平面内的长度轴
float3 W = normalize(cross(float3(camRight * L.x + camUp * L.y, 0), viewDir)); // 宽度轴由视线决定
```

要点:
- `airDir` **不需要归一化**(点乘后自动携带量级,`stretch = clamp(|airDir| * 0.045, 1, 3.5)` 仍成立);
- **沿视线的分量天然被点乘丢掉**,不需要 `normalize` 一个近零向量 → 从数学上消灭"径向爆散";
- 退化时**显式兜底为竖直下落**(`(0,-1)`),这是消除"换视角换症状"的关键。

**开放问题(阶段 4 解决)**:SP2 用矩阵第 3 列(`up`)携带了一个**绕长轴的随机滚转**;改成屏幕平面 2D 基后,这个滚转没有自然位置 —— 需要显式给每粒子一个随机 roll(或在 2D 平面内旋转 `L`),否则雨丝截面朝向会变得单一。**这是"偏离 SP2"必须自己补的东西,不是 bug。**

### 3.2 难点 2:URP 遮挡专有物在 BIRP 里不存在

SP2 的遮挡 = `OcclusionDepthCam`(256×256 正交深度 RT)+ **`Jundroo.Common.RTCameraRendererFeature`(`ScriptableRendererFeature`,`RTCameraRenderPass`,`AfterRenderingOpaques`,`DepthOnly`)**。

本工程:**`ProjectSettings/GraphicsSettings.asset` 的 `m_CustomRenderPipeline: {fileID: 0}`,`QualitySettings` 全部 `customRenderPipeline: {fileID: 0}`,无任何 URP/HDRP 包** → **这套完全不可用**。

| 方案 | 内容 | 对应 SP2 画质档 | 成本 |
|---|---|---|---|
| **A(起步必选)** | **不做几何遮挡**。只保留 CPU 侧整体淡出:AGL<100 m / 座舱内 / 入水冻结 / 云内按淡化值渐隐 / 上下地面切换 1 s 快速重入 | Low / Medium | 0(SP2 Low/Medium 本来就不做遮挡) |
| **B(二期可选)** | 保留 256² 正交深度相机 + `_previousOrthoVP` 延迟一帧链路,只替换深度图来源:`_occlusionCam.targetTexture = _occlusionTex; _occlusionCam.RenderWithShader(depthShader, "")`,或 CommandBuffer + `DrawRenderers(DepthOnly)` | High | 中(且带致命护栏,见下) |

> 🔴 **必须照搬的硬护栏(计划 §10.2b ⑲)**:遮挡深度图**一旦为空**,`CullAndOccludePoints` 会把**每个**粒子判成"被几何体遮挡"并剔除 → **雨完全消失且没有任何报错**。
> 因此走不走遮挡内核必须**两个条件同时成立**:`OcclusionEnabled`(配置开关)**且** `OcclusionDepthRendered`(由"真正把深度渲进那张 RT 的地方"置位)。后者在方案 B 接通前**恒为 false**。

**已知限制(方案 A 必须写进 UI/文档)**:低空穿建筑时雨会穿墙。

### 3.3 难点 3:6 个 kernel 没有源码,且跨 Unity 版本

**源码在两个来源里都不存在:**

| 资产 | 实际状态 |
|---|---|
| `<SP2_D4>/Assets/ComputeShader/BillboardParticles.asset` | 只有**编译后的 DXBC**(hex 内联)+ 反射数据(kernel 名、常量缓冲成员、线程组 64 / FillArgs 1) |
| `Custom/DropletInstancing`(实例化着色器) | 只有 DXBC;且 prefab 里 `_instancingShader` 引用为 **null**,全安装**无任何代码 `Shader.Find` 它** |
| `Jundroo_ParticleConstantNormal_Soft.shader` | `//DummyShaderTextExporter` 占位模板,**不是** SP2 真用的那个(属性名对不上,`_MainColor ≠ _Color`) |

**⚠️ 必须先更正的既有结论**:母计划 §8 第 2 条写「SP2 与 Volken 同为 2022.3.62,`BillboardParticles.asset` 大概率直接可用」—— **这句是错的**。
实测:`<SP2_D4>/ProjectSettings/ProjectVersion.txt` = **`6000.2.14f1`**;本工程 = **`2022.3.62f3`**。
→ 该 `.asset` 里的 DXBC 是按 Unity 6000.2 的 D3D11 目标编的,**2022.3 重新导入/重编很可能失败**;而它在工程里是「带 GUID 的资产引用」而非可读文本。导入失败 → `FindKernel` 拿不到 kernel → 后续全线崩。

**→ 决策:放弃导入 `.asset`,按已完全确定的接口手写 `.compute`。**
好消息:**接口是第一手可信的**(compute shader 资产内的反射数据 + 计划 §10.2b 表格互相印证):

| kernel | 绑定 | 线程组 |
|---|---|---|
| `Positioning` | out `_Positions`;`_domainRadius` `_domainPos` `_frameVel` `_particleVel` `_InstanceCount` | 64 |
| `Randomize` | out `_Positions`;`_domainRadius` `_domainPos` `_InstanceCount` | 64 |
| `TranslateFixed` | out `_Positions`;`_translation` `_InstanceCount` | 64 |
| `CullAndOccludePoints` | in `_Positions`;out `_CulledPositions` `_CulledCount`;tex `_OcclusionDepth`;`_FrustumPlanes[6]` `_OcclusionNear` `_OcclusionFar` `_InstanceCount` `_OcclusionVP` `_OcclusionView` | 64 |
| `CullPoints` | in `_Positions`;out `_CulledPositions` `_CulledCount`;`_FrustumPlanes` `_InstanceCount` | 64 |
| `FillArgs` | in `_CulledCount`;out `_ArgsBuffer` | 1 |

**手写这条路上必须避开本项目用血换来的 API 陷阱:**

| # | 事实 | 后果 | 正确做法 |
|---|---|---|---|
| ㉟ | **`RWStructuredBuffer<uint>` 既没有 `InterlockedAdd`,也没有 `Load4/Store4`** | **compute 编译失败 → 品红 + 掉帧**;而且**同一个根因发作了两次**(第一次只修 `InterlockedAdd`,因为日志被刷屏 4728 行,于是"紫色照旧") | 用 **`RWByteAddressBuffer`**;`_CulledCount.InterlockedAdd(0, 1u, slot)` 的 `0` 就是字节偏移;`_ArgsBuffer.Load4/Store4(0, …)` 本身即字节地址语义 |
| ⑩ | **compute shader 里没有 `_Time`** | 编译报未定义;即便能编,回绕重生不掺时间 → 每个粒子在同一 xz 重生 → **固定竖线** | 自加 `float _time`,由 C# 每帧写 `Time.time` |
| — | **`FillArgs` 是唯一写 `_ArgsBuffer.instanceCount` 的地方** | 它一失败,参数保持 C# 初始化的**容量值** → **硬画 `容量 × 12` 顶点/帧**(实测 400000 → 480 万顶点/帧) | 诊断次序固定:**"能画出来但品红 + 掉帧" → 先查 compute 编译错误,不要先调参** |
| ⑫ | 实例索引必须用 **`SV_InstanceID`** | 用 `UNITY_VERTEX_INPUT_INSTANCE_ID` / `unity_InstanceID` 那一套容易拿到**恒 0** 的实例号 → **所有雨丝重叠在同一处** | 用 `SV_InstanceID`(与 `_CulledPositions` 的原子累加写入顺序一一对应)。⚠️ 计划自注"属推断,尚未在真机跑过" → **阶段 4 必验** |

### 3.4 难点 4:SR2 的渲染时机与浮动原点

**(a) 绘制不能放在 `Update` 里。** `Graphics.RenderMeshIndirect` 在 `Update` 中调用会得到"帧开始前的游离渲染命令",与具体相机无关;雨是**独立网格**,必须排进该相机的透明队列并赶上它自己的深度纹理。

**(b) 🔴 但 `Camera.onPreCull` 静态委托在本工程从不触发**(计划 §10.2b ⑪,真机排除法证据:前置条件全满足、`fade` 从 0 正常爬到 0.765,但 `draws/s` 恒 0、`everDrew=False`,且 `RenderForCamera` 里三条 `LogThrottled` **一条都没打** → 函数**一次都没进入**)。
→ **用挂在游戏相机上的 MonoBehaviour 实例 `OnPreCull`**(实测 ≈20/s,这条路是通的)。
→ 另:静态委托类型是 `Camera.CameraCallback`,`.NET 4.x` 下 `Action<Camera>` **不能隐式转换**(CS0029)。

**(c) 浮动原点:** SR2 会 `RecenterReferenceFrame`(离帧中心 >5000 m / 帧速 >1000 m/s / 时间加速每帧 / 表面锁定切换)→ 世界坐标整体平移。

**→ 采用相机相对坐标系**(计划 §10.2b ⑨),可**天然免除**浮动原点补偿:
- 粒子位置 = **相对相机的偏移**;`_domainPos` = 相机世界位置(故 `世界 = _domainPos + 偏移` 仍成立);
- 相机高速(10 km/s 级)下没有大数精度问题;
- **`_frameVel` 恒 0 是刻意的,不是漏了** —— 不要"顺手补上";
- ⚠️ 附带要求:**视锥平面必须手工解析构造**(`BuildCameraPlanes`),**不要**混用 `GeometryUtility.CalculateFrustumPlanes`(它给**参考系空间**,与相机相对空间不一致 → 相机飞远后剔除**整体错位**,症状是"雨只在一小块出现/整片消失,且随离原点距离变化")。

**(d) 材质 uniform 是全局的**:多相机共享同一 `Material` 实例 → **每次绘制前必须按当前相机重设基向量与相机相关 uniform**,否则后一个相机覆盖前一个 → **朝向随相机跳**(计划 §10.2c G)。

### 3.5 难点 5:数量级必须重新标定(照抄 SP2 的「50 m / 10 万」必然失败)

| 参数 | SP2 | 本项目实测结论 | 必须改成 |
|---|---|---|---|
| **域半径** | 固定 **50 m** | `域直径 / 相机速度` = 粒子停留时间;实测 **348 m/s + 50 m → 0.29 s** → 肉眼"闪断";SR2 是航天游戏,轨道速度远超此值 | `needed = camSpeed * 0.6`(停留 ≈1.2 s);`radius = clamp(max(configured, needed), configured, 400)` |
| **粒子数** | 100,000 | 域是**球**,体积 ∝ r³;粒子数固定时半径翻倍 → **密度掉到 1/8**(所以"把半径调大"反而更稀) | `densityScale = (radius / 50)^2.5`;`amount = Base × quality × rainStrength × densityScale` |
| **目标密度** | 50 m 球 + 10 万 ≈ **0.191 个/m³** | 可用参照 = EVE `rain-Kerbin`:**200,000 / (4/3·π·70³) ≈ 0.139 个/m³**;上次实测到 **0.0382**(差 3.6 倍)→ **"雨时有时无"的真因是密度不足,不是闪断** | 按 EVE 反推起步值,再按画质档显式控制 |
| **数量本身** | 100,000 | **"满屏高频细节的粒子,2 万与 10 万实例肉眼几乎无差"** —— 白花 5 倍顶点 | 不照抄 10 万 |
| **上限** | — | 400,000 × 12 顶点 = **480 万顶点/帧**,是本机 5060 Laptop 的可承受边界 | 上限与画质档显式绑定;撞上限**必须打日志**(否则"雨突然变稀"无线索) |

> ⚠️ **顺序要求(踩过)**:域半径必须在**算完相机速度之后、算粒子数之前**确定,否则粒子数慢一帧、首帧用错值。
> 正确 `Tick` 顺序:`_spectatorVel`(相机速度)→ `EffectiveDomainRadius`(域半径)→ `ApplyParticleAmount`(粒子数,含密度补偿)→ `ComputeOcclusionActive` → `AdvanceFade` → 绘制判定。

---

## 4. 可以照抄的部分(移植的真正收益)

| # | 项 | 说明 |
|---|---|---|
| 1 | **6 kernel 的职责切分** | 位置积分 / 初始化 / 原点平移 / 剔除+遮挡 / 纯剔除 / 填参数 —— 与平台无关的通用结构,且接口已完全确定(§3.3 表) |
| 2 | **`_capacity` 与 `_activeCount` 分离** | 容量按画质**上界**分配一次,只在需要更大时重建,**且只在首次分配时才 `Dispatch(Randomize)`**(否则小雨转暴雨会把整片雨的位置重置 → 视觉上"雨幕跳一下");生效数量每帧只改 `_InstanceCount`,kernel 开头 `if (i >= _InstanceCount) return;` |
| 3 | **非下雨区间粒子数必须是 0** | SP2 的 `GetRainStrengthFactor` 在 `weatherValue <= 1.5` 时**返回当前值**,且 `_currentRainStrengthFactor` **初值就是 1.0** → 照搬会让**晴天常驻 5 万粒子空转**(位置推进 + 视锥剔除照跑,只是 `_FadeAmount=0` 不画) |
| 4 | **雨丝网格 = 8 顶点/12 三角的十字** | `_streakThickness 0.1 × _streakLength 2.5`;「软性涂抹纹理 + 速度方向拉伸」而非长条贴图 —— 与 EVE 的 `particleSize=0.03 + particleStretch=20` 同思路 |
| 5 | **淡入淡出非对称** | 开雨 **20 s** / 停雨 **2 s** |
| 6 | **画质档** | `Off / Low(0.25) / Medium(0.5) / High(1.0)`,High 才做遮挡;粒子数 = `100000 × quality × rainStrength`(SP2 口径,本项目改用 §3.5 的标定) |
| 7 | **CPU 侧整体淡出判据** | `WeatherValue > 2.25` 触发;AGL<100 m 或座舱内 → 淡出;入水冻结;云内按 `CameraCloudFadeVal` 渐隐;上下地面切换 1 s 快速重入 |
| 8 | **雨声公式** | 机舱雨声 `音量 = Clamp01(weather − 2.25) × 云淡化`,`pitch = 1 + speed/100`;环境雨声 `音量 ∝ 1 / Clamp01(离地高度)^0.7`(带 −0.05 偏置) |
| 9 | **EVE 的可借鉴点** | 贴图**图集 UV 分格**做每粒子变体(`particleSheetCount`,上次 seed 已传进 shader 但**未用于 UV**);`randomDirectionStrength`(雨 0.5);`minCoverageThreshold` 与云覆盖联动;落地水花独立 sheet |
| 10 | **配置层与面板** | **已就位,重做时不用动**:`RainSection` 字段位、XML 节点、面板分组(雨组现为禁用占位)全部存在 |

⚠️ **不能照抄的一条**:SP2 的 `ParticleAmount` setter 在数量变化时 `FreeBuffers() + SetupBuffers()` **重建全部 GPU buffer** 并重新 `Dispatch(Randomize)`。SP2 从**天气事件(低频、不在渲染循环)**调用,所以安全;**本项目从每帧 `Tick` 调用** → 会在"上一帧绘制命令还没执行完"时释放它引用的 `GraphicsBuffer` → **未定义行为**。必须用第 2 条的容量/生效数量分离方案。

---

## 5. 分步实施计划(一步步来)

> **总原则:每个阶段独立可验收;上一阶段没有拿到"可信信号"就不进入下一阶段。**
> 参考教训 §7「重做起步清单」的前 3 步 —— 本文档把它的台阶铺满。

### 阶段 0:准备与护栏(不改代码)

- [ ] **0.1** 把 stash 里的旧实现**只读参考**取出看一眼(`%TEMP%\volken-rain-fog-stash`)—— 目的是**知道哪些路已经证伪**,不是复用代码。对照教训 §6「已证伪思路」清单逐条打勾。
- [ ] **0.2** 确认 `docs/archive/weather-rain-fog-postmortem-2026-09-27.md` 的 §5.1 铁律 1~5 与 §7 起步清单已读。
- [ ] **0.3** **接上诊断三件套**(教训 §5.3 / 计划 §10.4),**先于任何渲染代码**:
  - GPU 回读:`_ArgsBuffer[1]` = `instanceCount`,每 10 s 一次(阈值表见计划 §10.4 ④:`instanceCount == 0` → 问题在**剔除/参数环节**,不是绘制环节);
  - **屏幕空间**量:倾角、长宽比(⚠️ **禁止**用世界空间夹角当屏幕倾角 —— 这是上次失败的直接原因);
  - 内部状态心跳:`fade / activeCount / capacity / domainR(eff) / camSpd / occlusion / draws/s / everDrew`。
- [x] **0.4** 建目录 `Assets/Scripts/Volken/Weather/`(**命名空间已定:跟随现有代码用 `Volken.Weather`** —— 母计划 §10.2 ① 关于 `VolkenMod.Weather` 的旧注记,源于当时存在的一个全局 `Volken` 类(`Core/Volken.cs`,触发 CS0101);该类**早已删除**,现有 `VolkenWeather.cs`/`LightningModule.cs`/`LightningBolt.cs`/`WeatherPanel.cs` 全部用 `Volken.Weather` 且编译通过,故新文件沿用同一约定)。

**准出**:诊断能打出上述字段(先接空实现也认)。

### 阶段 1:最小 billboard 朝向验证(1~10 个粒子,**不碰 compute**)

> **这是整个计划最关键的一步**,目的是把"朝向数学"与"GPU 管线"**彻底解耦**验证 —— 上次失败的根源就是把两者混在一起调。

- [ ] **1.1** 用一个**普通** `Mesh`(8 顶点十字) + `Graphics.DrawMesh`/`RenderMeshInstanced`,**CPU 每帧算 1~10 个实例的位置**(位置可以是假的,比如固定域内随机点)。
- [ ] **1.2** 用 §3.1 的**屏幕平面构轴**算每实例的四边形顶点(`camRight/camUp` 来自当前相机)。
- [ ] **1.3** 让它**随相机可转**:设备上用 `volkenSetWeather` 或直接 dev 命令驱动,或用游戏内相机环绕。
- [ ] **1.4** 打屏幕空间诊断:每实例的**屏幕倾角**(0=垂直,90=横)与**长宽比**。

**准出(Boolean,不含糊)**:
- 相机**任意朝向**(含正仰视/正俯视/雨丝朝相机飞)**都不出现**"横块"**也**不出现"细长斜线拉飞";
- 屏幕倾角随视角变化**单调、连续**,无跳变、无径向爆散;
- 退化视角(气流 ∥ 视线)时**稳定表现为竖直下落**,不是随机方向。

**🔴 停止条件**:若连续 **3 轮**修正都拿不到"倾角/长宽比按预期变化"的可信信号 → **停下来换构轴方案**(例如改用纯屏幕空间 + 深度分层),**不许原地继续修**。

### 阶段 2:最小 compute 管线(无雨滴 shader,先证明数据流通)

- [x] **2.1** 手写 `RainParticles.compute`:先只写 `Randomize` + `Positioning` + `FillArgs` 三个 kernel,`RWByteAddressBuffer` 语义,自加 `_time`。
- [x] **2.2** 用一个**最简单的**着色器(哪怕 `Hidden/Internal-Colored` 变体)配合 `Graphics.RenderMeshIndirect` 把点画出来 —— 只要能看到"粒子在动"即可。
- [x] **2.3** 验证 `SV_InstanceID` 数据流(**计划自注未验的那条**):故意让位置 = `(_CulledPositions[i].x, 0, 0)`,`i` 从 0 递增 —— 屏幕上应看到**一排逐渐分开的点**。若全部重叠 → 实例号恒 0,**改回 `SV_InstanceID` 写法或检查 buffer 绑定**。(dev 命令 `volkenRainP2Row 1`;**待真机验证**)

**准出**:`instanceCount` 回读值 == 期望粒子数且随参数变化;屏幕上粒子位置与 buffer 内容**可预测地对应**;compute 无编译错误(`Editor.log` 搜 `Shader error` / `method 'InterlockedAdd'`,**按消息去重**)。
**🔴 停止条件**:`instanceCount` 恒等于容量(而非期望值)→ **`FillArgs` 没跑**,按 §3.3 表的次序先查编译错误。

### 阶段 3:BIRP 实例化雨滴 shader(正式版)

> **状态注记(2026-09-29):3.1~3.4 已实现(§10.9),待真机验证后逐个勾选。**
> 关键取舍:SP2 用 material `_RotationMatrix` 世界系构轴(已被阶段 1 实测证伪为"横块"),
> 阶段 3 保留屏幕平面构轴;软粒子深度采样 `CloudRenderer.LinearSceneDepth`(本工程唯一线性深度入口)。

- [ ] **3.1** 按 SP2 的**第一手 Properties 命名**写(计划 §10.2b ⑳ 的权威来源):`_MainTex`(**RGB 有效、A 是 alpha**)、`_Emission`、`_MainColor`(**注意不叫 `_Color`**)、`_InvFade`(软粒子因子,`Range(0.01, 3)`)。本项目上一版用的是自写名(`_Tex`/`_Color`/`_SoftParticleFade`),**若要对齐 SP2 便于对照,改这里**。
- [ ] **3.2** 接入阶段 1 验证过的构轴算法;软粒子用 `CloudRenderer` 的线性深度(经只读口 `LinearSceneDepth`)。
- [ ] **3.3** **UV 不平铺**(计划 §10.2b ⑬):长度已由基向量列表达,贴图纵向就是"头亮尾淡";再按拉伸平铺会在 `wrapMode = Clamp` 下夹边形成糊块。
- [ ] **3.4** 补上 §3.1「开放问题」的**随机 roll**(SP2 用矩阵第 3 列携带,改 2D 基后需显式补)。

**准出**:雨丝有长宽比、有软边、随速度拉伸;把 `_stretchAmount` 设为 0 时退化为**正方形点**(可作为"基向量是否正确"的快速判据)。

### 阶段 4:数量、密度、域半径标定(**照 §3.5 做,不照抄 SP2**)

- [ ] **4.1** 实现 `EffectiveDomainRadius` + `densityScale`,并**保证 Tick 顺序**正确(§3.5 顺序要求)。
- [ ] **4.2** 实现 `_capacity` / `_activeCount` 分离(§4 第 2 条)。
- [ ] **4.3** 非下雨区间 `amount = 0`(§4 第 3 条)。
- [ ] **4.4** 打 `Rain.Density` 诊断日志,直接报 **个/m³** 与**与 EVE 的比值**(计划 §10.2c2 建立的手段)—— 判断"够不够密"不靠肉眼。

**准出**:高速(≥300 m/s)下不闪断;密度达到 EVE 参照的合理比例;晴天 GPU 空转粒子数为 0。

### 阶段 5:接入天气状态机与相机相对坐标

- [ ] **5.1** 复用现有 `Weather/VolkenWeather.cs`(阈值读**自己的配置字段** `rain.triggerValue`,**不要**引入全局天气档位 —— `WeatherTypes` 已删除,见 README 决策速查「不要 SP2 的全局天气预设」)。
- [ ] **5.2** 相机相对坐标系 + **手工构造视锥平面**(§3.4c)。
- [ ] **5.3** 实例 `OnPreCull` 渲染回调(§3.4b);每次绘制前按当前相机重设 uniform(§3.4d)。
- [ ] **5.4** `_frameVel` 保持恒 0 并**写注释说明是刻意的**(§3.4c)。
- [ ] **5.5** 接入 CPU 侧淡出判据(AGL / 座舱 / 入水 / 云内),沿用 `PlanetConfig` 的 `RainSection` 字段。

**准出**:进出雨区淡入淡出正确(20 s / 2 s);俯仰/滚转/高速/时间加速下无朝向异常、无位置漂移;`ReferenceFrameRecentered` 后无错位。

### 阶段 6:方案 B 遮挡(可选,二期)

- [ ] **6.1** 256² 正交深度 RT + 深度专用相机/CommandBuffer。
- [ ] **6.2** **必做护栏**:`OcclusionEnabled && OcclusionDepthRendered` 双条件(§3.2),并给 `OcclusionDepthRendered` 加"深度确实渲过"的置位点与心跳。
- [ ] **6.3** 先在**深度图非空**的前提下验证剔除率合理,再打开。

**准出**:开启遮挡后雨**不消失**;低空穿建筑时雨丝被正确遮挡。
**🔴 停止条件**:一旦出现"雨整体消失且无报错" → **立即关闭遮挡回方案 A**(这就是 §3.2 那个护栏要防的场景)。

### 阶段 7:水花、雨声、图集变体(收尾)

- [ ] **7.1** 落地水花(独立 sheet / 独立 pass,不要并进雨丝 pass)。
- [ ] **7.2** 雨声:素材导入 + 导入设置(`forceToMono` / `CompressedInMemory` / `preloadAudioData`)+ **进 `_otherAssets`**(⚠️ 删资产**不会**让 GUID 自动消失,纳新资产也**不会**自动入包)→ 打包 → dev 命令 `volkenAssets` 复核台账。
- [ ] **7.3** 贴图图集 UV 分格(用已传进 shader 的 seed)。
- [ ] **7.4** 本地化文案(EN-US / ZH-CN / RU-RU,key 前缀 `Volken.UI.*`),并把雨组从「禁用占位」改为可用。

**准出**:雨声在游戏内可闻;`volkenAssets` 台账无 `MISSING` / 无悬空 GUID。

---

## 6. 判定与停止条件总表(把"信心耗尽"变成可控)

| 触发条件 | 处置 |
|---|---|
| 同一问题**连续 3 轮**修正拿不到可信的**屏幕空间**改善信号 | **停止,换方案**(教训 §5.4 第 25 条 —— 这是上次放弃的直接触发点) |
| 诊断量与"你关心的那个量"**不在同一个空间** | 立即改诊断,不继续改实现(上次失败的真正原因) |
| 出现"**能画出来但品红 + 掉帧**" | **先查 compute 编译错误**(`Editor.log` 去重),**不要先调参** |
| `instanceCount` 恒 == 容量 | 判定为 `FillArgs` 未执行 → 查编译错误 / buffer 绑定 / 原子计数 |
| 开启遮挡后**雨整体消失且无报错** | 判定为空深度图 → 关闭遮挡回方案 A,检查 `OcclusionDepthRendered` |
| 相机高速时雨**闪断** | 查**域半径 vs 相机速度**(停留时间),以及密度补偿是否生效 —— 两者都真实存在,别只修一个 |
| 雨**时有时无**(不是闪断) | 查**密度**(个/m³)与 EVE 比值,而不是继续查淡变(上次误判过) |

---

## 7. 与既有文档的关系(避免重复劳动与再次踩坑)

| 既有结论 | 本文档的处置 |
|---|---|
| 母计划 §8 第 2 条「SP2 与 Volken 同为 2022.3,`.asset` 大概率可直接用」 | **⚠️ 更正**:sp2d4 是 **6000.2.14f1** → 改为**手写 `.compute`**(§3.3) |
| 母计划 §3 表格「遮挡改 BIRP」 | 细化为**方案 A / 方案 B**,并写入空深度图护栏(§3.2) |
| 母计划 §6「素材清单(从 sp2d4 拷贝)」 | 保留可用部分(billboard 纹理 / 网格尺寸 / 雨声),但**shader 与 compute 不可拷**(§3.3) |
| 计划 §10.2b ⑰「buffer 容量与生效数量分离」 | **照抄**(§4 第 2 条),并补充"不能照抄 `ParticleAmount` setter"的原因 |
| 计划 §10.2c0/10.2c2(域半径自适应 + EVE 密度标定) | **照抄**,作为阶段 4 的验收基线(§3.5) |
| 教训 §3.1㈢ 的屏幕平面构轴骨架 | **采用**,并明确"该骨架尚未真机验证"→ **阶段 1 就是它的验证** |
| 教训 §3.1 的 27 条铁律 | 不再重述,阶段 0 逐条对照即可 |
| 配置层 / UI 占位 / 天气状态机 | **不动**(计划 §10.2f㊶ / §10.2h) |

---

## 8. 剩余开放问题(开工前不必解决,但要记住)

1. **随机 roll 的归位**(§3.1 开放问题):SP2 用矩阵第 3 列携带绕长轴滚转,改 2D 屏幕基后没有自然位置 → 阶段 3.4 显式补。
2. **`SV_InstanceID` 路径**(计划 §10.2b ⑫ 自注"推断,未真机验证")→ 阶段 2.3 验证。
3. **SR2 是否已有可复用的雨/粒子 API**:母计划 §10.2c A 已确认 `WindManager` / `WindVelocity` 在 `<JNO_CODE>` **零命中**、`IPlanetAtmosphereData` **无气流速度接口** → **风只能来自 `CloudConfig.windSpeed/windDirection`**。雨的"风"同理,需在阶段 4 接上。
4. **联机同步**:雨是否同步、由谁驱动 —— 暂定仅本机表现(母计划 §8 第 5 条)。
5. **性能边界**:480 万顶点/帧是本机 5060 Laptop 的上限;若目标机型更低,需在阶段 4 就下调上限。
6. **许可边界**:`ParticleDomain` / `ParticleHandler` 属 Jundroo;本计划只移植**逻辑与算法**,不搬运原 DLL 与商业资源文件;若雨声素材来自游戏资源,使用边界需自行确认(母计划 §8 第 1 条)。

---

## 9. 起步建议(若只做一件事)

> **做阶段 1。**
> 1~10 个粒子、CPU 算顶点、不碰 compute、不碰 shader —— 用最小的代价回答"屏幕平面构轴到底立不立得住"。
> 这一步通过,**后面 7 个阶段都是工程量**;这一步不通过,**后面 7 个阶段全是无效迭代**(上次 8 轮的教训)。

---

## 10. 实施记录(2026-09-28,阶段 0 + 阶段 1)

> 状态:✅ **阶段 0 完成、阶段 1 代码落地并编译通过(0 错误)**,**真机验证未做**(需进游戏跑 dev 命令)。
> 本次改动文件:`Assets/Scripts/Volken/Weather/RainAxisProbe.cs`(新增)、
> `Assets/Scripts/Volken/Core/VolkenMod.cs`(两处挂载)、`Assets/Scripts/Mod.cs`(5 条 dev 命令)、
> `Volken.csproj`(Compile 清单补新文件)。

### 10.1 阶段 0 实施情况

| 项 | 结果 |
|---|---|
| 0.1 旧实现只读参考 | ⚠️ stash(`%TEMP%\volken-rain-fog-stash`)**已被清理、不存在** → 改依据[教训文档 §6「已证伪思路清单」](archive/weather-rain-fog-postmortem-2026-09-27.md)登记(8 条已证伪路径,不再重复尝试) |
| 0.2 复盘必读 | ✅ 铁律 §5.1(1~8 坐标系/物理量)、§5.3 诊断、§7 起步清单均已过 |
| 0.3 诊断三件套 | ✅ 由探针承载:①内部状态心跳(`preCullCalls` 累计计数 + 每 1s 一行,含模式/气流/每粒子倾角与长宽比);②**屏幕空间**量(倾角 0=竖直 90=横、长宽比、退化分类"横块/细线/ok");③GPU 回读 —— 阶段 1 无 GPU buffer,留待阶段 2 接 `_ArgsBuffer[1]`(计划 §10.4 阈值表照用) |
| 0.4 目录/命名空间 | ✅ 目录已存在;命名空间**跟随现有代码 `Volken.Weather`**(见 §5 修正注记;母计划 §10.2 ① 的 `VolkenMod.Weather` 注记作废) |

### 10.2 阶段 1 实施情况(`RainAxisProbe.cs`)

**双算法对照绘制**(实例 `OnPreCull` + `Graphics.DrawMesh`,命令提交类 API,计划 §10.2b ⑪ 验证过的路径):

| 版本 | 颜色 | 算法 |
|---|---|---|
| A(本项目方案) | **青色** | 屏幕平面构轴:投影气流到 camRight/camUp → 退化显式兜底 `(0,-1)` → 长度轴 L 在屏幕平面 → 宽度轴 `W = cross(L, viewDir)` |
| B(SP2 原样) | **品红** | `AlignStreaks` 逐行复刻(世界空间长度轴 + `side = cross(up_or_fwd, fwd)`) |

每帧在相机前 8m 处排 1~10 个测试四边形(网格排布,版本 B 右移 1.5m 避免重叠),只量**屏幕空间**(铁律 16):
`tiltDeg`(0=竖直,90=横)、`aspect`(屏幕长/宽)、退化分类(长<0.1×宽→横块,宽<0.1×长→细线)。

**气流模式**(dev 命令 `volkenRainAxisMode`):0=竖直下落(行星径向×15) / 1=前下45° / 2=**朝相机(退化场景)** / 3=自定义(`volkenRainAxisVec x y z`) / 4=重力−相机速度(`VolkenWeather.CameraVelocity`)。

**dev 命令**(注册于 `Mod.RegisterCommands`):

```
volkenRainAxis             → 打印状态 + 每粒子 A/B 诊断(未挂载则尝试挂载)
volkenRainAxisOn 0|1       → 开关(默认 0:不改变现有画面)
volkenRainAxisMode 0..4    → 气流模式
volkenRainAxisVec x y z    → 自定义气流(切到模式 3)
volkenRainAxisCount 1..10  → 测试粒子数
```

**关键实现点**(避免重蹈上次覆辙):
- 全局调试开关全部 `static`(铁律 14);
- 诊断**只**量屏幕空间,不量世界空间夹角(铁律 16 —— 上次失败的真正原因);
- `OnPreCull` 用**实例**回调(静态 `Camera.onPreCull` 在本工程从不触发,计划 §10.2b ⑪);
- 心跳 1s 节流 + `preCullCalls` 累计计数(铁律 17/18:能区分"没触发"与"触发了被重置");
- 材质用内置 `Unlit/Color`(双材质青/品红),**不需要新增 shader 文件** → 不碰打包清单;
- 全部 try/catch + `LogThrottled`,异常不炸帧。

**编译验证**:`dotnet build Volken.csproj` → **0 错误**(5 个警告全为既有文件,`ForceSetting.cs`/`PlanetRingsZWriteFix.cs`/程序集版本冲突);csproj Compile 清单与磁盘 .cs **双向零孤儿**(不存在"编辑器看得到但编译清单漏掉"的文件)。

**⚠️ 环境注记(与本改动无关)**:写代码时 Unity 编辑器正处于**陈旧编译状态** —— 它的 AssetDatabase 里还缓存着一个**磁盘上已删除**的 `HarmonyPatches/AnotherPatch.cs`(引用不存在的 `Volken.Debug.LogError`)→ Volken 程序集编译失败 → 大量 `VolkenMod` 缺失的 CS0234 级联错误,`Library/ScriptAssemblies/Volken.dll` 停留在 15:35:59 的旧版本。**处置:在 Unity 里聚焦窗口 / Ctrl+R 刷新(或重启编辑器),让 AssetDatabase 丢掉陈旧文件后重编译即可**;磁盘当前状态经 dotnet 验证是干净的。另:`LightningModule.cs` 有一处**非本次改动**的未提交注释删除(并行工作痕迹),不影响编译。

### 10.3 待真机验证(阶段 1 准出,逐条打勾)

> 进游戏 → `volkenRainAxisOn 1` → 按下面顺序测。青色 = 版本 A(本项目方案),品红 = 版本 B(SP2)。

- [x] **① 常规视角**:相机平视/俯视/仰视,模式 0/1 —— A 的四边形保持竖长条,倾角随视角单调连续,**无横块、无细线、无径向爆散**;
- [x] **② 退化视角(核心)**:模式 2(雨朝相机飞)—— A 应显示**竖直细长条**(倾角≈0,长宽比≈25),而 B 应复现**横块**(倾角≈90,长宽比≈0)→ **"屏幕平面构轴立得住"成立**;
- [x] **③ 自定义气流**:模式 3 + `volkenRainAxisVec 5 -15 3` —— A 的四边形朝屏幕内该方向倾斜,倾角/长宽比与手算一致;
- [ ] **④ 相机运动**:模式 4,移动相机 —— A 的朝向随"重力−相机速度"平滑变化,不跳变;(本次会话未测)
- [x] **⑤ 心跳**:`volkenRainAxis` 输出中 `preCullCalls` 持续增长(≈20/s),每粒子 A/B 两行数字与屏幕所见一致。
- [ ] **⑥ 开关幂等**:`volkenRainAxisOn 0` 后画面恢复原样(无残留)。(本次会话未测)

> 全部通过 → 阶段 1 准出成立,**阶段 2(最小 compute 管线)开工**。
> 若 ② 中 A 出现任何退化形态 → **停止**,按停止条件换构轴方案,不要原地修。

### 10.4 真机实测记录(2026-09-29,首次在游戏内验证)

**一句话:阶段 1 的核心问题——"屏幕平面构轴到底立不立得住"——已被实测回答:立得住,而且 SP2 原样(版本 B)在同一视角复现了旧失败的"横块"。** 屏幕平面构轴 vs SP2 世界空间长度轴的优劣对比不再需要推导,直接有数据。

**实测数据摘要**(摘自当次 `Player.log`):

| 模式 | 版本 A(青色,屏幕平面构轴) | 版本 B(品红,SP2 原样) | 结论 |
|---|---|---|---|
| 0 竖直下落(用户确认"正常的向下") | `倾角 0.0° / 长宽比 25.0 / ok` | `倾角 0.0° / 长宽比 25~35 / ok` | 两者都正常竖条 ✓ |
| **2 朝相机(退化视角)** | **`倾角 0.0° / 长宽比 25.0 / ok` —— 不退化** | **`倾角 90.0° / 长宽比 0.0 / 横块!` —— 复现旧失败** | **核心结论成立** ✓✓ |
| 3 自定义 `(5,-15,3)` | 倾角随相机旋转连续变化(61.7°→83.1°→89.1°→21.1°…),长宽比恒 25 | 长宽比随相机变化(10.4→18.6→12.4→1.5→7.1) | A 屏幕稳定性优于 B ✓ |

**验证链完整跑通**:命令注册 → `AttachToCurrentView via NearCamera` → 材质创建(Unlit/Color, queue=5000) → 挂载到 `NearCamera`(active=True, ortho=False, fov=20) → `preCullCalls` 从 1 涨到 3700+ → `first draw` 与心跳日志逐帧输出。**上次"啥都看不见"的原因**:探针默认关(`Enabled=false`)+ 旧版 `volkenRainAxisOn` 不挂载 + 挂载失败静默 —— 本轮已修复(开关自动挂载、全链路日志、材质置顶 `ZTest Always`)。

**同时修掉的诊断误报**:退化分类阈值原来 `wid<0.1*len`(长宽比>10 即"细线!")会把**正常雨丝(设计长宽比 25)全部误标**;已改为 `长宽比<0.2 → 横块、>50 → 细线`,25 落在 ok。旧失败的 175 与模式 2 的 0.0 仍能正确判出。

**未测项(下一轮补)**:④ 模式 4(重力−相机速度)的平滑性;⑥ `volkenRainAxisOn 0` 无残留。

**阶段 1 准出评估**:①②③⑤ 已过、④⑥ 为低风险收尾项;**核心退化验证(②)通过意味着阶段 2(最小 compute 管线)可以开工**,④⑥ 可在阶段 2 开发间隙补测。

### 10.5 实施记录(2026-09-29,阶段 2:最小 compute 管线)

> 状态:✅ **阶段 2 代码落地并编译通过(0 错误)**,**待真机验证**(SV_InstanceID 路径 2.3)。
> 本次改动文件:
> - 新增 `Assets/Scripts/Volken/Weather/RainParticles.compute`(Randomize+Positioning+FillArgs,RWByteAddressBuffer 原子加,自加 `_time`);
> - 新增 `Assets/Scripts/Volken/Weather/RainParticles.shader`(最小实例化 unlit,顶点按 `SV_InstanceID` 读 `_Positions`);
> - 新增 `Assets/Scripts/Volken/Weather/RainParticles.cs`(驱动器:挂载/心跳/GPU 回读/缓冲管理);
> - 改动 `Assets/ModData.asset`(`_otherAssets` +2:GUID `9c3f6a2e…` compute / `1a2b3c4d…` shader);
> - 改动 `VolkenMod.cs`(两处挂载)、`Mod.cs`(4 条命令)、`Volken.csproj`。

**实现要点(与 SP2 的差异记录)**:
1. **三个 kernel 手写**(SP2 6 个中先做 3 个,剔除留给阶段 6);
   - ⚠️ **原子加铁律(2026-09-29 真机编译错误实锤,纠正旧结论)**:本游戏编译器**没有** `RWByteAddressBuffer` 的 4 参 `InterlockedAdd`(Editor.log 报 "no matching 4 parameter intrinsic function;Possible intrinsic functions are: InterlockedAdd(int|uint, uint) InterlockedAdd(int|uint, uint, out int|uint original)");
     必须用**带下标的 `RWStructuredBuffer<uint>` 2/3 参形式**:`InterlockedAdd(_ArgsBuffer[1], 1, original)`。
     旧结论"RWStructuredBuffer<uint> 无 InterlockedAdd、必须用 RWByteAddressBuffer"是**错的**(见 §10.5 修复记录);
   - ⚠️ **一个 kernel 编译失败会毒掉整个 compute**(所有 kernel 全部 "Kernel at index N is invalid"),
     且报错只在**导入期 Editor.log**(运行时只刷 "Kernel at index N is invalid" 无细节)→ 打包前必须先聚焦 Unity 看控制台;
2. **`FillArgs` 复位在 CPU**(每帧 `SetData` 5×uint)——阶段 2 无剔除,跨 threadgroup 复位不可靠;`FillArgs` 仍由 GPU 原子统计 `instanceCount`(保住了"instanceCount==0 → 问题在 args 环节"的诊断语义);
3. **容量/活跃数分离先建好**:`_capacity`(buffer 大小,固定)vs `_amount`(每帧活跃数);阶段 2 `amount = capacity`,非测试时阶段 4 才引入 `amount=0` 晴天空转;
4. **SV_InstanceID 路径(shader)** = 计划 §10.2b ⑫ 自注"推断,未真机验证"那条 —— 阶段 2.3 用 `volkenRainP2Row 1` 排测试在真机验;
5. **两个关键坑已预先堵住**:
   - `RenderMeshIndirect` 参数要 **`GraphicsBuffer`**(不是 `ComputeBuffer`)→ `_args` 用 `GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint))`;
   - **shader 的 `_Positions` 必须绑到材质**(`_mat.SetBuffer`),RenderMeshIndirect 不会自动把 compute 的 buffer 带给材质 —— 不绑 → 全部画在原点;
   - `ComputeShader.SetFloat/SetInt/SetVector` 无 per-kernel 重载(全局共享),不传 kernel 参数;
6. `worldBounds` 每帧罩住相机(200m 盒)否则被视锥剔除;layer=0(相机 cullingMask `E4002011` 含 layer 0,探针已验证可画)。

**dev 命令**:
```
volkenRainP2            → 状态(资产/容量/回读 instanceCount)
volkenRainP2On 0|1      → 开关(默认 0)
volkenRainP2Row 0|1     → 阶段 2.3:相机前等距排(20 粒)验证 SV_InstanceID
volkenRainP2Cap n       → 容量(重建 buffer,默认 10000)
```

**待真机验证(阶段 2 准出)**:
- [ ] ① `volkenRainP2On 1` → 屏幕出现青色十字雨丝在动(下落+域环绕);
- [ ] ② 心跳日志 `reads/s`、`readback instanceCount` == 期望且随参数变化;
- [ ] ③ `volkenRainP2Row 1` → 相机前一排等距点(核心:SV_InstanceID 数据流);
- [ ] ④ `Editor.log`/控制台无 `Shader error` / `InterlockedAdd` 相关编译错误;
- [ ] ⑤ 若全重叠在一点 → SV_InstanceID 恒 0,改路径或查 buffer 绑定。

> 注意:需要**重打包**(新增两个 bundle 资产 → `_otherAssets` 已加,重建后生效);打包前让 Unity 聚焦刷新导入新 `.compute`/`.shader` 并确认无 shader 编译错误。

### 10.6 阶段 2 真机第一轮(2026-09-29):编译错误修复记录

**现象**:`volkenRainP2On 1` 后屏幕无雨,日志每帧刷 `RainParticles.compute: Kernel at index (1)/(2) is invalid`,心跳 `readback=0`(instanceCount 恒 0,FillArgs 从未执行),`draws` 在涨(间接绘制在发,但 0 实例)。

**根因(Editor.log 导入期实锤)**:
```
Shader error in 'RainParticles': 'InterlockedAdd': no matching 4 parameter intrinsic function;
Possible intrinsic functions are: InterlockedAdd(int|uint, uint)
InterlockedAdd(int|uint, uint, out int|uint original) at kernel FillArgs at RainParticles.compute(116) (on d3d11/glcore/vulkan/gles3)
```
1. **本游戏编译器没有 RWByteAddressBuffer 的 4 参 InterlockedAdd** —— 我最初按计划旧结论"RWStructuredBuffer<uint> 无 InterlockedAdd、必须用 RWByteAddressBuffer"写了 `InterlockedAdd(_ArgsBuffer, 4, 1, original)`,编译直接挂;
2. **一个 kernel 编译失败 → 整个 compute 所有 kernel 全部 invalid**(Randomize 也只在首帧报一次,因它只 dispatch 一次);
3. 运行时日志只有 "Kernel at index (N) is invalid" 无编译细节,必须看导入期 Editor.log。

**修复**(§10.5 文件已更新):
- `RWByteAddressBuffer _ArgsBuffer` → `RWStructuredBuffer<uint> _ArgsBuffer`;
- `InterlockedAdd(_ArgsBuffer, 4, 1, original)` → `InterlockedAdd(_ArgsBuffer[1], 1, original)`(带下标的 3 参形式,编译器明确列出的可用 intrinsic);
- C# 侧 `_args` 仍是 `GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint))`(RenderMeshIndirect 参数),compute 侧按 RWStructuredBuffer<uint>(stride 4) 绑定,同一块内存,无改动。

**教训(写进铁律)**:
- 本工程 compute 原子加**只有** `RWStructuredBuffer<uint>` 下标式 2/3 参形式;旧结论作废;
- compute 改完 → **先聚焦 Unity 看控制台有无 "Shader error in ... (on d3d11)"**,确认干净再重打包;
- 真机报 "Kernel at index (N) is invalid" = 编译失败,不是运行逻辑问题,回 Editor.log 找根因。

### 10.7 阶段 2 真机第二轮(2026-09-29):横雨 + 排测试观感

**本轮实测通过项(准出 ①② 已过)**:
- 编译干净(用户确认无报错);
- `readback: indexCount=12 instanceCount=10000` —— **FillArgs 原子加跑通,instanceCount == 期望**;
- 心跳 draws 稳步涨、无异常;等距排模式 `amount=20 testRow=True` 生效。

**现象 1"雨的方向是横" —— 根因:相机速度,不是 bug**:
- 心跳 `camSpd` 飞行时 140~170 m/s,雨下落仅 15 m/s;雨位置是世界系,相机高速飞 → 相对相机雨呈 170 m/s 横向拖影。
- 这正是计划阶段 5(相机系存储 + 相机速度补偿)解决的问题;阶段 2 先做**简化补偿**:
  `Positioning` 改为 `p.xyz += (float3(0,-_fallSpeed,0) + _camVel) * _dt;`(C# 算相机速度传 `_camVel`)。
  推导:相对相机位移 r=p-c,要让 r 每帧加 15m/s 下落 → p += (camVel + 下落)*dt。云锁在相机周围、相对相机竖直下落。
- 注意推导里是 **+_camVel**(不是减)—— 减会把雨甩到相机后面(那就是"没补偿")。
- 阶段 5 仍要做相机系存储(浮点精度),此处是数学等价的前置版。

**现象 2"volkenRainP2Row 1 后雨沾在摄像机上" —— 设计使然,且是 SV_InstanceID 已通的证据**:
- 等距排**本来就是相机系锁定**(每帧用 camPos+cam基 重算),"沾在摄像机上"就是它的工作方式;
- 用户看到的是"雨"(多条)不是"一个点" → 实例号不是恒 0,**SV_InstanceID 路径已通**;
- 观感改进:排从 6m/间距1.5m(30m 宽,fov20° 只露 2~3 根)改为 **30m/间距3m**(可见 6~7 根、间隔 5.7°,肉眼可数);
- 新增 **GPU 位置回读**:每 10s(或 `volkenRainP2Row` 触发即读)回读 `_positions[0..2]`,log 三个世界坐标;
  等距排模式下应看到 **x 每索引 +3m** —— 数据实锤 "compute 写入正确 + shader 按实例号读到正确位置"。

**再测命令**(重打包后):`volkenRainP2On 1`(应见青色雨丝**竖直**下落、云跟着相机)→ `volkenRainP2Row 1`(30m 外 6~7 根竖条)→ 日志应出现 `pos[0..2]` x 递增 +3m。

**第三轮(同日):2.3 准出通过 + "横"的真正根因=相机倾斜(雨丝轴)**
- **2.3 等距排实测通过**:`pos[0..2]: (-23.0,-15.4,-9.7)(-20.6,-13.9,-8.7)(-18.2,-12.4,-7.8)` —— 相邻差模长恰 3.0m、方向=camRight(相机右),`readback=20`。compute 写入对 + shader 按实例号读到对,**SV_InstanceID 数据流 100% 实锤**;
- **"雨还是横的"根因(与"运动方向"无关,是"雨丝朝向")**:飞行视角相机倾斜(camRight ≈ (0.8,0.5,0.33)),雨丝网格是**世界系竖直**轴(网格局部 Y=世界 Y,无旋转)→ 世界竖直在倾斜相机下投影成斜/横条。这与 Phase 1 实测完全同构:**Version A 屏幕平面构轴 = 竖条,Version B 世界系轴 = 横块** —— 阶段 2 的绘制把"世界系竖直"又复刻了一遍 Version B;
- **修复 = 把 Phase 1 Version A 搬进 shader**:顶点着色器里做屏幕平面构轴
  `axis = normalize(down - camFwd*dot(down,camFwd))`(世界下落投影到屏幕平面;退化时兜底 camUp),
  `side1 = cross(camFwd, axis)`(屏幕横轴),`side2 = cross(axis, side1)`(薄轴),
  局部顶点 `local = axis*y + side1*x + side2*z` → 雨丝在屏幕上恒竖条,相机怎么倾斜都竖;
- C# 每帧把 `_CamFwd/_CamUp` 传材质(`_mat.SetVector`);
- 顺带:去掉 `volkenRainP2Row` 的"即读回读"(与 GPU 竞态,读到旧帧散点误导;10s 心跳回读为准)。

**阶段 2 准出状态**:① 有雨可见 ✅(竖条问题本轮修复后待确认)、② instanceCount 回读==期望且随参数变 ✅(10000/20 都对)、③ SV_InstanceID 等距排 ✅(pos 回读实锤)、④ 无编译错误 ✅、⑤ 无全重叠 ✅。**等待用户确认雨丝竖直后,阶段 2 收官 → 阶段 3(正式 BIRP 雨滴 shader,本 shader 就是它的骨架)。**

### 10.8 坐标系实锤(2026-09-29,参考 jnoCode 源码)—— 雨下落方向必须用径向,不是世界 -Y

**自查发现两个被忽略的坐标系事实(jnoCode: `Flight\GameView\ReferenceFrame.cs` / `CraftScript.GravityNormal`)**,已修正阶段 2 全部三处:

1. **世界(帧)空间 = 浮点原点 + 绕行星 Y 轴旋转**:
   - `ReferenceFrame` 有 `_rotation = Quaternion.Euler(0f, RotationAngle, 0f)`(只绕 Y 偏航)+ 平移重定心(玩家远离原点后 `ReferenceFrameRecentered` 事件整体平移);
   - 相机/飞行器 transform、物理都是**帧空间**;世界 Y = 行星自转轴,**不是**行星"当地上方向";
   - 浮点原点:我的世界系位置在重定心后与游戏内 Transform 不一致(游戏平移了所有 Transform,我的 compute buffer 不会)→ 靠 50m 域环绕下一帧全部拉回(能撑住,阶段 5 相机系存储才是精度正解)。

2. **重力径向 → "下" = 指向行星中心,不是世界 (0,-1,0)**:
   - `CraftScript.GravityNormal => _flightData.GravityFrameNormalized`(帧空间、归一化);
   - 符号实锤:`CraftScript L2162 _terrainAlignedTransform.position = FramePosition + Altitude * GravityNormal`(把地形对齐变换放到地表点)→ **GravityNormal 指向行星中心 = 雨的下落方向**;EVA/碰撞用法里的 `-GravityNormal` = "上"一致;
   - 球面赤道处径向"下"≈世界水平 → 写死世界 (0,-1,0) 在大多数位置都下错方向(之前"看着正常"是测试位置恰好世界-Y≈径向)。

**修正(三处全用 `_downDir/_DownDir = craft.GravityNormal`,无飞行器时兜底世界 down)**:
- compute `Positioning`:`p += (_downDir*_fallSpeed + _camVel)*_dt`(替换写死的 `(0,-1,0)`);
- shader 屏幕平面构轴:`axis = _DownDir - camFwd*dot(_DownDir, camFwd)`(替换写死的 down);
- C#:`down = Game.Instance.FlightScene.CraftNode.CraftScript.GravityNormal`(try/catch + 零向量兜底防 NaN),每帧传 compute 与材质。

**铁律(写进阶段 3/5)**:雨的一切"下/重力"方向都用 `craft.GravityNormal`;世界 (0,-1,0) 只在极区附近近似成立。阶段 3 的每粒子速度、阶段 5 的相机系存储都要遵守"帧空间 + 径向"。

### 10.9 阶段 3 实施记录(2026-09-29)—— 正式 BIRP 雨滴 shader 落地

**改动文件**(dotnet build = **0 错误**;5 条既有警告未新增):
- `RainParticles.shader` —— 阶段 2 骨架上换正式版(见文件头注释):
  - **3.1 SP2 第一手 Properties**:`_MainTex`(RGB+A)、`_Emission`(Range 0..3)、`_MainColor`(不叫 `_Color`)、`_InvFade`(Range 0.01..3);另加 material 侧 `_Falloff`(尾淡)、`_FadeAmount`(全局 alpha,阶段 5 淡入淡出走这里)、`_Stretch`(随速度拉伸,每帧由 C# 算);
  - **3.2 软粒子**:采样 `_LinearSceneDepth`(= `CloudRenderer.LinearSceneDepth` = `combinedDepthTex`,RFloat,**LinearEyeDepth 米**,按相机输出尺寸创建、屏幕 UV 对齐)。frag 里 `col.a *= saturate((sceneDepth - eyeDepth) * _InvFade)`;C# 深度图未就绪时置 `_InvFade=0` → shader 跳过采样退化为普通透明(兜底,不崩);
  - **3.3 UV 不平铺**:网格 UV 全 0..1,长度由基向量列表达;uv.y 0=头(亮)→1=尾(淡),`col.a *= 1-saturate(uv.y*_Falloff)` 头亮尾淡,Clamp 不夹边;
  - **3.4 随机滚转角**:SP2 用矩阵第 3 列携带,改 2D 屏幕基后无自然位置 → 由每粒子 seed(`_RandomData.x`)派生 `roll=frac(seed*1.3247)*2π`,绕雨丝轴旋转十字截面;
  - **构轴仍用屏幕平面 Version A**(SP2 的 `_RotationMatrix` 世界系构轴已被阶段 1 证伪),`axis = _DownDir - camFwd*dot(_DownDir,camFwd)`,退化兜底 `_CamUp`;
  - 拉伸沿屏幕平面轴:`local = axis*(vertex.y*_Stretch) + s1*x + s2*z`。
- `RainParticles.cs`:
  - `BuildMesh` 补 **UV 数组**(此前网格无 UV,tex2D 恒采 (0,0) 会全灭/全亮);
  - 新增静态参数与命令:`volkenRainP2Stretch n`(→ `StretchAmount`,默认 0.045)、`volkenRainP2Soft n`(→ `InvFade`,默认 1;0=关软粒子);
  - `Tick`:算**相机系相对速度** `streakSpeed = |_downDir*_fallSpeed + camVel|` → `stretch = clamp(streakSpeed*_StretchAmount, 1, 3.5)` 传 `_Stretch`(SP2 `_stretchAmount/_stretchLimit` 语义);深度图解析(`_cam.GetComponent<CloudRenderer>()?.LinearSceneDepth`)→ SetTexture + SetFloat `_InvFade`;
  - `EnsureBuffers` 补绑 `_RandomData` 到材质 + **资产未就绪时静默返回**(修复场景切换瞬间 `Awake→EnsureBuffers` 的 NRE,日志 L883 那处);
  - 心跳加 `stretch=` / `soft=`(soft=off = 深度图未就绪,`soft=1.0` = 软粒子开)。
- `Mod.cs`:注册 `volkenRainP2Stretch` / `volkenRainP2Soft`(float),注册确认日志已更新。

**阶段 3 准出判读(真机)**:雨丝有长宽比(0.1×2.5 十字)、有软边(尾淡渐变 + 软粒子)、随速度拉伸(心跳 `stretch` 高速 >1);
- `soft=off` → 检查云渲染器是否挂在本相机(CloudRenderer 挂在 NearCamera,雨也挂 NearCamera,应能取到);
- 拉伸不生效 → `camSpd` 太小(streakSpeed<22.2 时 stretch clamp 到 1 属正常,低速不拉长是 SP2 语义);
- 软粒子异常(雨在楼后透出/闪烁)→ 深度是上一帧的(OnPreCull 在 OnRenderImage 之前),移动物体边缘可接受;严重时 `volkenRainP2Soft 0` 关掉对比。

**再测命令**(重打包后):`volkenRainP2On 1`(雨丝竖 + 软边)→ 飞行看 `stretch>1` → `volkenRainP2Soft 0` 对比硬边 → 心跳确认 `soft=` 状态。

**阶段 2 收官注记**:阶段 2 准出 ① 由用户确认(雨丝竖直,含 14:24 日志管线全绿 + L977 末次开雨);阶段 3 已直接在其上落地,阶段 2 的收尾以阶段 3 真机通过为最终确认。

### 10.10 阶段 3 第一轮真机(2026-09-29):"竖线贴在摄像机上"根因 = 云坍缩到相机 + 雨丝不编码相对运动

**本轮日志事实(用户反馈"竖线贴在摄像机上")**:
- 管线全绿(assets ok / readback=10000 / stretch 随速 / soft=1.0→0 切换正常 / 无错误);
- **铁证 1:pos 回读相对位置冻结** —— L771 与 L791(相隔 ~20s)的 `pos[0..2]` 相对偏移**逐字节相同**(p1−p0 两次都精确 = (−0.8,1.8,−3.6)),但绝对坐标从 (173,312,−673) 变到 (113,520,314) → 云跟着相机**刚性平移**(camVel 锁在工作),粒子间**相对运动(下落)没有发生**;
- **铁证 2:pos 三粒簇在 4m 内**(10000 粒的均匀球里 3 个随机点相距 <4m 的概率 ≈ 0)→ 整团云**坍缩在相机附近**;
- **铁证 3**:camSpd 在停泊后恒等于 423.8 冻结(OrbitDiag speed 已 0.0)→ 停泊/暂停时 `dt≤1e-4` 后 camVel 不再重算(这本身无害,停泊雨本来就不动)。

**根因(两层)**:
1. **回卷坍缩(主)**:旧 `frac(dist/R)*R` 回卷把从边界缓慢飘出的粒子回收到 `frac(≈1.0)≈0` 的近中心处 → 高速飞行 ~3s 内整团云被"吸"到相机上 → 之后相对位置冻结、雨糊在镜头前 = "贴在摄像机上"。修复:**回卷改随机重生在球内**(`RandomUnitVector(seed+ph*7.13)*Hash11(seed+ph*3.7)*R`,相位 `ph=floor(_time*20)` 保证每次重生位置稳定不抖),密度均匀、无坍缩;
2. **雨丝轴不编码相对运动(次)**:轴恒取屏幕竖直投影,静止=竖条、高速也不变 → 即使下落在工作,视觉上也没有"雨在往后飘"。修复:**按 SP2 `AlignStreaks(_combinedVel+_frameVel−_spectatorVel)` 语义加流线轴** —— 轴 = 相对速度 `down*_fallSpeed − camVel` 的屏幕平面投影(静止退化为屏幕竖直,与旧路径一致;高速呈流线斜向),由 `_StreamMode` 切换(`volkenRainP2Stream 1|0`,默认 1)。

**本轮改动**:
- `RainParticles.compute` Positioning:回卷改随机重生;compute 内加 down 防御(`!(dot>1e-6)` → 世界下,对 NaN 也成立,不用 isnan);
- `RainParticles.shader`:加 `_RelVel`/`_StreamMode`,轴 = `_StreamMode>0.5 ? 相对速度屏幕投影 : 恒竖直`;拉伸沿轴不变;
- `RainParticles.cs`:传 `_RelVel = down*FallSpeed − camVel`、`_StreamMode`;拉伸改用 `|relVel|`;**诊断三件套**:心跳加 `dt=`/`down=(x,y,z)`/`stream=`,pos 回读加 `pos0Δ=`(上次回读 pos[0] 的位移 —— 正常下落 10s 应 ≈150m 或回卷后相位变化,恒 0 = 下落没积分,先查 dt/down);
- `Mod.cs`:注册 `volkenRainP2Stream`。

**再测命令**(重打包后):`volkenRainP2On 1`(静止应见**竖直雨丝在下落**)→ 飞行看**流线斜向**(`stream=1` 默认)→ `volkenRainP2Stream 0` 对比恒竖直 → 日志看 `dt=`(是否暂停)、`down=`(GravityNormal 是否正常)、`pos0Δ=`(**关键:10s 应 >100m**)。

**判读**:`pos0Δ` 大 = 下落/回卷在跑,贴相机根因已除;`pos0Δ≈0` 且 `dt>0` 且 `down` 正常 → 回卷没触发(粒子都在域内不出去),再查域半径/数量级。

### 10.11 阶段 3 第二轮真机(2026-09-29):"雨是一团+位置不动"根因 = Positioning 读未绑定 buffer 整段空转

**本轮用户反馈**:
1. `volkenRainP2On 1` 时雨是一团,位置没有动,没有跟着 craft/摄像机;
2. 暂停时会丢失方向,重新变成竖直向下。

**日志铁证(新包确认在跑:dt/down/pos0Δ/stream 字段齐全,L1049 切了 stream=0)**:
- **`pos[0..2]` 从 L790 到 L1188 全程逐字节相同、`pos0Δ=0.0` 恒 0** —— 粒子冻死在初始随机位;
- 关键:连 **camVel 锁也死了**(上一轮 camVel 锁是活的、云跟着相机),而 `dt=0.02~0.03`、`camSpd=106~168`、`down=(0.20,-0.43,0.88)`(归一化正常)全是活的 → **Positioning kernel 整段没有执行**;
- FillArgs 正常(`readback=10000`)、无 "Kernel at index invalid" 刷屏 → compute 编译通过,只有一条警告:
- **L786 `Compute shader (RainParticles): Property (_RandomData) at kernel index (1) is not set`** —— 我上一轮在 Positioning 的随机重生里加了 `_RandomData[id.x].x`,但 `_RandomData` 只绑给了 Randomize(0) kernel,Positioning(1) 读的是**未绑定 buffer** → 本平台驱动让 kernel 空转(读 0 之外整个 kernel 被废)。

**根因**:**compute kernel 里读未绑定的 StructuredBuffer = 整段空转**(本轮实锤);上一轮(§10.10)的 Positioning 不读 `_RandomData`,所以 fall+camVel 是活的,这轮一加读就全死。

**修复**:
- compute Positioning:**不再读 `_RandomData`** —— seed 直接用 `(float)id.x*7.13f+1.0f`(与 Randomize 同公式,每粒子稳定);随机重生逻辑不变;
- C# Tick:防御性把 `_RandomData` 也绑到 `_kernelPositioning`(双保险,消掉 L786 警告);
- **执行探针**:Positioning 里 `p.w = _time;`(写回时间戳,shader 只用 xyz 不受影响),回读日志加 `w0=` —— **下一轮直接可见 kernel 跑没跑**(w0 随帧变 = 在跑;w0 恒不变 = 还在空转);
- **暂停方向修复(问题 2)**:`_lastCamVel` 锁存 —— `dt≤1e-4`(暂停)时把**最近一次测得的速度**传给 `_camVel`/`_RelVel`,流线轴保持飞行方向,不再跳回竖直;compute 侧 dt=0 乘 0 无影响。

**再测命令**(重打包后):`volkenRainP2On 1` → 静止看雨丝下落、飞行看流线 → **暂停**看方向是否保持 → 日志看 **`w0=`(关键:应随帧变)**、`pos0Δ`(应 >100m)、心跳应**不再有 `Property (_RandomData) ... not set`**。

**判读**:`w0` 随帧变 + `pos0Δ>100` = Positioning 复活,问题 1 根除;`w0` 仍恒不变 = 不是未绑定读的问题,下一步查 kernel 索引/打包(回 Editor.log 看导入期 compute 编译)。

### 10.12 阶段 3 第三轮真机(2026-09-29):"朝向随摄像机角速度变"根因 = 流线轴屏幕投影随视角摆动;§10.11"kernel 空转"系误诊

**本轮用户反馈**:
1. 雨粗的和面条一样(小问题,丢给 UI 调整 —— 粗细=_StreakThickness 0.1、长度=速度拉伸,阶段 4 面板化);
2. **重点**:雨的朝向随摄像机移动朝向的角速度改变。

**日志铁证(w0 探针首次立功,推翻 §10.11 结论)**:
- L787 `w0=153.3` → L805 `w0=162.9` **`pos0Δ=60.7`** → L824 `w0=164.5` **`pos0Δ=27.0`**:前 3 次回读 **kernel 活着、位置在动**(下落+锁都在工作);
- L843 起 `w0=164.5` 恒不变、`pos0Δ=0`:与 `Time.time` 恰好冻结在 164.5 同步 —— **游戏暂停了**,不是 kernel 死;
- §10.11 的"雨一团不动"同一解释:§10.10 日志里 L790 起 dt=0.0000 的心跳 = 用户开启后立即暂停;§10.11 移除 `_RandomData` 读取、防御性绑定、w0 探针均为**无害但非必需**(L786 警告本身不致命)。

**真根因(重点)**:流线轴(stream=1)取 `_RelVel`(世界系固定向量)的**屏幕平面投影**,暂停/转镜头时投影随视角旋转 → 雨丝朝向跟着摄像机角速度摆。用户要的是稳定朝向。

**修复**:
- **默认改回恒屏幕竖直**(`StreamMode=false`,即 shader 走 downAxis 路径):雨丝在屏幕上**永远竖直**,无论怎么转镜头都不摆 —— 与用户历轮要求一致("雨是横的不是竖的"→ 竖线);
- **流线轴/拉伸的速度源改飞行器本体速度** `CraftScript.FrameVelocity`(帧空间,纯平移,无视角旋转切向分量;SP2 用相机速度在 SR2 成立是因为其相机锁飞行器平移,我们直接用本体速度更稳):stream=1 选项与拉伸长度均不再随摄像机角速度抖动;无飞行器(深空/观察者)回退 camVel;NaN 兜底 `IsFinite`;
- 心跳加 `craftSpd=`(速度源大小)验证取值。

**再测命令**(重打包后):`volkenRainP2On 1` → **暂停状态下旋转镜头:雨丝应恒竖直、不摆**(本轮验收点)→ 飞行看拉伸随 `craftSpd` 稳定(不随转头抖)→ 可选 `volkenRainP2Stream 1` 对比流线模式。日志看 `craftSpd=` 是否随飞行变化、`w0=`/`pos0Δ=` 在**非暂停**时是否在动(暂停时冻结是正常现象,勿当 bug)。

### 10.13 阶段 3 第四轮真机(2026-09-29):回 SP2 第一手实现,废弃屏幕平面构轴 → 世界系旋转矩阵

**本轮用户反馈**:
1. 垂直往下看的时候会糊在屏幕上;
2. 雨像是贴在了摄像机上而不是独立物品,会随着转动、zoom 变化;
3. 再去看一眼 SP2 咋做的(`<SP2_D4>`,AssetRipper 导出)。

**SP2 第一手实锤(sp2d4 + `rain_analysis/` 解出的 C#)**:
- **`ParticleDomain.AlignStreaks(dir)`(权威)**:
  ```
  Vector3 n = dir.normalized;
  Vector3 side1 = Cross((|n.y| < 0.999f) ? Vector3.up : Vector3.forward, n).normalized;
  Vector3 side2 = Cross(n, side1);
  float num = Clamp(dir.magnitude * _stretchAmount, 1f, _stretchLimit);
  _RotationMatrix.SetColumn(0, side1); SetColumn(1, n*num); SetColumn(2, side2);
  ```
  → **雨丝朝向 = 世界空间旋转矩阵**(列1 = 相对速度方向 × 拉伸,承载网格长轴 y),**零屏幕投影**;
  `dir = _combinedVel + _frameVel - _spectatorVel`,`_combinedVel = _windVel + _fallSpeed*Vector3.down`。
- **粒子位置是世界系下落**:`_particleVel = Time.deltaTime * _combinedVel`,`Positioning` 只加它,
  **没有相机速度补偿**;域中心 `_domainPos = _target.transform.position`(**相机位置**,每帧),出球重生。
  compute(uniform 布局实锤):Positioning 用 `_domainRadius/_domainPos/_particleVel/_frameVel/_InstanceCount`。
- **换帧原点重定位**:`ParticleHandler.OnFloatingOriginChanged → TranslateParticles(e.OldFloatingOriginOffset - e.NewFloatingOriginOffset)`
  (compute 有 `TranslateFixed` kernel,`_translation`)。
- **速度源**:`RespectCameraVel` 只在第一人称/FlyBy 视角为真(用 `_targetCam.velocity`),常规视角用
  `GetPlayerVelocity()`(= 飞行器速度,**且 y 取绝对值** `Mathf.Abs(velocity.y)`,俯冲时雨丝不翻转)。
- 其它:无随机滚转角;网格 y∈[-streakLength, 0](亮头拖尾);遮挡(正交深度图)+ 视锥剔除;
  雨强度阈值 `WeatherValue > 2.25`,淡入 20s/淡出 2s,基础量 100000×质量×强度。

**根因(本轮三问同一根)**:我阶段 3 的**屏幕平面投影构轴** —— 雨丝被强制躺在屏幕平面内 →
① 转镜头时朝向随视角摆("贴相机");② zoom 改变屏幕尺寸感;③ **垂直往下看时雨丝垂直于视线 → 糊满屏幕**。
SP2 的**世界系矩阵**天然没有这三问:朝向锁在世界系(转镜头/缩放无关),垂直向下看时雨丝与视线平行 → 呈点状。

**本轮改动(全部对齐 SP2)**:
- `RainParticles.shader`:删掉 `_DownDir/_CamFwd/_CamUp/_RelVel/_StreamMode/_Stretch/_RandomData`,
  改为 `float4x4 _RotationMatrix` + `local = mul((float3x3)_RotationMatrix, v.vertex.xyz)`(**世界系构轴**);
  顺带修正 UV 方向(uv.y 0 = 头(亮) = +轴 = 相对运动前方 = 亮头拖尾,同 SP2)。
- `RainParticles.compute`:Positioning 去掉 `+ _camVel`(**世界系下落**);新增 **`TranslateFixed` kernel**
  (`_Positions[i].xyz += _translation`)。
- `RainParticles.cs`:
  · `BuildStreakMatrix()`(AlignStreaks 逐行移植,拉伸烘焙进列1)+ 每帧 `_mat.SetMatrix("_RotationMatrix")`;
  · 速度源 = `CraftScript.FrameVelocity`,**y 取绝对值**(照抄 SP2),`StreamMode=1` 沿相对速度 / `0` 恒径向"下";
  · 去掉随机滚转角与 `_RandomData` 材质绑定;
  · **换帧重定位**:ModApi `IGameView.ReferenceFrameRecentered(IReferenceFrame, Vector3d positionDelta, Vector3d velocityDelta)`
    → `TranslateFixed(positionDelta)`(ModApi 文档明示非托管物体应"加到"当前位置);
    ⚠️ `GameWorld.FloatingOriginChanged` 不可用 —— mod 引用的 `SimpleRockets2.dll` 是剥过的 API 程序集
    (2021 个类型,无 `Assets.Scripts.Flight.Events`),必须走 ModApi 这层;
  · 心跳新增 `rec=次数/最近平移量(m)`(验证重定位)与 `craftSpd=`。

**再测命令**(重打包后):`volkenRainP2On 1` → **转镜头/暂停/缩放:雨丝朝向不变**(本轮验收点)
→ **垂直往下看:雨丝呈点状、不糊屏** → 飞行看流线斜向(世界系固定)→ 飞过 ~1km 看是否平滑(日志 `rec=` 增长)。
`volkenRainP2Stream 0` 对比恒竖直。

### 10.14 阶段 3 第五轮:自查(发现 2 个真 bug + 1 个日志陷阱)+ 全量诊断日志

**自查发现 ①(真 bug,分布)**:`Randomize` 与重生都用 `r = Hash11(...) * _domainRadius`(半径**均匀**)
→ 体密度 ∝ 1/r² → **粒子挤在球心**。这既是"雨是一团"的**另一半**原因(不只是暂停/坍缩),
又是"**垂直往下看糊屏**"的帮凶(镜头附近粒子过密 → 近处粒子在屏幕上巨大)。
**修**:`r = R * u^(1/3)`(u 均匀 → 体积均匀),`pow(u, 0.3333333f)`,两处都改。

**自查发现 ②(真 bug,可观测性)**:没有"回收是否在工作"的观测量 —— 出球重生完全不可见。
**修**:加 `RWStructuredBuffer<uint> _DiagBuffer`,`Positioning` 重生分支里 `InterlockedAdd(_DiagBuffer[0], 1u)`;
C# 每帧**不**清零(累计到下次回读),回读回调里记录并清零重启 → 精确 **respawns/10s 与 /s**。
读法:静止 ≈ cap/(R/fallSpeed) ≈ 3000/s;高速飞行显著升高(相机扫过自身体积 ≫ 下落速度)。

**自查发现 ③(日志陷阱)**:原 SELFCHECK 用 `Material.HasProperty` 逐个查 `_Positions/_RotationMatrix/_FadeAmount/...`
—— 但它们是**只在 HLSL 里声明的 uniform,不在 Properties 表里**,`HasProperty` 未必返回 true →
会把**新版 shader 误报成"旧 shader"**,反向误导。
**修**:shader Properties 里加**部署哨兵** `_ShaderVer`(本版 = 2;1 = 屏幕平面构轴,2 = SP2 世界系旋转矩阵),
C# `ShaderBuild = 2f` 对比;Properties 表里那 6 个才逐个 `HasProperty`。
compute 侧判据 = `TranslateFixed` 内核索引 ≥ 0。

**自查发现 ④(消除歧义)**:`mul((float3x3)_RotationMatrix, v)` 的列语义可能在以后被反复质疑 ——
已在 shader 注释写明**证明**(HLSL `mul(M,v)` 取各行点积 → 平移须落在 M 第 3 列 = Unity `SetColumn(3,t)` 的
`[0..2][3]`,与 `mul(UNITY_MATRIX_V, worldPos)` 实测带平移互为佐证);并**弃用** `_mRC` swizzle 写法
(Unity 官方 CGIncludes 里查不到该用法,不值得冒语法风险)。

**本轮新增日志地图(真机读法)**:

| 日志行 | 频率 | 判读 |
| --- | --- | --- |
| `SELFCHECK: cap=... domainR=...` | 一次 | 参数实况快照 |
| `SELFCHECK density:` | 一次 | 粒子/m³(与 SP2 参考 0.139/m³ 比,阶段 4 标定基准) |
| `SELFCHECK kernels:` | 一次 | `TranslateFixed≥0` = **compute 是新版**;−1 = 包旧 |
| `SELFCHECK material: shaderVer=N` | 一次 | **N==2 = shader 是新版**;否则包里是旧 shader(雨丝朝向会全错) |
| `SELFCHECK mesh / recenter` | 一次 | 网格规模;`hooked=true` = 换帧事件挂上(否则飞远会跳) |
| `enabled = True \| cap=...` | 开雨时 | 全状态 + 提示看哪几行 |
| `RainParticles: calls=...` | 1s | camSpd/craftSpd/dt/down/stretch/stream/`rec=次数/平移量`/soft/readback |
| `RainParticles axis: dir=... ·fwd=` | 1s | **`·fwd` 恒≈0 = 雨丝被压在屏幕平面里(旧构轴指纹)**;世界系构轴下随视角自由变化;`·down≈1` = 静止时沿径向"下"正确 |
| `pos[0..2] ... pos0Δ w0` | 10s | `pos0Δ` 变动 = 位置在积分;`w0` = kernel 写入的时间戳 |
| `dist[64]: ... mean/R≈0.75` | 10s | **分布实况**:mean/R 明显偏小 = 还在中心堆积(near<R/2 占多数) |
| `time: kernelW0 vs nowTime` | 10s | delta≈0 = kernel 在跑;delta 大 = kernel 没跑或游戏暂停 |
| `diag: respawns=... /s` | 10s | 回收在工作;恒 0 且游戏在跑 = 粒子从不离域 |
| `RECENTER #n: delta=...` | 事件 | 换帧重定位真的发生且平移已应用 |

### 10.15 阶段 3 第六轮:SP2 再挖一轮(出厂参数 + 帧差异实锤 + 柔边贴图/边界淡出)

**① SP2 雨的出厂参数表(第一手,`sp2d4/Assets/Resources/prefabs/ParticleDomain.prefab`,序列化原值)**:

| 字段 | SP2 出厂值 | 我的默认 | 备注 |
| --- | --- | --- | --- |
| `_domainRadius` | 50 | 50 | 一致 |
| `_particleAmount` / `_baseAmount` | **100000** | **10000** | **差 10 倍!密度 0.191/m³ vs 0.019/m³**;`volkenRainP2Cap 100000` 可对齐 |
| `_fallSpeed` | 15 | 15 | 一致 |
| `_streakLength` / `_streakThickness` | 2.5 / 0.1 | 2.5 / 0.1 | 一致 |
| `_stretchAmount` / `_stretchLimit` | 0.045 / 3.5 | 0.045 / 3.5 | 一致 |
| `_falloff` / `_fadeAmount` | 0.9 / 1 | 0.9 / 1 | 一致 |
| `_mainLightIntensity` | **1.2** | `_Emission`=0(≈×1) | 阶段 4:按主光强度调雨亮度(夜里雨变暗) |
| `_particleScale` | 1 | — | 阶段 4:每粒子尺寸抖动(SP2 的 `_culledBuffer` stride=16 vs `_pointBuffer` 12 → 第 4 分量疑似每粒子缩放) |
| `_occlusionEnabled` / `_occlusionResolution` | 1 / 256 | 无 | 阶段 4 性能项:沿下落方向的正交深度相机 + `_occlusionThresholdAGL=100`(贴地/机内才启用) |
| `_threadGroupSize` | 64 | 64 | 一致 |
| 强度系数 | heavy 1 / normal 0.5 / light 0.25;质量 high 1 / medium 0.5 / low 0.25 | — | 阶段 6 接天气状态机 |
| 贴图 | `_billboardTexture` = `Resources/sprites/particle_blob_2.png`(**软 blob**) | 程序化柔边贴图 | 见 ③ |
| compute | guid `2146876f780e95a428575f680dcb7ef8` = **`Assets/ComputeShader/BillboardParticles.asset`** | 自写 RainParticles.compute | 同一套内核命名(Positioning/Randomize/TranslateFixed/Cull…/FillArgs) |

**② "down 用哪个"最终实锤(解掉本轮最大疑点)**:
- SP2 的 `_combinedVel = _windVel + _fallSpeed * Vector3.down` —— **世界 -Y**;
- 但我用 `craft.GravityNormal`(日志实测 `(0.20,-0.43,0.88)`,不是 `(0,-1,0)`);
- **两者不矛盾**:`jnoCode` 里 `CraftScript.GravityNormal => _flightData.GravityFrameNormalized`
  —— **帧空间**归一化重力 ✓;而 `ReferenceFrame` 的旋转是 `Quaternion.Euler(0, yaw, 0)`(**yaw-only 实锤**,
  帧 Y = 行星自转轴,中纬度处径向"下"在帧里是斜的);SP2(Simple Planes 2,地表飞机)的帧竖直对齐,所以世界 -Y 就是它的"下"。
- **另:jnoCode(SR2)里根本没有 `ParticleDomain`/`ParticleHandler`/`WeatherValue`** —— 这套 GPU 雨是 SP2 的,SR2 本身没有。
  故"SR2 该用哪个 down"没有历史包袱,答案就是**帧空间径向** = `GravityFrameNormalized` ✓(我的实现正确)。
- 新增判读 `down·camUp`(停机坪镜头水平时应 ≈ **-1**;≈0 = down 不在渲染空间里)—— 直接验证这条结论。

**③ 本轮按 SP2 补的两处**:
- **域边界淡出**(SP2 把 `_DomainPos`/`_DomainRadius` 传给 **material**,唯一合理用途即此):
  shader 里按**粒子中心**到域中心距离在 `[1-_EdgeFade, 1]·R` 内把 alpha 渐隐到 0
  → 球域边界与"出域回收"的突现都不显形。`_EdgeFade` 默认 0.2,`volkenRainP2Edge 0` 可关。
- **程序化柔边雨丝贴图**(SP2 用软 blob `particle_blob_2.png`,我原先 `_MainTex` 默认 white = **硬边四边形**,
  正是"面条感"的来源之一):运行时生成 32×64 贴图,横向 smoothstep 柔化侧边 + 纵向轻微头亮尾淡
  (主渐变仍由 `_Falloff` 控制,避免叠加过淡)。**不搬资产,程序化生成**。
- shader 版本哨兵 → `_ShaderVer = 3`(C# `ShaderBuild = 3`)。

**④ 供阶段 4/5 引用的 SP2 事实**:
- 密度必须跟半径联动(SP2 固定 R=50/100k = 0.191/m³ ≈ EVE 0.139 量级);我阶段 4 的
  `densityScale = (radius/50)^2.5` 与 `cap 400k` 上限方向正确。
- `_FadeAmount = _fadeAmount * _fadeMultiplier * cloudHeightFade` —— **云层高度淡出**(相机进云层时雨淡出)
  与 20s 淡入/2s 淡出都在这一条;阶段 5/6 接线。
- `TranslateParticles` 只做**平移**不做旋转(yaw-only 帧的旋转被忽略,50m 域内误差可忽略)——我也是这么做的。
- 风:**SR2(jnoCode)没有 WindManager、ModApi 也没有风 API** → 风只能来自 mod 自己配置(阶段 5 既定结论,已复核)。
- `FrameVel`(SP2 protected 属性)在 ParticleHandler 里**没有被赋值**(全程 zero)→ 阶段 5 我们保持 `_frameVel = 0` 有据。

### 10.16 阶段 3 第七轮(2026-09-29):真机全绿 + 抓到"换帧事件不触发"的真 bug + 接入 UI 实时控制

**① 真机判读全绿(§10.13~10.15 的修复全部验证通过)**:
```
SELFCHECK kernels: Randomize=0 Positioning=1 TranslateFixed=2 FillArgs=3      ← compute 是新版 ✓
SELFCHECK material: shaderVer=3(期望 3) 新版✓ props ok=[...] missing=[]       ← shader 是新版 ✓
SELFCHECK mesh: verts=8 tris=4 bounds=(0.10,2.50,0.10) tex=32x64 程序化柔边   ← 网格/贴图 ✓
RainParticles axis: dir=(0.17,-0.43,0.89) stretch=1.00 ·fwd=0.12~0.55 ·down=1.00 down·camUp=-0.98
RainParticles dist[64]: min=6.7 mean=32~38 max=49.8 near<R/2=7 mid=54 beyondR=0~2
RainParticles time: kernelW0=99.7 nowTime=99.7 delta=0.0                       ← kernel 在跑 ✓
```
- **`down·camUp ≈ -0.98`** → `GravityNormal` 确实是"画面下方"(§10.15 的 down 结论实测成立;相机抬头时降到 -0.84~-0.86,合理);
- **`·fwd` 在 0.12~0.55 之间随视角变** → 雨丝**不是**屏幕平面构轴(旧构轴的指纹是恒 ≈0)✓;`·down=1.00` → 停着时雨丝正好沿径向"下" ✓;
- **`dist mean/R = 0.65~0.76`**(均匀体积期望 0.75)→ 体积均匀分布生效(修前会是 ≈0.5 的中心堆积)✓。

**② 本轮抓到的真 bug:换帧事件不触发 → 雨被留在原地 → 批量重生(40k/s)**
- 现象:心跳 `dt=0.0000`(暂停)、`craftSpd=0.0`(飞行器没动),但 **`pos0Δ` = 450~1630m**(相机帧坐标跳了半公里到 1.6 公里),而 **`rec=0`(ModApi `ReferenceFrameRecentered` 一次都没触发)**;`respawn` 飙到 **2.2 万~5 万/s**(正常停机应为 ~3 千/s)。
- 机理:世界系下落的位置存在**帧空间**坐标里 → 换帧(浮点原点重定位)时整个世界平移,雨不跟着平移 → 全部粒子瞬间落到域外 → **每帧整批重生**(4~5 次/秒)→ 表现为"雨整片刷新一下"。
- 修复:
  · 事件回调**只记录不应用**(`_pendingRecenterDelta`),ModApi 文档明确警告"订阅了该事件就不要再在别处加 delta,否则会加两次";
  · **兜底判据(主力)**:飞行器是"世界物体",换帧时它的**帧位置**会整体跳变(它自己并没动)→
    `RainParticles.DetectRecenterJump`:`|ΔcraftFramePos| > max(100m, 自身运动×3 + 30m)` 即判定换帧,
    用 `ICraftScript.FramePosition`(ModApi 公开)算 delta;
  · 每次 Tick **只应用一次**(事件 delta 优先,否则用跳变量)→ `TranslateFixed` kernel;
  · 新增诊断:`rec=N(ev事件数/jp跳变判定数)` + `craftJump=` + RECENTER 事件日志;面板状态行同显。
- ⚠️ 副作用待观察:阈值 100m 只会在"单帧位移 > 100m"时误判(需 dt>0.6s 的长卡顿 @170m/s),代价是多平移一次(50m 域内可忽略)。

**③ 接入 UI 实时控制(任务 2)**:
- `RainSection` 新增 6 个字段(默认值即 SP2 出厂值):`streamMode=true`、`stretchAmount=0.045`、
  `edgeFade=0.2`、`softParticles=1`、`tailFalloff=0.9`、`brightness=0`;同步 `CopyFrom` 与 `Clamp()`
  (旧 XML 反序列化自动取初始值 → **不改变老玩家行为**)。
- `RainParticles`:把 `FallSpeed/StreakLength/StreakThickness/Falloff` 由 **const 改 static**(UI 可调),
  新增 `Brightness`、`ApplyConfig(RainSection)`(容量→重建 buffer;雨丝长宽→重建网格;其余每帧读静态字段)、
  `DensityPerM3()`、`StatsLine()`(面板状态行)、`RebuildMesh()`、`FindCurrentInstance()`;
  **配置驱动初始状态**:`AttachToCurrentView()` 末尾调 `ApplyConfig`(weather.xml 里 `enabled=true` 就自动开雨);
  ⚠️ `ApplyConfig` 内部**不再**回头调 `AttachToCurrentView`(避免递归),改为 `inst.EnsureBuffers()`。
- `WeatherPanel.BuildRainGroup`:**整组由"禁用占位"改为实时控制** —— 启用开关、**状态行(实时数值)**、
  「立即切换雨」按钮、数量(1000~200000)、域半径、下落速度、雨丝长度/宽度、**拉伸系数**、**域边界淡出**、
  **软粒子**、**尾淡**、**亮度**、**朝向模式(相对速度/恒径向)**、触发阈值(阶段 6 前手动调);
  新增 `ApplyRain()`(写配置 + 立刻推运行时)。
- **Locale**:ZH-CN/EN-US/RU-RU 各新增 11 个键(状态行、立即切换、开关提示、7 个参数标签);
  雨分组标题从"雨(已移除,占位)"改为"雨(阶段 3,实时生效)"。
- 保留在 XML 但不进面板的字段:`strength`(阶段 6 天气联动)、`windInfluence`(阶段 5 风)、`volume`(阶段 7 雨声)、
  `adaptiveDomain`(面板中显示为禁用占位)。

**④ 下一步**:用户去 SP2 实机对比找差异;我这边等真实观感反馈后进阶段 4(密度标定 0.139~0.191/m³、
自适应域半径、`_mainLightIntensity`/遮挡剔除等 SP2 项)。



### 10.17 阶段 3 第八轮:两游戏**相机缩放尺幅不同** → 高度闸门(修"太空里还下雨")

**用户指出的关键差异(必须考虑)**:
- SP2 最大缩放只到"半个岛",**远低于云层** → 它的雨从不需要处理"相机在云层之上/太空"的情况;
- JNO(SR2) 最大缩放是**整颗星球** → 不加限制,**当前 build 会在太空里下雨**。

**SP2 的等价机制(第一手)**:`ParticleHandler.Update()` ——
`signedAltitudeAGL = GetSignedAltitudeAGL(camera)`;只有 `> 0`(地面以上)**才调 base.Update()**;
`base.CloudHeightFade = FlightSceneScript.Instance.Environment.CameraCloudFadeVal`(**环境给的云层高度淡出**),
最终 `_FadeAmount = _fadeAmount × _fadeMultiplier × cloudHeightFade`;另 `IsCameraSubmerged()`(水下)直接不更新。
即:SP2 是"环境提供的高度淡出系数",因为它的缩放尺幅用不到更强的门。

**本实现(第一版曾按云顶自动取值;2026-09-29 用户决定改为独立配置项)**:
- **上限 = 雨自己的配置项 `ceilingAltitude`(米;0 = 关闭闸门/不限制),默认 12000m**;
  ⚠️ **不与云层联动**:不去读 `CloudConfig.maxCloudHeight` —— 用户判断当前非常早期,少一层耦合、行为可预测,
  云层联动留到以后需要时再说(已删掉 `ComputeCloudTop()` 及其对 `VolkenMod.Instance.layers` 的依赖)。
  默认 12000m 与 JNO 默认云顶(≈11238m)同量级,但**来源是雨配置,不是云配置**。
- **相机海拔**:`ComputeCameraAltitude()` 与 `CloudRenderer.ComputeCameraAltitude` **同一公式**
  (`|camPos − 行星中心| − 行星半径`,用 `craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero)` + `Parent.PlanetData.Radius`);
  取不到时返回 −1 → 闸门不生效并**每 30s 告警一条**(失败模式可见,不会静默)。
- **淡出**:`altFade = saturate((ceil − alt) / (ceil × band))`,`band` 默认 0.4 → 上限的顶部 40% 渐隐;
  乘进 shader 的 `_FadeAmount`(阶段 5/6 的天气淡入淡出后续乘进来)。
- **`altFade ≤ 0.001` 时 Tick 直接 return**:不 dispatch、不绘制 → 太空/云层之上**零成本、零雨**。
- ⚠️ 抑制期间**必须同步换帧状态**(`_lastCraftFramePos` / 清 pending):否则恢复下雨时,抑制期间累积的
  帧位置跳变会被 `DetectRecenterJump` 误判成一次换帧,把整片雨甩飞(实测隐患,已修)。
- 配置/UI:`RainSection.ceilingAltitude`(默认 12000m;0 = 关闭闸门)+ `ceilingBand`(默认 0.4);
  面板两个滑块「雨的海拔上限(米;0=关闭闸门,不限制)」「上限淡出带宽」;状态行显示 `alt 800m ceil 12000 fade 1.00`
  (闸门关闭时 ceil 显示 `off`),被抑制时显示 `suppressed by altitude — alt …m ≥ ceiling …m`;
  心跳加 `alt=/ceil=/altFade=`;`ApplyConfig` 日志打印 `ceil=12000m(独立配置)` 或 `off(不限制)`。

**为什么不做"只按 AGL"**:SP2 用 AGL(地面以上)是因为它的地形尺幅小;JNO 有 8km 高山与整颗星球,
用**ASL + 云顶**更符合"雨只存在于天气层内"的物理,也天然覆盖太空/轨道视角。
**遗留**:水下未门控(SP2 有 `IsCameraSubmerged`)—— 列入阶段 4。
### 10.18 阶段 3 第九轮:诊断"说不出的怪异感"(移动镜头朝向/缩放时最明显)

**用户反馈**:当前雨与 SP2 比仍有一种"说不出的怪异感",**特别是移动摄像机朝向和缩放的时候**。

**主嫌疑(有 assets 级证据):出域处置方式 —— 我的是"球内随机",SP2 是"确定性镜像"**
- 证据(`sp2d4/Assets/ComputeShader/BillboardParticles.asset` 序列化元数据):`Positioning`
  **`inBuffers: []`**(无任何输入 buffer)、**`outBuffers: 只有 _Positions`**
  (`_pointBuffer` stride = **12 = float3**,连 w 都没有)、常数缓冲只有
  `_domainRadius/_domainPos/_frameVel/_particleVel/_InstanceCount`
  → SP2 的 Positioning **没有每粒子随机数可用**,出域处置只能是**位置式确定性**的(穿过球心镜像/传送带)。
- 我的第 §10.10 轮为修 `frac` 坍缩改成了**球内均匀随机重生**(id 派生 seed + `floor(_time*20)` 相位):
  后果是粒子**在域内任意位置凭空出现,包括紧贴镜头处**(50m 球里 0~2m 也有)→
  一根 2.5m 雨丝在 1~2m 处爆闪 = 整场**沸涌**;而这个沸涌场**以相机为中心** →
  **转动/移动镜头、缩放时最刺眼**(正是用户描述的"说不出的怪异感")。
- **修复(默认开)**:`_respawnMode == 1` → `p.xyz = _domainCenter - normalize(p-c) * R * 0.999`
  (穿过球心镜像到**对侧边界**):新粒子永远在 **50m 外的边界**出现,配合 `_EdgeFade` 边界渐显
  → 视觉上是一条**连续穿过的雨帘**,近旁不爆闪。且与 `_EdgeFade` 天然配套
  (这正是 SP2 把 `_DomainPos/_DomainRadius` 传给 material 的用途:边界渐显)。
- 开关:`volkenRainP2Respawn 0|1` / 配置 `respawnMirror`(默认 true)/ 面板「出域镜像重生」。
  `0` = 旧的球内随机(用于 A/B 对比"沸涌感")。

**次要嫌疑(建议 A/B 排除,均按"SP2 没有/SP2 看不到"排列)**:
1. **软粒子是我加的,SP2 没有**(SP2 material 只有 `_FadeAmount/_Falloff/_MainLightIntensity/_DomainPos/_DomainRadius/_Tex/_Scale/_Positions/_RotationMatrix`,
   **没有任何深度纹理**)→ 我这条屏幕空间 alpha 调制依赖 `CloudRenderer` 的深度图(且在 `OnRenderImage` 写、比雨的 `OnPreCull` **晚一帧**)→
   转镜头/缩放时深度滞后 → alpha 在屏幕上"爬行"。**验证:`volkenRainP2Soft 0`**;若怪异感消失,阶段 4 再决定要不要保留软粒子。
2. **50m 球域在 JNO 的缩放尺幅下会被看见**:SP2 最大只到"半个岛",镜头始终贴着飞机,看不到域边界;
   JNO 缩远后 50m 域会显成一个"雨球"(叠加 `_EdgeFade` 还会看到一圈渐隐壳)。
   → 这正是阶段 4「自适应域半径」要解决的;**验证:`volkenRainP2Edge 0` 看壳是否消失 / 拉远看球是否显形**。
3. **十字截面是世界系朝向**(与 SP2 相同,`side1 = cross(worldUp, n)`):转视线时两片交叉面呈现的
   宽度/亮度会变(重叠=亮、一片侧对=暗)→ 但 **SP2 同样如此,不是差异源**;若以后仍觉闪烁,
   可改成"绕雨丝轴面向相机"(billboard),那是改进项而非对齐项。

**判读日志**:心跳 `respawn=`(镜像模式下重生率应与随机模式同量级,但**新粒子位置恒在 R 附近**);
`dist[64]` 的 `min` 会**明显变大**(球心/near 区不再被"补种")→ 镜像生效的可观测指纹。
### 10.19 阶段 3 第十轮:删除全部控制台指令,旋钮全部集成进天气面板

**用户要求**:"删除这些 debug 指令,把它集成到 UI 里面"(不再靠敲 `volkenRainP2*` 调参)。

**已删(Mod.RegisterCommands 里 9 条雨指令)**:`volkenRainP2` / `On` / `Row` / `Cap` / `Stretch` / `Soft` /
`Stream` / `Edge` / `Respawn` —— 以及它们对应的静态 setter(`SetRow/SetCapacity/SetStretch/SetSoft/SetStream/SetEdge/SetRespawn`)。
保留 `SetEnabled`(面板用)、`ApplyConfig`(配置→运行时)、`StatsLine`(状态行)、`DiagStatus`(日志导出)。

**全部功能已在天气面板「雨」分组**(22 项,真值来源 = `weather.xml` 的 `RainSection`):
启用雨 · 实时状态行 · 立即切换雨 · **把雨状态写入日志**(原 `volkenRainP2` 的 UI 化,一键把 5 行完整状态写进
Player.log 方便发日志)· 粒子数量(密度)· 域半径 · 下落速度 · 雨丝长度 · 雨丝宽度 · 拉伸系数 · 域边界淡出 ·
软粒子 · 尾淡 · 亮度 · 朝向模式(相对速度/恒径向)· **出域镜像重生**(原 `volkenRainP2Respawn`)·
海拔上限(0=关闭闸门)· 上限淡出带宽 · 触发阈值 · 域半径自适应(阶段 4 前禁用)· **开发:等距排自检**(原 `volkenRainP2Row`)。
Locale:中/英/俄各新增 3 键(`RainDumpLog` / `RainDumped` / `RainRowTest`),雨分组共 27 个键。

**未删**:Phase 1 的 5 条 `volkenRainAxis*`(那是另一套"构轴对照探针" `RainAxisProbe`,无 UI 对应物,
且计划 §10.4 的 ④⑥ 两项还没补)→ 如需一并删除或也做进 UI,说一声即可。

**约定**:以后雨的所有可调项一律进面板;需要"一次性诊断输出"就加按钮(如本次「把雨状态写入日志」),不再加控制台指令。
### 10.20 阶段 3 第十一轮:"和下面条一个样"→ 找到并修掉三个成因(配置默认值被换掉 + 贴图两处)

**用户反馈**:"得改回来,现在这个状态看上去和下面条一个样"。

**根因(机械性、可核对)**:第十轮把参数真值来源从"代码静态值"换成"weather.xml 配置",而**配置层的旧默认值和代码默认值不一致**:

| 参数 | 代码静态值(用户上一版实际看到的) | 配置层旧默认值(换源后生效) | SP2 出厂值 |
| --- | --- | --- | --- |
| `streakWidth` 雨丝宽度 | **0.1** | **0.05** ← 细了一半 | 0.1 |
| `amount` 粒子数量 | 10 000 | 20 000 | 100 000 |

宽度腰斩 → 雨丝变细亮线 = 面条。**更关键的是:磁盘上已有存档**(`UserData/VolkenWeatherConfig/Droo/{Default,Default1,sb}.xml`)
里就写着 `<streakWidth>0.05</streakWidth>` / `<amount>20000</amount>`,而 **XML 反序列化对已存在字段用存档值,
改 C# 默认值根本不生效** → 必须做"未修改过的旧默认值"升级。

**修法**:
1. **配置默认值对齐 SP2 出厂值**:`streakWidth 0.05 → 0.1`;`amount 20000 → 100000`(密度 0.191/m³ = SP2);
   新增 `stretchLimit`(默认 3.5 = SP2 `_stretchLimit`)+ 面板滑块(雨丝最长 = 长度 × 此值 → 直接治"太长的面条")。
2. **`VolkenWeatherConfig.UpgradeUneditedDefaults()`**(加载后调用):**只重写"仍是旧默认值"的字段**
   (0.05→0.1、20000→100000、stretchLimit 缺省→3.5),手动改过的一律不动,并打日志说明升级了几项。
   —— 否则老配置会永远停在旧值上(这是本轮最容易被忽略的一环)。
3. **贴图两处"面条"成因**:
   · 横向:早期是**中心亮线** profile(有效宽度只剩四边形 ~40%)→ 改**满宽软板条**(中段 45% 满不透明+软边);
   · 纵向:早期亮度几乎满长(1.0→0.75)→ 拉伸 3.5 倍后是 **8.75m 满长亮柱** → 改**快速收尾**
     (`1-smoothstep(0.2,1,v)`,亮芯只占靠头一段、尾端收尖)= SP2 软 blob 被拉伸后的"软芯长条"观感。

**SP2 对照**:它的雨同参数(宽度 0.1 / 长度 2.5 / stretchAmount 0.045 / limit 3.5)之所以不像面条,
靠的是 **10 万粒子铺满 + 软 blob 贴图**(软芯短、两端收尖)+ `_mainLightIntensity 1.2` 的整体提亮。
本轮把前两项对齐了;亮度项(面板「亮度增益」)留作微调,**遮挡剔除/云层高度淡出**仍在阶段 4/5 清单里。

**面板新增**:「拉伸上限」滑块(1~12)。**排查入口**:面板「把雨状态写入日志」会打印当前全部参数与实时数值
(cap/R/密度/respawn/stretch/alt/ceil…),发日志即可核对"配置到底生效没有"。
### 10.21 阶段 3 第十二轮:澄清真问题 = "雨像下面条一样集中一股脑下降"(§10.18 的镜像重生是元凶)

**用户澄清**:"我说问题的是**雨会像下面条一样集中一股脑下降**,而不是和现实的雨一样"
—— 即 §10.20 我按"雨丝形状/粗细"去修是修错了方向;真问题是**整片雨的运动是成团的、有节律的**。

**机理(根因就在 §10.18 我加的那个"镜像重生")**:
- 所有粒子**同速下落**(`p += down * _fallSpeed * dt`,SP2 也是这样),所以两次重生之间整片雨是**刚性平移**;
- §10.18 我把出域处置改成**确定性镜像**(出域点 → 对侧对称点)→ **整片雨零混合**:
  初始分布里的任何一簇粒子**永远保持成团**,一起下落 → 到边界被镜像 → 又从对侧整团下来;
- 于是同一团雨**周期性地重复经过镜头**:静止周期 = 2R / fallSpeed = 100m ÷ 15m/s ≈ **6.7 秒**,
  飞行时(170 m/s)缩到 **≈0.6 秒** → 高频脉动 —— 这正是"集中一股脑下降 / 像下面条"的来源,
  也解释了更早那条"移动镜头朝向和缩放时说不出的怪异感"。
- 反过来说:**"随机重生"带来的"持续混合"才是像真雨的关键**(每次出域都重新随机化 → 场不断去相关);
  §10.18 我为了"消除近旁爆闪"牺牲了混合,是本末倒置。

**修法**:
1. **默认回到随机重生**:`RespawnMirror = false`(配置 `respawnMirror = false`);镜像分支保留为对照实验
   (`_respawnMode = 1`),并在注释里写明"确定性 → 零混合 → 周期团块,不推荐"。
2. **同时保留 §10.18 的正确一半**:随机重生位置改为**壳层 [0.15R, R] 内体积均匀**
   (`r = R * lerp(0.15³, 1, u)^(1/3)`)—— 被排除的球心只占体积的 0.3%(密度无感),
   但**粒子不再紧贴镜头凭空冒出**,兼顾"混合"与"不爆闪"。
3. 面板开关文案改为「开=确定性传送带,会周期性团块;关=随机混合,推荐」。

**副作用/代价**:随机重生在域内随机位置出现(含近处)→ 仍可能有轻微近旁闪;若观感上仍觉"闪",
下一步可加**每粒子下落速度抖动**(hash 派生 ±10~15%,现实雨的雨滴尺寸/终速本来就不同),
它能在不引入确定性的前提下进一步打散相干性 —— 待用户观感确认后再决定。

**另修(§10.20 的补充)**:上一轮我按"雨丝形状"改的宽度/贴图/密度**保留**(那些本身是改进:
配置默认值对齐 SP2 出厂值 + `UpgradeUneditedDefaults` 升级未改过的旧值 + 贴图"满宽软板条+快速收尾"),
但它们不是本次"一股脑下降"的成因,已在 §10.20 的措辞里更正。

**顺带修掉一个自造事故**:用 `.ps1 脚本文件`批量改语言文件时,**PowerShell 5.1 会把无 BOM 的 UTF-8 脚本按 ANSI 读**
→ 脚本里的字面中文/俄文在写入前就被破坏(ZH 3~4 条、RU 4 条词条变成乱码)。
已全部改回正确文本,并**复检三个语言文件 0 乱码**。约定:**改本地化一律用编辑工具或内联命令(内联正常),
不要用 .ps1 文件承载非 ASCII 字面量**。

### 10.22 阶段 3 第十三轮:把雨搬进 Unity 编辑器(打包 5 分钟 → 按 Play 5 秒迭代)

**用户诉求**:每轮测试都要"打包进游戏→进飞行场景",一轮 5 分钟起步,没法迭代观感。

**为什么能直接搬进编辑器**:`RainParticles` 对游戏 API 的依赖**全都写成了"失败即降级"**(前几轮防御性写法的红利),逐条对应:

| 输入 | 游戏内来源 | 编辑器里降级为 | 影响 |
| --- | --- | --- | --- |
| 下落方向 | `craft.GravityNormal`(帧空间径向) | 世界 `(0,-1,0)` | 观感等价(预览只需"竖直向下") |
| 朝向/拉伸速度源 | `craft.FrameVelocity` | **相机实测位移速度** | 正好 = 自由飞的飞行速度,能直接看飞行观感 |
| 海拔闸门 | craftNode/PlanetData 算海拔 | 返回 −1 → 闸门不生效;用 `DebugAltitudeOverride` 手动喂 | 可模拟云上/太空 |
| 软粒子 | `CloudRenderer.LinearSceneDepth` | 无深度图 → `_InvFade=0` 自动关 | **预览测不到软粒子** |
| 换帧重定位 | ModApi `ReferenceFrameRecentered` | 挂不上 → 只打一条日志 | 预览不需要 |
| 资产加载 | `Mod.ResourceLoader`(bundle) | **`AssetDatabase`**(新增 `#if UNITY_EDITOR` 回退) | 用工程内的 .compute/.shader 源文件 |

**新增/改动**:
1. `Mod.LoadVolkenAsset`:先走游戏加载器,失败则**编辑器分支用 `AssetDatabase.LoadAssetAtPath` 读工程资产**
   (路径格式两边一致 → 同一个常量);`Diag` 本来就有 try/catch 且无条件打日志 ✓、`Log` 在 `ModSettings.Instance==null`
   时静默返回 ✓ → 日志在编辑器里天然安全。
2. `RainParticles.DebugAltitudeOverride`(float,NaN = 用真实海拔):编辑器里手动喂海拔,试"云上/太空"闸门。
3. **`Assets/Scripts/VolkenTests/RainPreview.cs`(本轮先建在 Volken/Debug,随后见 §10.23 收进 VolkenTests)**(新,~430 行):编辑器预览台
   · 自动建相机(或复用本物体的 Camera/主相机)、把雨挂到该相机、铺**地面 + 近/中/远三个参照方块** + 平行光;
   · **自由飞**:右键拖拽转视角 / WASD / QE 升降 / Shift 加速 / 滚轮调基础速度;
   · **左上角 IMGUI 面板**:与游戏内天气面板**同一批字段**(密度、R、下落速度、雨丝长宽、拉伸、拉伸上限、
     边界淡出、软粒子、尾淡、亮度、朝向模式、镜像重生、海拔上限/带宽 + 覆盖海拔),改动**即时生效**;
     标 ◈ 的两项(容量、雨丝长宽)需要重建 buffer/网格 → **松手才提交**(避免拖动卡顿);
   · 实时状态行(`StatsLine` + alt/ceil/altFade/stretch/axis)+「重置为 SP2 出厂参数」+「把状态写入 Console」;
   · 「**下次 Play 自动启动**」:写入 PlayerPrefs,之后**任意场景按 Play 都自动出现预览台**
     (`[RuntimeInitializeOnLoadMethod]` + `Application.isEditor` 门控 → 只影响编辑器,正式游戏不会触发)。

**用法**:任意场景(含 `SampleScene`)新建空物体 → Add Component → **Volken Rain Preview** → Play。
建议先在面板勾「下次 Play 自动启动」,此后免手动。

**预览测不到的项(仍需打包进游戏验收)**:软粒子、换帧重定位、真实海拔、游戏内面板/配置持久化、
与其他 mod 系统(云、天气状态机)的交互。

**注意**:预览里跑的是**同一份 compute/shader/驱动代码**,所以观感结论可直接外推;
但"游戏内最终验收"仍不可省(尤其上面那几项)。

**⚠️ 本轮事故与教训(必须记住)**:恢复文档时踩到老坑 —— 在**内联 pwsh 命令里带中文路径**,
路径被 shell 编码破坏 → `ReadAllText` 抛错但脚本未停 → `$t` 为空 → 随后的 `WriteAllText($fp, $t + $add)`
**把 986 行的计划文档覆盖成只剩新增一节**(3413 字节)。
* 恢复:同目录存在 ASCII 命名的全本镜像 `sp2-rain-particledomain-port-2026-09-28.md`(986 行,含到 §10.21),
  以其为源 + 追加本节 = 复原。
* **铁律**:① 内联 PowerShell 命令里**绝不出现非 ASCII 路径/文件名** —— 用 `Get-ChildItem -Filter` 取 `.FullName`;
  ② 改文档一律用编辑工具,或"先写临时文件再字节级拼接";
  ③ 任何 `ReadAllText` 之后必须确认成功(脚本开头设 `$ErrorActionPreference='Stop'`),避免空内容回写。

### 10.23 阶段 3 第十四轮:测试/开发脚本收进单一文件夹 `Assets/Scripts/VolkenTests/`(与正式代码解耦)

**用户诉求**:"这些代码和丢进游戏的相对独立,把测试相关的脚本 CS 文件全部放在一个文件夹里"。

**搬迁内容(连同 `.meta`,GUID 保留)**:

| 原路径 | 新路径 |
| --- | --- |
| `Assets/Scripts/Volken/Debug/RainPreview.cs` | `Assets/Scripts/VolkenTests/RainPreview.cs` |
| `Assets/Scripts/Volken/Debug/NoiseVisualizer.cs` | `Assets/Scripts/VolkenTests/NoiseVisualizer.cs` |
| `Assets/Scripts/Volken/Debug/RaymarchDebug.cs` | `Assets/Scripts/VolkenTests/RaymarchDebug.cs` |
| `Assets/Scripts/Volken/Weather/RainAxisProbe.cs` | `Assets/Scripts/VolkenTests/RainAxisProbe.cs` |
| `Assets/Scripts/Volken/Profiler/*`(4 个) | `Assets/Scripts/VolkenTests/Profiler/*` |

空掉的 `Volken/Debug/`(含 `.meta`)已删除。

**解耦(关键)**:原先正式代码里有 **7 处**对测试脚本的引用,全部摘除 ——
- `Mod.cs`:5 条 `volkenRainAxis*` 命令注册 + `VolkenProfiler.ProfilerController.Create()`;
- `VolkenMod.cs`:2 处 `RainAxisProbe.AttachToCurrentView()`。
改为 **`VolkenTests/TestsBootstrap.cs` 自发注册**(`[RuntimeInitializeOnLoadMethod]` 起隐藏物体 →
等控制台就绪后注册一次 → 自毁;`volkenRainAxisOn 1` 本来就会自行挂载探针,所以摘掉正式代码的挂载不影响使用)。

**契约(写进 `VolkenTests/README.md`)**:
1. **单向依赖**:测试代码可引用正式代码;正式代码**不得**引用测试代码;
2. **可整体删除**:删掉 `VolkenTests` 文件夹,mod 仍能编译运行(只是少了工具与命令)→ 想彻底不打包就直接删;
3. 命名空间统一 `Volken.Tests`(顺带**修掉 `Volken.Debug` 命名空间遮蔽 `UnityEngine.Debug` 的隐患** ——
   本轮就因此踩过一次 CS0234),`Profiler/` 保留 `VolkenProfiler`;
4. 命令/工具一律自发注册,不再由 `Mod.RegisterCommands` 代管。

**自查口令**(期望输出只有注释行,无任何代码引用):
```
Get-ChildItem Assets\Scripts -Recurse -File -Include *.cs |
  Where-Object { $_.FullName -notmatch '\\VolkenTests\\' } |
  Select-String -Pattern 'Volken\.Tests|RainAxisProbe|ProfilerController|VolkenProfiler|RainPreview|NoiseVisualizer|RaymarchDebug'
```

**验证结果**:正式目录 `Assets/Scripts/Volken/` 只剩 24 个正式文件;上述自查**零代码引用**(仅剩注释说明);
dotnet build **0 错误**。
