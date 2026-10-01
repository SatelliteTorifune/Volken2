# Volken —— SP2 雨系统 `ParticleDomain` 移植难点与分步计划(2026-09-28)

> 状态:📋 规划中(难点已核实,**未开工**;不改动任何现有代码)
> 日期:2026-09-28
> 关联:
> - [`archive/weather-rain-fog-postmortem-2026-09-27.md`](archive/weather-rain-fog-postmortem-2026-09-27.md) —— **本文档的必读前置**。本文所有"不能照抄"的判断都建立在它的铁律上(尤其 §3.1㈠㈡㈢、§5.1)。
> - [`proposals/sp2-weather-port-2026-09-27.md`](proposals/sp2-weather-port-2026-09-27.md) —— 母计划(**已转 proposals**)。§10.2 / §10.3 / §10.4 是实施期 30+ 处实测更正,本文大量引用;资源均已据原始数据复核。
> - [`README.md`](README.md) §四之三「雨重做」—— 本文档是该条的执行细案。
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
| 上次结果 | 雨(阶段 2)/雾(阶段 3)实现**整体移除**,当前只保留雷电 | [`README.md`](README.md) §四之三 |
| 代价 | **8 轮迭代 / 27 条更正 / 约 150 KB 代码被弃**(`Rain.cs` 113 KB + `RainParticles.compute` 14 KB + `RainParticles.shader` 10.5 KB + 雾相关);3 条环境雨声一并弃用 | 教训 §2 |
| 代码现状 | 全部 stash 在 `%TEMP%\volken-rain-fog-stash`,但 ⚠️ **该 stash 已被清理、不存在(2026-10-02 核实)** → 重写只能依据[复盘 §6「已证伪思路清单」](archive/weather-rain-fog-postmortem-2026-09-27.md) | 教训 §2 / 计划 §10.5 |
| 保留的净收益 | 教训清单(27 条)、诊断方法、量化基线;**配置层与 UI 占位已就位,重做时不用动** | 教训 结尾 / 计划 §10.6 ㊶ |
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

> 🔴 **必须照搬的硬护栏(计划 §10.2 ⑲)**:遮挡深度图**一旦为空**,`CullAndOccludePoints` 会把**每个**粒子判成"被几何体遮挡"并剔除 → **雨完全消失且没有任何报错**。
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
好消息:**接口是第一手可信的**(compute shader 资产内的反射数据 + 计划 §10.2 表格互相印证):

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

**(b) 🔴 但 `Camera.onPreCull` 静态委托在本工程从不触发**(计划 §10.2 ⑪,真机排除法证据:前置条件全满足、`fade` 从 0 正常爬到 0.765,但 `draws/s` 恒 0、`everDrew=False`,且 `RenderForCamera` 里三条 `LogThrottled` **一条都没打** → 函数**一次都没进入**)。
→ **用挂在游戏相机上的 MonoBehaviour 实例 `OnPreCull`**(实测 ≈20/s,这条路是通的)。
→ 另:静态委托类型是 `Camera.CameraCallback`,`.NET 4.x` 下 `Action<Camera>` **不能隐式转换**(CS0029)。

**(c) 浮动原点:** SR2 会 `RecenterReferenceFrame`(离帧中心 >5000 m / 帧速 >1000 m/s / 时间加速每帧 / 表面锁定切换)→ 世界坐标整体平移。

**→ 采用相机相对坐标系**(计划 §10.2 ⑨),可**天然免除**浮动原点补偿:
- 粒子位置 = **相对相机的偏移**;`_domainPos` = 相机世界位置(故 `世界 = _domainPos + 偏移` 仍成立);
- 相机高速(10 km/s 级)下没有大数精度问题;
- **`_frameVel` 恒 0 是刻意的,不是漏了** —— 不要"顺手补上";
- ⚠️ 附带要求:**视锥平面必须手工解析构造**(`BuildCameraPlanes`),**不要**混用 `GeometryUtility.CalculateFrustumPlanes`(它给**参考系空间**,与相机相对空间不一致 → 相机飞远后剔除**整体错位**,症状是"雨只在一小块出现/整片消失,且随离原点距离变化")。

**(d) 材质 uniform 是全局的**:多相机共享同一 `Material` 实例 → **每次绘制前必须按当前相机重设基向量与相机相关 uniform**,否则后一个相机覆盖前一个 → **朝向随相机跳**(计划 §10.4 G)。

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

- [ ] **0.1** ~~把 stash 里的旧实现只读参考取出看一眼~~ → ⚠️ **stash 已不存在**(2026-10-02 核实),改依据[教训 §6「已证伪思路」清单](archive/weather-rain-fog-postmortem-2026-09-27.md)逐条打勾 —— 目的是**知道哪些路已经证伪**,不是复用代码。
- [ ] **0.2** 确认 `docs/archive/weather-rain-fog-postmortem-2026-09-27.md` 的 §5.1 铁律 1~5 与 §7 起步清单已读。
- [ ] **0.3** **接上诊断三件套**(教训 §5.3 / 计划 §10.8),**先于任何渲染代码**:
  - GPU 回读:`_ArgsBuffer[1]` = `instanceCount`,每 10 s 一次(阈值表见计划 §10.8 ④:`instanceCount == 0` → 问题在**剔除/参数环节**,不是绘制环节);
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

> **状态注记(2026-09-29):3.1~3.4 已实现(见 §10),待真机验证后逐个勾选。**
> 关键取舍:SP2 用 material `_RotationMatrix` 世界系构轴(已被阶段 1 实测证伪为"横块"),
> 阶段 3 保留屏幕平面构轴;软粒子深度采样 `CloudRenderer.LinearSceneDepth`(本工程唯一线性深度入口)。

- [ ] **3.1** 按 SP2 的**第一手 Properties 命名**写(计划 §10.2 ⑳ 的权威来源):`_MainTex`(**RGB 有效、A 是 alpha**)、`_Emission`、`_MainColor`(**注意不叫 `_Color`**)、`_InvFade`(软粒子因子,`Range(0.01, 3)`)。本项目上一版用的是自写名(`_Tex`/`_Color`/`_SoftParticleFade`),**若要对齐 SP2 便于对照,改这里**。
- [ ] **3.2** 接入阶段 1 验证过的构轴算法;软粒子用 `CloudRenderer` 的线性深度(经只读口 `LinearSceneDepth`)。
- [ ] **3.3** **UV 不平铺**(计划 §10.2 ⑬):长度已由基向量列表达,贴图纵向就是"头亮尾淡";再按拉伸平铺会在 `wrapMode = Clamp` 下夹边形成糊块。
- [ ] **3.4** 补上 §3.1「开放问题」的**随机 roll**(SP2 用矩阵第 3 列携带,改 2D 基后需显式补)。

**准出**:雨丝有长宽比、有软边、随速度拉伸;把 `_stretchAmount` 设为 0 时退化为**正方形点**(可作为"基向量是否正确"的快速判据)。

### 阶段 4:数量、密度、域半径标定(**照 §3.5 做,不照抄 SP2**)

- [ ] **4.1** 实现 `EffectiveDomainRadius` + `densityScale`,并**保证 Tick 顺序**正确(§3.5 顺序要求)。
- [ ] **4.2** 实现 `_capacity` / `_activeCount` 分离(§4 第 2 条)。
- [ ] **4.3** 非下雨区间 `amount = 0`(§4 第 3 条)。
- [ ] **4.4** 打 `Rain.Density` 诊断日志,直接报 **个/m³** 与**与 EVE 的比值**(计划 §10.3 建立的手段)—— 判断"够不够密"不靠肉眼。

**准出**:高速(≥300 m/s)下不闪断;密度达到 EVE 参照的合理比例;晴天 GPU 空转粒子数为 0。

### 阶段 5:接入天气状态机与相机相对坐标

- [ ] **5.1** 复用现有 `Weather/VolkenWeather.cs`(**不要**引入全局天气档位,也**不要**引入"天气值"标量 —— `WeatherTypes` 与天气值状态机均已删除,见 [weather-cloud-decoupling](weather-cloud-decoupling-2026-10-01.md) §8「移除天气值」;雨只按 `rain.enabled` + 自己的参数跑)。
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
| 计划 §10.2 ⑰「buffer 容量与生效数量分离」 | **照抄**(§4 第 2 条),并补充"不能照抄 `ParticleAmount` setter"的原因 |
| 计划 §10.3(域半径自适应 + EVE 密度标定) | **照抄**,作为阶段 4 的验收基线(§3.5) |
| 教训 §3.1㈢ 的屏幕平面构轴骨架 | **采用**,并明确"该骨架尚未真机验证"→ **阶段 1 就是它的验证** |
| 教训 §3.1 的 27 条铁律 | 不再重述,阶段 0 逐条对照即可 |
| 配置层 / UI 占位 / 天气状态机 | **不动**(计划 §10.6 ㊶ / §10.6) |

---

## 8. 剩余开放问题(开工前不必解决,但要记住)

1. **随机 roll 的归位**(§3.1 开放问题):SP2 用矩阵第 3 列携带绕长轴滚转,改 2D 屏幕基后没有自然位置 → 阶段 3.4 显式补。
2. **`SV_InstanceID` 路径**(计划 §10.2 ⑫ 自注"推断,未真机验证")→ 阶段 2.3 验证。
3. **SR2 是否已有可复用的雨/粒子 API**:母计划 §10.4 A 已确认 `WindManager` / `WindVelocity` 在 `<JNO_CODE>` **零命中**、`IPlanetAtmosphereData` **无气流速度接口** → **风只能来自 `CloudConfig.windSpeed/windDirection`**。雨的"风"同理,需在阶段 4 接上。
4. **联机同步**:雨是否同步、由谁驱动 —— 暂定仅本机表现(母计划 §8 第 5 条)。
5. **性能边界**:480 万顶点/帧是本机 5060 Laptop 的上限;若目标机型更低,需在阶段 4 就下调上限。
6. **许可边界**:`ParticleDomain` / `ParticleHandler` 属 Jundroo;本计划只移植**逻辑与算法**,不搬运原 DLL 与商业资源文件;若雨声素材来自游戏资源,使用边界需自行确认(母计划 §8 第 1 条)。

---

## 9. 起步建议(若只做一件事)

> **做阶段 1。**
> 1~10 个粒子、CPU 算顶点、不碰 compute、不碰 shader —— 用最小的代价回答"屏幕平面构轴到底立不立得住"。
> 这一步通过,**后面 7 个阶段都是工程量**;这一步不通过,**后面 7 个阶段全是无效迭代**(上次 8 轮的教训)。

---

## 10. 实施记录(阶段 0 → 3)

> 本节**只留结论**:已落地物、当前参数、关键转弯点、已证伪思路、剩余项。
> 原 §10.1~§10.23 的逐轮调试流水(734 行)已于 2026-10-02 精简;方法论沉淀见[复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) §5。
> ⚠️ 旧雨/雾实现**不可回取**:`%TEMP%\volken-rain-fog-stash` 已被清理、**不存在**;重写只能依据[复盘 §6「已证伪思路清单」](archive/weather-rain-fog-postmortem-2026-09-27.md)。

### 10.1 已落地(阶段 0 → 3)

| 阶段 | 交付物 | 状态 |
|---|---|---|
| 0 护栏 | 诊断三件套(内部状态心跳 / **屏幕空间**量 / GPU 回读);复盘 §5 铁律逐条过 | ✅ |
| 1 朝向验证 | `RainAxisProbe.cs` 双算法对照(青 = 屏幕平面构轴 / 品红 = SP2 `AlignStreaks` 原样)+ 5 条 `volkenRainAxis*` | ✅ 真机证实 |
| 2 最小 compute 管线 | `RainParticles.compute`(`Randomize` / `Positioning` / `TranslateFixed` / `FillArgs`)+ `.cs` 驱动 + `_Positions` 回读 | ✅ 真机全绿 |
| 3 正式雨滴 shader | `RainParticles.shader`(SP2 属性名 + **世界系旋转矩阵构轴** + 随速度拉伸 + 软粒子 + 域边界淡出 + 程序化柔边贴图) | ✅ 真机全绿 |
| 调参入口 | 天气面板「雨」分组 **22 项**(真值来源 `weather.xml` 的 `RainSection`)。**约定:以后可调项一律进面板**,不再加控制台指令 | ✅ |
| 编辑器预览台 | `Assets/Scripts/VolkenTests/RainPreview.cs`(自由飞 + 同批字段 IMGUI + 「下次 Play 自动启动」),免去每轮 5 分钟打包 | ✅ |

**阶段 1 的核心实测结论(不再需要推导)**:模式 2 退化视角(雨朝相机飞)下,版本 A(屏幕平面构轴)**保持竖直细长条**(倾角 ≈0、长宽比 ≈25),版本 B(SP2 原样)**复现旧失败的"横块"**(倾角 ≈90)→ **"屏幕平面构轴立得住"成立**。① 常规视角 ② 退化视角 ③ 自定义气流 均已过;**④ 相机运动未测**。

### 10.2 当前参数与部署哨兵

SP2 雨的**出厂值(第一手:`sp2d4/Assets/Resources/prefabs/ParticleDomain.prefab` 序列化原值)**:

| 字段 | SP2 出厂值 | 备注 |
|---|---|---|
| `_domainRadius` | 50 | 米 |
| `_particleAmount` | **100000** | 本项目早期用 10000 → 密度差 **10 倍**(0.019 vs 0.191 个/m³),现已向 SP2 对齐 |
| `_fallSpeed` | 15 | 米/秒 |
| `_streakLength` / `_thickness` | 2.5 / 0.1 | 雨丝长 / 宽 |
| `_stretchAmount` / `_limit` | 0.045 / 3.5 | 随速度拉伸 / 上限 |
| `_mainLightIntensity` / `_occlusionThresholdAGL` | 1.2 / 100 | |
| 贴图 / compute | 软 blob / `BillboardParticles` | 本项目改为**程序化柔边**;SP2 的 `.asset` 是 `DummyShaderTextExporter` 占位模板,只有 Properties 块可信 |

**本项目特有的两项覆盖**:

- `ceilingAltitude`(**默认 12000m,0 = 关闸门**):SP2 最大缩放只到"半个岛",JNO 能缩到整颗星球 → 用相机海拔算 `altFade` 乘进 `_FadeAmount`,超上限不 dispatch / 不绘制(**太空零成本零雨**);**不与云层联动**。
- `respawnMirror`(**默认 false**):随机混合重生(理由见 §10.3 #5 / #6)。

**部署哨兵(判"包里装的是不是新版")**:`_ShaderVer`(当前 **3**)、`TranslateFixed=2`、`down·camUp ≈ -1`、`dist mean/R ≈ 0.75`;`SELFCHECK` 行打印 kernel 索引 / material / mesh / 贴图。⚠️ **不能用 `HasProperty` 判版本** —— 它看不见只在 HLSL 里声明的 uniform。

### 10.3 关键转弯点(每一次都是"证据推翻了上一版假设")

| # | 原假设 / 症状 | 实锤证据 | 转向 |
|---|---|---|---|
| 1 | 屏幕平面构轴可行 | 真机:朝向随相机**角速度**摆 + 像"贴在镜头上" | 废弃 → **SP2 世界空间旋转矩阵** `_RotationMatrix`(朝向锁世界系) |
| 2 | "减相机速度"能表达雨丝相对运动 | 位置回读:相对偏移**逐字节冻结**,绝对坐标跟着相机刚性平移 | 粒子**世界系下落**(去 camVel)+ 新增 `TranslateFixed` 换帧重定位 |
| 3 | 出域用 `frac` 回卷 | 整团云**坍缩到相机附近**(3 粒簇 < 4m) | 改为出域重生 |
| 4 | 球内位置 `r = R*u`(半径均匀) | 垂直往下看**糊屏** | `r = R*u^(1/3)` **体积均匀**(体密度 ∝ 1/r² → 粒子原本挤在球心) |
| 5 | 出域处置用"球内随机" | 近旁爆闪 | 试方案 B(镜像) |
| 6 | 镜像重生能消除爆闪 | 确定性 → **零混合**;同速下落使整片雨**刚性平移**,周期 = `2R/fallSpeed`(静止 **6.7s**、170 m/s 飞行时 **≈0.6s**)→ "像下面条一样一股脑下降" | **默认改回随机**;重生范围收成**壳层 [0.15R, R]** 体积均匀(兼顾混合与不爆闪);镜像保留为对照项并注明"不推荐" |
| 7 | 高度不设限 | JNO 相机能缩到整颗星球 → **太空里还下雨** | 加 `ceilingAltitude` + `altFade`(SP2 等价物 = `signedAltitudeAGL > 0` 才更新 + `CameraCloudFadeVal`) |
| 8 | `HasProperty` 判 shader 版本 | HLSL-only uniform **看不见** → 误判"没部署" | 加 `_ShaderVer` 哨兵 + `SELFCHECK` 全量诊断日志 |
| 9 | "位置在动"说明逻辑正确 | `w0` 探针**证伪**上一轮结论:所谓"kernel 空转"实为**游戏暂停**(`Time.time` 冻结) | 诊断必须同时输出**当前值 + 内部状态**;先排除暂停 |
| 10 | 参数真值来源从代码静态值换成 `weather.xml` | 配置层旧默认值与代码默认值**不一致**(`streakWidth` 0.1→0.05、数量 10000→20000)→ 观感突变 | 配置默认值对齐 SP2 出厂值 + `UpgradeUneditedDefaults` 升级未改过的旧值 |
| 11 | 每轮都要打包进游戏 | 一轮 5 分钟起,无法迭代观感 | 搬进**编辑器预览台**(正因依赖都写成"失败即降级"才能搬) |

### 10.4 已证伪思路(不要再试)

- ❌ **世界空间长度轴 + 任意宽度轴**(SP2 `AlignStreaks` 原样):视线接近长度轴即退化成"横块";`side ∥ 视线` 变"细线"。
- ❌ **流线轴做屏幕投影**(`StreamMode`):朝向随视角摆动 → 默认改回恒屏幕竖直,速度源改 `CraftScript.FrameVelocity`。
- ❌ **`frac` 回卷处理出域**:粒子坍缩到相机(贴合镜头的竖线)。
- ❌ **半径均匀分布** `R*u`:粒子挤在球心。
- ❌ **确定性镜像重生**:零混合 → 周期团块。
- ❌ **只修"雨丝形状 / 粗细"去解"一股脑下降"**:方向修错 —— 真问题是**整片雨的运动**成团、有节律(用户澄清)。
- ❌ **`HasProperty` 判版本**、**只看"位置在动"就判逻辑正确**、**把游戏暂停误诊为 kernel 空转**。
- ❌ **用 `.ps1` 文件承载非 ASCII 字面量**:PS 5.1 按 ANSI 读 → 语言文件词条变乱码(已复检 3 个语言文件 0 乱码);改本地化用编辑工具或内联命令。
- ❌ **控制台指令当调参入口**:9 条 `volkenRainP2*` 已全删,功能 100% 进面板。

### 10.5 剩余项 / 未验证

- **阶段 4(下一步)**:密度 0.139~0.191 个/m³ 标定、自适应域半径(`needed = camSpeed × 0.6`,clamp ≤ 400m)、`_mainLightIntensity`、遮挡剔除。
- **只有进游戏才能验**:软粒子、换帧重定位、真实海拔、配置持久化 → **定稿后必须打包一次**。
- 阶段 5:接入天气状态机 + 相机相对坐标(精度正解,替代"靠域环绕把浮点原点重置拉回")。
- 阶段 6 / 7:方案 B 遮挡(二期)、水花 / 雨声 / 图集变体。
- 阶段 1 的 `volkenRainAxis*` 5 条命令暂留(另一套构轴探针,无 UI 对应物);其 ④ 相机运动项未测。
- 可选:每粒子下落速度抖动(hash 派生 ±10~15%)进一步打散相干性 —— 纯观感决定,待与 SP2 实机对比后定。

### 10.6 环境事故与纪律(已按此执行)

- ⚠️ **命令行绝不写非 ASCII 路径 / 文件名**:曾因中文路径被 shell 编码破坏,`ReadAllText` 失败却未停 → **把 986 行文档覆盖成 1 节**(已用同目录 ASCII 全本镜像复原)。用编辑工具、或 `Get-ChildItem -Filter` 取 `.FullName`;脚本开头设 `$ErrorActionPreference='Stop'`,任何 `ReadAllText` 后必须确认成功。
- ⚠️ **Unity 陈旧编译状态**:AssetDatabase 缓存了磁盘上已删除的文件 → 大量 CS0234 级联错误、`Library/ScriptAssemblies` 停留旧版;聚焦窗口 / **Ctrl+R** 刷新即可(磁盘状态以 dotnet 编译为准)。
- ⚠️ **命名空间 `Volken.Debug` 会遮蔽 `UnityEngine.Debug`**(曾因此踩 CS0234):测试代码统一 `Volken.Tests`;测试与正式代码**单向依赖** —— 正式代码零引用,整个 `VolkenTests/` 可删除,契约见 `VolkenTests/README.md`。
