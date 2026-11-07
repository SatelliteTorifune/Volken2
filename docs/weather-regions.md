# 地区天气设计

> 设计核对：2026-10-24。参考仅经静态分析；当前行为见 [天气](weather.md)，实施 / 验收见 [待办](backlog.md#其他未排期项)。

## EVE 参考

对象为 EVE Redux 3.1.1 / `Atmosphere.dll`，配置见 `<KSP_GAME>/GameData/StockVolumetricClouds/Clouds/{clouds,particleFields}.cfg`；[路径映射](LOCAL_PATHS.md)，本机分析留在 `<PROJECT>/tools/local-ksp-weather-reference/`。

| 入口 | 已核实的做法 |
|---|---|
| `Kerbin-Weather-1` | `coverageMap` 控制覆盖，`cloudTypeMap` 插值 Fog / Rain / Thunder 等类型，分别配置高度、密度与效果强度 |
| `CloudsRaymarchedVolume.SampleCoverage` | 变换位置后采样覆盖 / 类型，乘高度曲线和时间淡变；CPU 不查完整三维噪声、不逐帧回读 GPU |
| `ParticleField.Update` | 覆盖经阈值映射再乘类型粒子强度；声音、水珠另取类型倍率，水珠保留干燥过程 |
| `Lightning.Spawn` | 候选点投影到球壳，覆盖 >0.1 后按类型 `lightningFrequency` 随机接受；此处为概率筛选，不是每秒次数 |
| `TimeSettings.GetFadeForUT` | 游戏 UT 决定周期、偏移、持续与淡变，配合层运动形成变化 |

天气体积覆盖近地至云底，近景另挂雨粒子；雪层 `fxOnlyLayer` / 零密度支持只驱动效果。CPU 查询不等同逐点视觉密度；未验证 KSP 画面或完整气象模拟，空白 / 注释配置不作功能证据。

## 分布与采样契约

同一行星可同时有晴区、雨区、雾带与雷暴。地区模式默认关闭，关闭时恒等调制，保留全局兼容；采样只调制运行态，不回写用户开关、外观或限高。

- 天气层持有覆盖、类型、高度曲线、运动与时间包络；来源为程序化或可选贴图，类型连续插值，雨 / 雾 / 雷倍率独立。缺图走程序化，损坏 / 非有限数据不能静默把全星球乘零。
- 输入行星位置、高度、统一时间，输出效果强度。双精度坐标处理自转、风、浮动原点；CPU / GPU 共用方向、UV、插值与时间，支持极区、接缝和不同半径。
- 数据归现有天气驱动，复用云的行星事件；每相机采样，主视图独占雨声。缓存按行星 / 预设 / 数据版本失效；格式、多层重叠与范围在实现前确定。

曲线 / 阈值按本项目标定：

```text
局部覆盖 = 分布覆盖 × 类型高度曲线 × 时间包络
雨强度 = saturate((局部覆盖 - 下阈值) / (上阈值 - 下阈值)) × 类型粒子强度
```

阈值须有限且递增；CPU 用可读分布和预烘焙曲线，不以完整云 raymarch 或同步回读作为常规查询。

## 效果接入边界

| 消费者 | 拟议接入 |
|---|---|
| 雨 / 水花 / 水珠 | 固定容量，调制有效粒子或生成概率；停雨后残留按寿命消退，不随强度反复重建 buffer |
| 雨声 | 主视图雨量控制混音，保留音量与环境门控；PIP 不覆盖 |
| 雷电 | 查询候选发生点，允许晴区看邻区雷暴；保留限高、动画、暂停与雷声 |
| 雨幕 / 地区雾 | 沿视线采样分布以表示远处区域；相机倍率不能替代空间积分 |
| 云 | 显式共享分布 / 对齐运动，预设与开关独立，不回写云参数，全球图可选 |

先接效果、后接可见体积。入口为 `VolkenWeather.Tick`、`RainParticles`、`LightningModule.CastRandomBolt`、`FogRenderer`；源码 / 渲染契约见 [天气](weather.md)，迁移见 [架构](architecture.md#配置与预设)。

查询、内存、体积积分及切换峰值按 [测量方法](development.md#按改动选择验证) 实测。
