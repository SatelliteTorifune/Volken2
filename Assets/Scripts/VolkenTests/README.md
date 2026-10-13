# VolkenTests —— 测试 / 开发工具（与正式代码相对独立）

这个文件夹**只放测试与开发工具**，特点是：**正式代码对它零引用**。

## 契约（重要）

1. **单向依赖**：本文件夹里的代码**可以**引用 `Assets/Scripts/Volken/**`（正式代码）；
   **正式代码不得引用本文件夹**（`Mod.cs` / `VolumetricClouds.cs` / `RainParticles.cs` 等文件不引用
   `Volken.Tests` / `ProfilerController` / `NoiseVisualizer` 等类型）。
2. **可整体删除**：删掉整个 `VolkenTests` 文件夹后，mod 仍能编译并正常运行（只是少了这些工具与命令）。
   所以想"彻底不打包进游戏"，直接删文件夹即可。
3. **自发创建工具**：本文件夹里的工具不靠正式代码启动，而是由 `TestsBootstrap.cs` 用
   `[RuntimeInitializeOnLoadMethod]` 自发创建（剖析器覆盖层）。**控制台命令只在
   `Mod.RegisterCommands()` 注册，本文件夹不再注册任何命令。**
4. 命名空间统一用 **`Volken.Tests`**（`Profiler/` 子目录保留其独立命名空间 `VolkenProfiler`），
   与正式命名空间（`Volken.*`）分开，便于一眼分辨。

## 内容

| 文件 | 用途 | 入口 |
| --- | --- | --- |
| `NoiseVisualizer.cs` | 遗留体积云噪声可视化组件（`[ExecuteInEditMode]`） | 当前项目缺少 `Hidden/NoiseVisualizer` shader,需补齐后使用 |
| `RaymarchDebug.cs` | 遗留体积云 raymarch 步进组件 | 当前项目缺少 `Hidden/RaymarchDebug` shader,需补齐后使用 |
| `Profiler/` | 性能剖析覆盖层（帧耗时/面板） | 由 `TestsBootstrap` 自动创建；读取开发配置 `ShowProfiler`,设置页入口保留 |
| `TestsBootstrap.cs` | 上述工具的自发创建入口 | 自动（`RuntimeInitializeOnLoadMethod`） |

## 校验"零引用"（改完正式代码后可自查）

2026-10-12 核对:`RainPreview.cs` 已不在当前项目中,旧文档的 Add Component、自动启动、参数记忆与 XML 复制入口均已失效。雨参数使用游戏天气面板调整;历史实现与验证结果仍见[雨计划 §10.8](../../../docs/archive/sp2-rain-particledomain-port-2026-09-28.md#108-gpu-雨滴碰撞与水花2026-10-08)。

```powershell
# 期望输出：只有注释行，没有任何代码引用
Get-ChildItem Assets\Scripts -Recurse -File -Include *.cs |
  Where-Object { $_.FullName -notmatch '\\VolkenTests\\' } |
  Select-String -Pattern 'Volken\.Tests|ProfilerController|VolkenProfiler|NoiseVisualizer|RaymarchDebug'
```

## 历史

- 2026-10-09 [用户 UI 清理](../../../docs/archive/user-ui-cleanup-2026-10-09.md):游戏雨 / 雾日志按钮和诊断视图移除,当时保留编辑器雨预览及 GPU 探针;GPU 探针已在后续清理中删除;旧记录的游戏按钮入口已失效。`ShowProfiler` 仍读取已有 XML,默认 false;当前源码保留设置页入口。

- 2026-10-08 日志清理:雨 / 雨声不再自动心跳,雨粒子与水花 GPU 回读由“把状态写入 Console”按钮触发,最小间隔 10 秒;游戏天气面板的日志按钮行为一致。GPU 数量与重生数显示最近一次手动采样,−1 表示尚未采样。

- 2026-09-29 建立：原先散落在 `Assets/Scripts/Volken/Debug/`、`Assets/Scripts/Volken/Weather/RainAxisProbe.cs`、
  `Assets/Scripts/Volken/Profiler/` 的测试/开发脚本统一搬到这里；同时把 `Mod.cs` / `VolkenMod.cs` 里
  对这些脚本的 7 处引用**全部摘除**（改为自发注册），使正式代码与测试彻底解耦。
- 2026-10-03 控制台命令精简：本文件夹内的 `DevConsoleApi.RegisterCommand` 全部删除，命令只剩
  `Mod.RegisterCommands()` 的 3 条（`frs` / `brs` / `VolkenForceRefresh`）。
- 2026-10-03 清理死代码：删 `RainAxisProbe.cs`（构轴结论已定案，删命令后无人调用）、
  `RainAudio.SetEnabled/SetVolume`、剖析器的 CSV 录制链（`ProfilerSession.BeginCapture/FinishCapture/
  CaptureActive/CaptureLimit/FrameSample`，唯一入口是被删掉的 `VolkenProfiler.Capture` 命令）。

## 已移除的编辑器验证工具

【决策:2026-10-09】按用户要求删除 `Editor/` 整个目录:雾 GPU、性能、昼夜光照与水面水花四个验证脚本及对应 `.meta`,同时删除目录的 `.meta`。Unity 菜单与命令行执行入口随脚本移除。

历史验证结果保留在[雾记录 §9、§11、§13](../../../docs/fog-implementation-plan-2026-10-09.md)与[水面修复 §3](../../../docs/rain-water-splash-fix-2026-10-09.md#3-验证)。这些是删除前的测试证据,不再提供当前可执行的编辑器工具入口。
