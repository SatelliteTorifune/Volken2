# VolkenTests —— 测试 / 开发工具（与正式代码相对独立）

这个文件夹**只放测试与开发工具**，特点是：**正式代码对它零引用**。

## 契约（重要）

1. **单向依赖**：本文件夹里的代码**可以**引用 `Assets/Scripts/Volken/**`（正式代码）；
   **正式代码不得引用本文件夹**（`Mod.cs` / `VolkenMod.cs` / `RainParticles.cs` … 里不允许出现
   `Volken.Tests` / `RainAxisProbe` / `ProfilerController` / `RainPreview` 等类型名）。
2. **可整体删除**：删掉整个 `VolkenTests` 文件夹后，mod 仍能编译并正常运行（只是少了这些工具与命令）。
   所以想"彻底不打包进游戏"，直接删文件夹即可。
3. **自发注册**：本文件夹里的工具不靠正式代码启动，而是由 `TestsBootstrap.cs` 用
   `[RuntimeInitializeOnLoadMethod]` 自发注册命令/创建工具（原来写在 `Mod.RegisterCommands()` 里的
   测试命令与剖析器入口已全部搬到这里）。
4. 命名空间统一用 **`Volken.Tests`**（`Profiler/` 子目录保留其独立命名空间 `VolkenProfiler`），
   与正式命名空间（`Volken.*`）分开，便于一眼分辨。

## 内容

| 文件 | 用途 | 入口 |
| --- | --- | --- |
| `RainPreview.cs` | **编辑器内雨预览台**：自由飞相机 + 参数面板即时生效 + 地面/参照物；跑的是同一份 compute/shader/驱动代码 | Unity 里 Add Component「Volken Rain Preview」→ Play（可勾「下次 Play 自动启动」写 PlayerPrefs，仅编辑器生效） |
| `RainAxisProbe.cs` | Phase 1 雨丝构轴 A/B 对照探针（屏幕平面构轴 vs SP2 世界系构轴） | 命令 `volkenRainAxis` / `volkenRainAxisOn 1` / `volkenRainAxisMode n` / `volkenRainAxisVec x y z` / `volkenRainAxisCount n`（`On 1` 会自行挂到当前视图相机） |
| `NoiseVisualizer.cs` | 体积云噪声可视化（`[ExecuteInEditMode]`） | 场景里挂组件 |
| `RaymarchDebug.cs` | 体积云 raymarch 步进调试 | 场景里挂组件 |
| `Profiler/` | 性能剖析覆盖层（帧耗时/面板） | 命令 `VolkenProfiler` / `VolkenProfiler.Capture`；由 `TestsBootstrap` 自动创建 |
| `TestsBootstrap.cs` | 上述命令/工具的自发注册入口（重试至控制台就绪） | 自动（`RuntimeInitializeOnLoadMethod`） |

## 校验"零引用"（改完正式代码后可自查）

```powershell
# 期望输出：只有注释行，没有任何代码引用
Get-ChildItem Assets\Scripts -Recurse -File -Include *.cs |
  Where-Object { $_.FullName -notmatch '\\VolkenTests\\' } |
  Select-String -Pattern 'Volken\.Tests|RainAxisProbe|ProfilerController|VolkenProfiler|RainPreview|NoiseVisualizer|RaymarchDebug'
```

## 历史

- 2026-09-29 建立：原先散落在 `Assets/Scripts/Volken/Debug/`、`Assets/Scripts/Volken/Weather/RainAxisProbe.cs`、
  `Assets/Scripts/Volken/Profiler/` 的测试/开发脚本统一搬到这里；同时把 `Mod.cs` / `VolkenMod.cs` 里
  对这些脚本的 7 处引用**全部摘除**（改为自发注册），使正式代码与测试彻底解耦。
