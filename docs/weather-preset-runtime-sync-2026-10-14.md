# 天气预设切换后的雨参数与雷电排程同步

> 状态:✅ 原工程修复已接入,C# 编译与 36 项独立 Unity 集成检查通过;游戏部署后表现待验收。
> 日期:2026-10-14
> 触发:切换天气配置后,雨和雷似乎需要手动 toggle 才生效;先核对源码 / 日志,再按用户要求修复。
> 关联:[预设下拉](archive/weather-preset-dropdown-stale-2026-10-04.md)、[雨启停 / 切场景](archive/rain-toggle-scene-switch-2026-10-02.md) §3、[天气解耦](archive/weather-cloud-decoupling-2026-10-01.md) §8。

## 1. 原因与游戏证据

**雨:**`ApplyPreset` 替换 `Config` 后未调用 `RainParticles.ApplyConfig`;`PlanetConfigLoaded` 在当前源码中没有订阅者负责同步雨。每帧 `SyncToCurrentView` 会在配置关闭时清零 `Enabled`,但配置开启且 `_current` 已存在时直接返回,没有重新开启或更新静态参数。手动雨开关会执行 `ApplyConfig` / `SetEnabled`,因而补上遗漏。

2026-10-14 核对最后写入时间为 04:55:11 的游戏日志:`None → Stormy` 加载成功且记录 `rain=True`,随后运行摘要仍为 `rainEnabled=False`;切到 `sb` 时保存 XML 的 amount 为 100000,运行摘要仍为 50000。这是配置已加载而雨运行态未同步的直接证据。

**雷:**预设切换已调用 `UpdateLightning`;日志确认 `None → Stormy` 后雷暴循环启动。两个雷电均启用的预设之间切换时,`LightningModule.SetActive(true)` 因 `_running` 已为 true 而早退;`StormLoop` 仅在每轮开始时读取间隔并抽取一次等待时间,当前等待不随配置变化。已保存的 Stormy 为 10~40 秒,sb 为 0.5~1 秒,短间隔预设可能继续等旧长间隔;手动关开会重启循环并重新抽取。日志不足以证明切换后雷电永久停止。

## 2. 实现与边界

【决策:2026-10-14】把配置应用放在加载路径,不靠面板 toggle 补同步。切换启用中的雷电预设只重排下一次落雷,不清理正在播放的闪电 / 雷声。

| 位置 | 改动 |
|---|---|
| `VolkenWeather.ApplyPreset` | `Config` 替换后调用 `RainParticles.ApplyConfig` 和 `SyncToCurrentView`,更新开关 / 静态参数及全部存活相机实例,相机缺失时沿用 tick 的挂载重试 |
| `VolkenWeather.SetActive / UpdateLightning` | 增加默认 false 的 `refreshSchedule` 传递;预设加载显式请求刷新,即使环境激活态没变也同步雷电。只调用一次更新,避免重复创建起始倒计时 |
| `LightningModule.SetActive` | 已运行且请求刷新时停止旧雷暴协程、启动新协程并读取新间隔;不进入停用分支,因此保留在播闪电、待播雷声和参考系基准 |
| `ResetCurrentConfigToDefault` | 同步应用默认雨参数 / 关闭状态及雷电启停,避免重置后仍保留旧静态参数 |
| `PlanetConfigLoaded` | 通知发生在配置及运行态应用之后;订阅者看到一致状态,现有事件接口保持 |

普通 `RefreshActiveState` 和开关仍使用默认刷新参数,已运行时不反复重置倒计时。另存为只改名称与文件,不重排正在运行的天气;同名切换和缺失文件的既有拒绝规则保持。雨 buffer / 网格只按原 `ApplyConfig` 的容量或几何变化重建,没有新增逐帧全量套参、配置字段、UI、shader、相机或 GPU 回读。

## 3. 本次验证

- `dotnet build Volken.csproj --no-restore --verbosity quiet` 通过:0 错误,3 条既有警告(程序集版本冲突和星环不可达代码)。
- 独立 Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop GPU Play Mode 集成检查:**36 通过 / 0 失败**。复制完整当前 `VolkenWeather`、`RainParticles`、`RainCollision`、`LightningModule`、`LightningBolt`、`GamePause`、`VolkenWeatherConfig` 及所需 shader;运行真实 `SwitchPreset`、雨 GPU buffer 重建和 Unity 协程。
- 游戏 SDK / 环境 / 资产加载使用替身,雨声只核对传给消费者的参数;XML 根路径通过测试副本的 Application 别名定向到工作区,使用原序列化 / 读写逻辑,没有改游戏预设或行星清单。
- 覆盖关闭配置不挂雨 / 不创建雷、关→开、开→开、开→关、保留既有主相机实例、主 / PIP 实际 buffer 数量更新、雨几何 / 音量输入同步、加载完成事件的一致性。
- 使用 120 秒与固定 0.5 秒的测试 XML,确认开→开立即替换旧等待,保留正在动画中的真实闪电对象及待播雷声排程;普通状态刷新、同名预设和另存为不重排计时;关闭时仍清理闪电 / 雷声。
- 覆盖缺失 XML 不创建文件、游戏暂停仍冻结计时、暂停中切配置、恢复默认及重新启用、相机暂不可用后由正常 tick 自动挂载。
- 工程与结果在被忽略的 `.utmp/weather-preset-validation/`,最终日志 `unity-verified-final.log`,摘要 `result.txt`;测试工具未加入正式 `VolkenTests/Editor`。

## 4. 游戏验收

更新模组程序集后,同一飞行场景按 `None → Stormy → sb → Stormy` 切换,不操作雨 / 雷开关。核对雨立即开关、粒子数量 / 几何 / 雨声随配置变化,主视图与 PIP 一致;雷电按新间隔等待下一次落雷,正在播放的闪电 / 雷声正常收尾。检查暂停、相机替换、离场恢复及默认配置重置。

本次修改为 C# 运行态同步;独立检查不代替游戏画面、音频听感或性能验收。雷电仍按新配置的随机间隔等待,切换预设不强制立即劈一道雷。
