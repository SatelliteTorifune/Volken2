# VolkenTests 开发工具

本目录只放开发工具，正式代码不得引用这些类型；移除整个目录不能影响模组正常编译和运行。工具可以引用 `Volken` 正式实现，命名空间为 `Volken.Tests`，Profiler 保留 `VolkenProfiler`。

## 当前内容

| 文件 | 用途 / 入口 |
|---|---|
| `TestsBootstrap.cs` | `RuntimeInitializeOnLoadMethod` 自动创建 Profiler，不由正式代码启动 |
| `Profiler/` | 整帧耗时覆盖层，读取 `ShowProfiler`，默认 false，设置页入口保留 |
| `NoiseVisualizer.cs` | 遗留噪声组件；当前缺少 `Hidden/NoiseVisualizer` shader |
| `RaymarchDebug.cs` | 遗留步进组件；当前缺少 `Hidden/RaymarchDebug` shader |

控制台命令统一由 `Mod.RegisterCommands()` 注册，含 `frs` / `brs` / `VolkenForceRefresh`；当前工具见[开发与验证](../../../docs/development.md#当前工具与诊断)。

## 已删除的入口

`RainPreview`、`RainAxisProbe`、雨方向临时诊断、Profiler CSV 录制链和 `Editor/` GPU / 性能探针已删除，不再提供 Add Component、菜单或旧命令入口。雨参数用游戏天气面板；显式开发诊断与历史测试摘要见 [开发与验证](../../../docs/development.md)。

当前雨与水花契约见 [天气](../../../docs/weather.md)，游戏回归矩阵见 [待办](../../../docs/backlog.md#游戏验收)。历史验证通过不代表工具仍存在。

## 正式代码引用检查

在项目根目录执行，人工区分注释中的说明与真正代码引用：

```powershell
rg -n 'Volken\.Tests|ProfilerController|VolkenProfiler|NoiseVisualizer|RaymarchDebug' Assets/Scripts -g '*.cs' -g '!**/VolkenTests/**'
```
