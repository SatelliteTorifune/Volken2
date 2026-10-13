# 雷声兜底声速删除与配置审查

> 状态:✅ 用户已授权删除,6 个实现文件完成修改;正式 C# 编译通过,63 项独立核心检查通过,游戏听感尚未验收。其他配置候选只完成审查,未删除。
> 日期:2026-10-14
> 定位:删除“兜底声速(米/秒)”,统一观察者大气取样,并审查其他内部保护 / 遗留参数。
> 关联:[原雷声实现](thunder-realism-2026-09-28.md) §2、§5.4、[待办索引](../README.md#四之三待办清单backlog)。

## 1. 删除前用途与证据

`LightningModule.cs:514` 先读取游戏 `CraftNode.CraftScript.FlightData.AtmosphereSample.SpeedOfSound`,仅在结果≤1m/s 时使用 `thunderFallbackSpeedOfSound`;正常有效游戏声速不受该滑块影响。`VolkenWeatherConfig.cs:266` 的默认值为 343,同文件 CopyFrom / ClampAll 复制并限制字段;`WeatherPanel.cs:435` 提供 50~1500m/s 滑块,三语均有同名文案。

复核 `<JNO_CODE>/ModApi/Planet/PlanetAtmosphereData.cs:898` 的 `SampleAltitude`:仅 `HasPhysicsAtmosphere && altitude<Height` 时给 SpeedOfSound 赋值,否则保持 0;有效值依游戏平均表面温度、平均成分计算,不是当前高度温度。`IPlanetAtmosphereData.cs:147` 暴露 `SampleAltitude(double)`,可以直接在观察者海拔查询,无需自行建立声速公式。

现有天气环境只允许物理大气行星激活(`VolumetricClouds.cs:256`,`VolkenWeather.cs:364`),普通无大气行星已被挡住。但该门控不是观察者局部大气检查,雷电海拔上限也可关闭,不足以代替声音传播介质判断。

两项源码缺口与删除方案相关:

- 声程 / 音源观测者为传入相机位置,声速却使用飞船大气样本(`LightningModule.cs:492`、`:536`)。飞船在轨道、镜头在地面时,会错误触发人为兜底;反过来也可能对太空镜头使用飞船所在处的非零声速。本次确认代码口径不同,未在游戏中复现听感。
- `c<=1` 不会拦住 NaN / 正无穷,字段 Clamp 也没有有限值处理。`Mathf.Lerp(thunderDelay,pathDist/c,at)` 会先计算除法,即使 at=0 也不能靠乘零保证无效结果消失。直接删掉兜底分支后继续除零不可行。

原文“必须兜底”的要求应改为“必须避免无效除法”。真空不需要人为指定 343m/s 来发出外部雷声,缺失 API 数据也应与真空区分。声波需要传播介质的依据见 [NASA 机械波说明](https://science.nasa.gov/ems/02_anatomy/)。

## 2. 两种删除范围

| 范围 | 结果 | 评估 |
|---|---|---|
| 只删滑块,内部保留常量 343 | 面板变简洁,真空 / API 失败仍按人为声速调度 | 可做,但仅隐藏配置,没有去掉兜底逻辑 |
| 删除字段、滑块及回退分支,仅用有效游戏大气样本 | 正常雷声延迟保持游戏口径;无传播介质或数据无效时不新排雷声 | 建议采用,需同时统一观察位置并处理无效值 |

## 3. 实施方案与改动范围

【决策:2026-10-14】用户在可行性分析后要求动手,采用 §2 的完整删除方案,不保留隐藏常量声速。其他参数只审查,不自动扩大删除范围。

在一次雷击落点回调中,使用实际 `observerPos` 与当前参考系行星中心 / 半径计算海拔,查询该行星 `AtmosphereData.SampleAltitude`。仅在物理大气有效、样本声速为有限正数时计算声程除法;观察点在大气顶以上 / 真空时跳过该次新雷声,闪电视觉保持。场景 / 参考系 / 行星数据取不到时同样跳过该次雷声,下次正常重新查询,不跨行星复用旧声速或新增常量。该调用频率为每次落雷,不加入逐帧采样。

保留 `thunderDistanceAttenuation=0` 的固定延迟模式;在有效大气中直接取 `thunderDelay`,不先计算 `pathDist/c` 再 Lerp。其余模式继续按既有声程 / 声速与音量公式,并保证最终 delay 有限。near/far 素材、云底声程混合、3D 衰减、多声部、DSP 排程与暂停机制沿用现有实现。

删除点共 6 个实现文件:LightningModule 的读取 / 回退与旧注释,VolkenWeatherConfig 的字段 / CopyFrom / ClampAll,WeatherPanel 的滑块,以及三语 XML 的 `Volken.UI.ThunderFallbackSpeedOfSound`。不新增替代配置,不修改用户预设文件。

现有加载器直接使用 XmlSerializer,没有未知节点拒绝处理或 XmlAnyElement 收集。Unity 中实际配置类的验证确认,旧 XML 的同名节点作为未知节点忽略,显式保存不再写出;删除节点的旧文件也可加载。未知元素机制参考 [Microsoft XmlSerializer.UnknownElement](https://learn.microsoft.com/en-us/dotnet/api/system.xml.serialization.xmlserializer.unknownelement?view=netframework-4.8)。

删除字段 / 滑块本身没有可感知性能收益;观察者大气查询增加一次落雷级别的 CPU 计算,不新增 GPU 工作、纹理、声音素材或每帧分配。未测运行耗时,不以本评估承诺 FPS 提升。

## 4. 实施前后判据

- 地面 / 云内正常大气下,固定距离的游戏声速与雷声延迟保持;至少覆盖两种大气成分不同的行星。
- 相机与飞船分处大气内 / 外,按相机位置选择是否新排外部雷声,不由飞船样本触发 343m/s 回退。
- 零 / NaN / Infinity、缺场景或参考系数据不进入除法与 PlayScheduled;恢复有效数据后后续雷击正常。
- 固定延迟模式在有效大气中保留,near/far、声程混合、音量、暂停及落雷视觉不受删除影响。
- 旧 XML 带节点可加载,删除节点亦可加载,CopyFrom / 保存不再带该值;三语滑块均消失。

本次针对新雷声的声速取得与排程;已排队雷声在镜头进入真空后是否取消、跨大气声程、海面以下的不同介质均不属于本次删除方案。没有运行修复后的完整游戏或声频测试。

## 5. 实施与验证结果

- `LightningModule.TryGetSpeedOfSound(observerPos,...)` 按当前参考系行星中心 / 半径求相机海拔,再读游戏 `AtmosphereData.SampleAltitude`。声速必须为有限正数;缺数据、真空、异常取样均返回失败,本次不新排雷声,不创建人为声速或跨行星缓存。声程的云底高度差也复用同次观察者海拔,不再读取天气每帧缓存高度。
- 固定延迟模式使用条件分支直接取 thunderDelay,其余模式继续 Lerp 既有声程除以游戏声速;非有限最终 delay 不传给 AudioSource。near/far、音量、DSP 播放、暂停与落雷动画代码保持。
- 配置字段、CopyFrom / ClampAll、面板滑块和三语键 `Volken.UI.ThunderFallbackSpeedOfSound` 均已移除;正式 Assets 中已无旧字段 / 标签引用。用户预设未重写。
- 正式项目使用真实 Unity / 游戏程序集编译,0 错误,仅星环既有两条 CS0162。Unity 2022.3.62f3 的隔离工程执行正式配置类和逐字提取的三段雷声核心方法,**63 通过 / 0 失败**;场景 / 大气返回值为最小桩,音频排程被截获,没有把该结果当作完整游戏听感验收。
- 检查覆盖平移中心 / 非世界 Y 径向、相机与飞船大气状态分离、340 / 931m/s 的合成样本延迟、云底声程混合、near/far 与音量、固定模式旁路除法、零 / 负 / NaN / Infinity、缺场景 / 参考系 / 大气、异常后恢复。实际 XmlSerializer 可忽略旧声速节点中的非数字文本,保留其余参数,复制 / 保存往返不写出已删字段;三语 XML 可解析且无旧键。

证据位于被忽略的 `.utmp/lightning-validation/result-thunder-removal.txt / thunder-removal.log`,临时验证工程未添加到正式 Assets。测试的核心方法与正式源文一致,配置副本与正式文件相同。游戏复测仍按 §4 的场景与听感判据进行,未测整机耗时。

## 6. 其他配置字段审查

范围为 `CloudConfig`、`VolkenWeatherConfig`、行星映射 `PlanetConfig`、`ModSettings` 及对应 UI / 运行消费者。删除兜底声速后未发现另一项同样以自定义数字代替无效物理 API 返回值的用户配置;内部空值 / 有限值检查不因此删除。

| 候选 | 实际用途与证据 | 建议 / 当前状态 |
|---|---|---|
| `CloudConfig.depthThreshold` | 只有默认 / CopyFrom 与 `CloudLayer.cs:93` 的 SetFloat;正式云 shader / include 没有同名 uniform。Composite 的同名局部常量 5000 与此字段无关,无 UI | 已无效果消费者,优先删除候选;本轮未改 |
| `VolkenWeatherConfig.overall.enabled` | OverallSection 仅构造、补空、复制和 XML 保存;天气运行只看独立效果开关与环境门控 | 历史兼容残留,可精简整个 Overall 节;此前会话明确保留兼容,本轮未改 |
| `rain.respawnMirror` | `WeatherPanel.cs:235` 仍展示“出域镜像重生”;`RainParticles.cs:1158` 实际切换 compute 的重生模式,配置注释标为仅对照用 | 开发对照参数,适合移出普通面板;有真实消费者,不能当死字段直接删,本轮未改 |
| `CloudConfig.nearThreshold` | `CloudRenderer.cs:408` 绑定 `_NearThreshold`;`Clouds.shader:1015` 的 Standard / 无雾分支按场景深度把近景云颜色和消光最低缩到 20%,默认预设为 100km,无 UI | 最接近保护画面的经验阈值;后续需验证几何遮挡并替换该近景修正,不能只删字段而留下不确定合成行为,本轮未改 |

以下参数仍有直接效果或质量用途:雨 / 雷 `ceilingAltitude`、雨 `ceilingBand`、`underwaterGate / underwaterFade`、`stretchLimit`、雾 `maxOpacity / startDistance`、云 `historyDepthThreshold` 与采样质量 / 分辨率。它们控制出现范围、淡出、雨丝形状、雾的外观或历史接受,并不是声速一类的人为 API 兜底。`fog.diagnostics / debugMode`、`orbitDebugMode` 是开发诊断;`PlanetConfig.LegacyWeather` 正在执行旧内联预设迁移,删除会丢失迁移入口;这些均保留。
