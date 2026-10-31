# 开发、验证与文档维护

> 核对日期：2026-10-14。入口以注册代码为准；验证与游戏部署分开记录。

## 开发与资源

- 源码与开发工具依赖见 [架构](architecture.md#项目与入口)，移除工具不影响正式编译 / 运行。
- 新功能默认关闭或恒等，保留回退；配置遵守 [配置契约](architecture.md#配置与预设)，核对实际消费者，三语键前缀 `Volken.UI.*`。
- shader / compute 按子系统放 `Shader/`；移动资产带 `.meta`，同步加载路径与 `Assets/ModData.asset` 的 `_otherAssets` GUID。资源登记或本机加载成功不等于已经入游戏包。
- 正式环境由 `Mod.LoadVolkenAsset` 使用模组 `ResourceLoader.LoadAsset<T>`；编辑器优先 AssetDatabase。新增 shader / compute 需要更新资源包，只有程序集更新不能替换旧 shader。
- 渲染避免 LINQ、临时集合与 GPU 同步回读；资源归属见 [架构](architecture.md)。

## 当前工具与诊断

`frs`、`brs`、`VolkenForceRefresh` 及下述诊断命令见 `Mod.RegisterCommands`；刷新不绕过同行星配置早退。

[VolkenTests](../Assets/Scripts/VolkenTests/README.md) 保留 Profiler 自动启动；已删 Editor 探针与菜单不可照历史记录执行。

| 入口 | 使用范围 |
|---|---|
| `Mod.Log` | 唯一日志接口；诊断及故障均受 `DevMode` 控制、默认静默；Unity 日志调用仅在其内部 |
| `orbitDebugMode` | XML 开发分屏；不输出轨道参数、投影或覆盖率日志 |
| `fog.debugMode` | XML 开发视图，排查后恢复 debug=0 |

云性能临时采样、冻结输入、省略渲染阶段、NoSolar 变体和照明 A/B 面板均已移除；整帧覆盖层仍由独立 Profiler 提供。正式渲染不持有基准测试状态，不修改 VSync / FPS 限制。

源码标识：云 shader 22 / lightning 2，噪声 `VolkenWorleyFBM` 且两张 128³ 纹理各 8 级 mip，雨 shader 10，镜头水珠 shader 2，雾 shader 3。部署须核对程序集 / 资源，`build` 不能代替 shader 标识。

游戏日志：`<USERPROFILE>/AppData/LocalLow/Jundroo/SimpleRockets 2/Player.log`；shader 编译查 `Editor.log`。本机映射见 [LOCAL_PATHS.md](LOCAL_PATHS.md)。排除暂停，记录配置、运行态、触发计数与资源标识；连续三轮无可信改善时换方向。

## 已有验证证据

本机 GPU 指 Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop。独立验证使用 SDK / 资源替身；历史结果不表示复跑，检查数不可跨版本 / 颜色空间相加。

| 范围 / 日期 | 保留的证据 | 实际限制 |
|---|---|---|
| 云直算清理 / 2026-10-24 | `.utmp/cloud-direct-cleanup-validation/`：C# 0 错误；shader 22 的 13 pass ×2 空区组合编译通过，Gamma / Linear 各 16 个体积 / 8 个上采样场景，RGBA / 深度 / MV / 雷光及上采样差 0，临时探针已删除 | 合成纹理 / NewConfig2 参数的独立回归，未验游戏部署或 FPS。撤销前复用短测 GPU P50 2.0406→2.1025ms 且有明暗分层，用户判为失败；只保留负结果防止误用 |
| 云合成 / 历史资源 | `.utmp/cloud-{history,composite}-validation/result-{Gamma,Linear}.txt`：2026-10-20 历史各 39 场景 / 303 帧 / 7180 检查、RGBA 差 0、复制 3N→N+1；2026-10-19 合成各 171 场景 / 1538 检查、像素差 0、无雾 / 有雾少 2 / 1 次复制；C# 0 错误 | 本机 GPU；正式代码 / shader、替身输入，含多层 / 多相机、历史失效、雾 / 雷光、HDR / AA / 缩放 / 编码及异常清理；未验游戏部署或 FPS |
| 云阳光遮蔽 / 局部投影 / 2026-10-15 | `.utmp/cloud-native-shadow-validation/` 各 256 配置、Linear 800 距离；`cloud-native-shadow-design-probe/` 各 621 世界点近远差 0、64 契约；无扩展 / 遮蔽回归 128 / 248 项，C# 0 错误 | Unity 2022.3.62f3 / D3D11；SDK / 行星替身，含 CloseFit / StableFit、0~4 级、EVA、矩阵 / 剔除 / 嵌套恢复；手动剔除只验字段。历史 build 16 游戏机体影消失，build 17 画质 / 几何开销待复测 |
| 雨计数 / 云影短路 | 2026-10-23：`.utmp/gpu-fastpath-validation/result-{Gamma,Linear}.txt` 各 119 场景通过，云影像素与雨数据一致，C# 0 错误；30秒×3，中位雨 0.0166→0.0113ms、云影 0.1298→0.0891ms；2026-10-14 `.utmp/rain-splash-regression/` 各 115 项通过 | 本机 GPU；正式 shader / compute / 计数、替身输入，含容量 / 重建 / 多层 / 相机隔离；主机内存压力 / 丢样本，仅作趋势，未验游戏绘制、观感或 FPS |
| 天气地区 / 行星门控 / 预设 | 2026-10-25：`.utmp/regional-weather-validation/` 91751 项数值 / XML / 三语键检查；`regional-weather-integration/` 68 项 Unity / GPU、9 项贴图导入 / 资源包检查通过；C# 0 错误。独立旧范围：2026-10-15 门控 49 项、预设 36 项，目录同前缀 `planet-gate` / `weather-preset` | 含双来源、原图 524288 像素打包前后逐点一致、主 / PIP 数量与池复用、主雨声、水珠 / 水花残留、候选落雷、坐标 / 自转 / 东向与旧 XML。SDK、云照明和音频为替身，XML 写工作区；旧门控未复跑，未验游戏部署 / 画质 / FPS |
| 雾配置保存 / 2026-10-14 | `.utmp/fog-persistence-validation/result.txt`：99 项通过，Unity 2022.3.62f3；加入生成工程遗漏的新源码后编译 0 错误 | 实际面板回调、全部雾字段 XML 往返、锁定 / 只读 / 无效路径失败与旧文件保护；游戏 / UI SDK 和镜头效果消费者为替身，XML 写工作区，未验游戏部署 |
| 镜头水珠 / 2026-10-14 | `.utmp/rain-lens-visibility/result-{Gamma,Linear}.txt`：各 97 项通过；C# 编译 0 错误。1280 / 1920 灰背景、旧尺寸 / 折射参数下 shader 2 平均对比差为首版 3.57~3.59 倍 | 本机 GPU，正式模拟 / 渲染与天气，含配置 / 生命周期 / 光学回归；SDK 替身，非游戏感知倍数或性能验收 |
| 雾夜间光照 / 2026-10-09 | `Build/FogNight/fog-gpu-summary.txt`：161 项通过 | 独立 BIRP GPU / 配置检查，正式编辑器探针已删除 |
| 无标签雨碰撞 / 2026-10-08 | 历史 135 项 GPU 检查通过；用户确认当时水花可见性修复 | 不覆盖后续水面 shader、光照或完整复杂材质矩阵 |
| 雨方向 / 重生 | 2026-10-11 游戏 34 组构轴：符号反射误差 0，预期角差最大 0.17°，用户确认改善；2026-10-04 重生 hash 落点唯一率 0.932、最大重复 5 | 方向仅表面锁定、variation=0、roll=0，不覆盖移动帧、重定位和 PIP；hash 为固定规模 / 统计格，不保证所有密度 |

游戏太阳拆分（2026-10-23，NewConfig2、2560×1600、427×267 射线、反射开）：shader 19 `.utmp/cloud-trace-game-validation/` 的 9 档 / 10496 帧完成并恢复设置，完整 / NoSolar / 无步进为 28.035 / 24.686 / 23.771ms；太阳项逐轮减少 2.629 / 3.348 / 6.465ms，余项 0.808~0.917ms。GPU / Main / Render 均 n/a，整帧分档有波动，非纯 GPU 耗时。

## 按改动选择验证

1. 文档修改运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs` 和 `git diff --check`，再查全仓入链、章节引用和已删接口残留。
2. 只改注释用 `tools/strip-code-comments.ps1 -Old <快照> -New <改后>` 证明非注释内容一致。C# 行为修改用本机 Unity / 游戏引用编译；已生成工程可执行 `dotnet build Volken.csproj --no-restore --verbosity quiet`。
3. shader / compute 修改要确认全部 kernel / pass 编译，做真实图形设备验证。雨 compute 一个 kernel 编译失败可能影响整个资源；不能只以 C# 编译判断雨能渲染。
4. 游戏验收按 [待办矩阵](backlog.md#游戏验收) 做镜头、场景、画面和听感检查。历史独立 GPU 检查不替代打包安装后的验证。

性能固定构建、行星、镜头、分辨率、画质、预设、PIP 与帧率限制，一次改一项；预热后每档 ≥30秒×3，记录平均 / P50 / P95 / P99、Main / Render / GPU、GC 与 DrawCall；雷击另测生长 / 闪光 / 消退尖峰。

Profiler 的 `CloudRenderInfo` 仅为配置摘要，不能当作耗时。

第三方 `MultiPlayerUI.OnSceneLoaded` NRE 曾中断云 / 分布事件链；先查首个异常，当前冲突需复核。安装勿同时保留 `Volken.sr2-mod` / `Volken-R.sr2-mod`。

## 文档维护规则

规则见 [AGENTS.md](../AGENTS.md)，[校验脚本](../tools/check-docs.ps1) 不替代源码 / 部署 / 事实核对。
