# 开发、验证与文档维护

> 核对日期：2026-10-14。工具入口以当前文件和注册代码为准；本页历史结果不表示本次重新运行或已部署到游戏。

## 开发与资源

- 正式实现位于 `Assets/Scripts/Volken/`；开发工具位于 `Assets/Scripts/VolkenTests/`，单向依赖正式代码。移除开发工具不能影响模组正常编译和运行。
- 新功能默认关闭或恒等，保留回退入口。新配置同步默认值、复制、限值、XML 往返 / 迁移、实际消费者和三语键，文案前缀为 `Volken.UI.*`。
- shader / compute 按子系统放 `Shader/`；移动资产带 `.meta`，同步加载路径与 `Assets/ModData.asset` 的 `_otherAssets` GUID。资源登记或本机加载成功不等于已经入游戏包。
- 正式环境由 `Mod.LoadVolkenAsset` 使用模组 `ResourceLoader.LoadAsset<T>`；编辑器优先 AssetDatabase。新增 shader / compute 需要更新资源包，只有程序集更新不能替换旧 shader。
- 每帧渲染避免 LINQ、临时集合及不必要 GPU 同步回读。相机资源、材质和历史归属见 [架构](architecture.md)、[云](clouds.md) 与 [天气](weather.md)。

## 当前工具与诊断

控制台仅注册 `frs`、`brs`、`VolkenForceRefresh`，见 `Mod.RegisterCommands`。旧 `volkenAssets`、`volkenBolt`、`volkenRainAxis*` 等命令已删除；`VolkenForceRefresh` 是行星解析开发入口，不保证绕过同行星配置早退。

[VolkenTests 工具说明](../Assets/Scripts/VolkenTests/README.md) 保留 Profiler 自动启动入口；NoiseVisualizer / RaymarchDebug 是缺少配套 shader 的遗留组件。`RainPreview`、雨方向临时诊断、`VolkenTests/Editor` 探针和对应菜单已删除，不能照历史测试记录执行。

| 入口 | 使用范围 |
|---|---|
| `Mod.Log` | 项目唯一的日志接口；资源缺失、配置保存失败、初始化故障和逐帧异常均走此接口，全部受 `DevMode` 控制，默认静默；Unity 底层日志调用仅位于该方法内部 |
| `orbitDebugMode` | XML 开发分屏；不输出轨道参数、投影或覆盖率日志 |
| `fog.debugMode` | XML 开发视图，排查后恢复 debug=0 |

游戏效果面板没有手动诊断按钮。项目不再提供独立诊断 / 节流日志接口、时间戳字典、雨 / 雨声 / 雨碰撞 / 雾状态转储、诊断 GPU 回读或雾周期帧计时。旧 XML 的 `fog.diagnostics` 节点由反序列化忽略；雨 compute 的计数缓冲仍按原 shader 契约绑定，不再 CPU 回读。

当前源码标识集中列在这里：云 shader 14 / lightning 2，噪声 `VolkenWorleyFBM` 且两张 128³ 纹理各 8 级 mip，雨 shader 10，镜头水珠 shader 2，雾 shader 3。旧云 `build=17` 和雾 `code=3` 仅是已删除诊断的标签，不再用于识别运行包；部署版本以模组元数据和实际包内程序集 / 资源核对。

游戏日志路径为 `<USERPROFILE>/AppData/LocalLow/Jundroo/SimpleRockets 2/Player.log`；shader 编译还查 Unity `Editor.log`。真实路径按被忽略的 [LOCAL_PATHS.md](LOCAL_PATHS.md) 解析。先排除暂停，诊断空间要与症状一致；同时记录配置、运行态、触发计数和资源标识。连续三轮无可信改善时换诊断方向。

## 已有验证证据

下表只保留各范围最新证据；历史结果不代表本次重新运行。独立工程含游戏 API / 资源替身，已删除的工具和旧测试快照不代表当前完整工程的测试覆盖。检查数不可跨版本或颜色空间相加为同一轮结果。

| 范围 / 日期 | 保留的证据 | 实际限制 |
|---|---|---|
| 云阳光遮蔽 / 局部投影 / 2026-10-15 | `.utmp/cloud-native-shadow-validation/result-formal-fullsync-{Gamma,Linear}.txt` 各 256 机体 / 薄翼配置通过；`result-formal-fullsync-range-sweep-Linear.txt` 800 距离扫描无丢影；`.utmp/cloud-native-shadow-design-probe/result-after-formal-fullsync-{Gamma,Linear}-all.txt` 各 621 世界点近远差为 0、64 契约通过，`result-no-expansion-{Gamma,Linear}.txt` 各 128 状态检查；`.utmp/cloud-occlusion-validation/result-native-shadow-regression-{Gamma,Linear}.txt` 各 248 项；C# 编译 0 错误 | Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop；CloseFit / StableFit、0/1/2/4 级、原生 50~2000m、云影 20/100km，采地面排除机体自身；4 级原生影像素最低保留 99.46%，2 级最低 77.46%，单级恒等。扫描含 EVA near=0.06，会有额外粗影；覆盖投影 / 剔除同步、原绘制矩阵 / 标准深度、嵌套恢复。SDK / 行星为替身，特殊手动剔除只验字段恢复；build 16 游戏截图已证实原生影消失，build 17 待游戏画质及额外几何提交耗时复测 |
| 雨滴光照 / 水花显示回退 / 2026-10-14 | `.utmp/rain-splash-regression/`：Gamma / Linear 各 62 项专项 + 53 项水面检查，即各 115 项通过 | 恢复水花原显示的对照检查；不能替代游戏水面观感 |
| 天气行星门控 / 预设同步 / 2026-10-15 | `.utmp/planet-gate-validation/result.txt`：49 项通过；当前源码复跑 `.utmp/weather-preset-validation/result.txt`：36 项通过；完整 C# 工程编译 0 错误 | Unity 2022.3.62f3 / D3D11；实际环境解析、SOI 订阅、天气 / 雨 / 雨声 / 雷电 / 水珠生命周期与预设同步，游戏 SDK、云渲染和音频素材为替身；XML 写工作区，未验游戏部署 |
| 雾配置保存 / 2026-10-14 | `.utmp/fog-persistence-validation/result.txt`：99 项通过，Unity 2022.3.62f3；加入生成工程遗漏的新源码后编译 0 错误 | 实际面板回调、全部雾字段 XML 往返、锁定 / 只读 / 无效路径失败与旧文件保护；游戏 / UI SDK 和镜头效果消费者为替身，XML 写工作区，未验游戏部署 |
| 删除兜底声速 / 2026-10-14 | `.utmp/lightning-validation/result-thunder-removal.txt`：63 项通过 | 核心方法和配置验证，音频排程被截获，未做游戏听感测试 |
| 镜头水珠 / 2026-10-14 | `.utmp/rain-lens-visibility/result-{Gamma,Linear}.txt`：各 97 项通过；C# 编译 0 错误。1280 / 1920 灰背景、旧尺寸 / 折射参数下 shader 2 平均对比差为首版 3.57~3.59 倍 | Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop，正式模拟 / 渲染 / shader / 天气驱动；覆盖默认升级 / 自定义保留、低对比辨识及原生命周期 / 光学回归，游戏 API 替身；不代表游戏感知改善倍数或性能验收 |
| 雾夜间光照 / 2026-10-09 | `Build/FogNight/fog-gpu-summary.txt`：161 项通过 | 独立 BIRP GPU / 配置检查，正式编辑器探针已删除 |
| 无标签雨碰撞 / 2026-10-08 | 历史 135 项 GPU 检查通过；用户确认当时水花可见性修复 | 不覆盖后续水面 shader、光照或完整复杂材质矩阵 |
| 雨方向 / 2026-10-11 | 游戏 34 组构轴：符号反射误差 0，预期角差最大 0.17°；用户确认改善 | 表面锁定、variation=0、roll=0；不覆盖移动帧、重定位和 PIP |
| 重生 hash / 2026-10-04 | 历史游戏落点唯一率 0.932、最大重复 5 | 固定测试规模 / 统计格；不能当所有密度下的保证 |

上述修复当时均有相应编译记录；不要把历史“0 错误”写成本次编译结果，也不要承诺既有警告数永远固定。专用方向日志已清理，旧文档原文可从 Git 历史恢复。

## 按改动选择验证

1. 文档修改运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs` 和 `git diff --check`，再查全仓入链、章节引用和已删接口残留。
2. 只改注释用 `tools/strip-code-comments.ps1 -Old <快照> -New <改后>` 证明非注释内容一致。C# 行为修改用本机 Unity / 游戏引用编译；已生成工程可执行 `dotnet build Volken.csproj --no-restore --verbosity quiet`。
3. shader / compute 修改要确认全部 kernel / pass 编译，做真实图形设备验证。雨 compute 一个 kernel 编译失败可能影响整个资源；不能只以 C# 编译判断雨能渲染。
4. 游戏验收按 [待办矩阵](backlog.md#游戏验收) 做镜头、场景、画面和听感检查。历史独立 GPU 检查不替代打包安装后的验证。

测性能要固定构建、行星、镜头路径、分辨率、画质、预设、PIP 数与帧率限制，一次只改一项；预热后每档至少采样 30 秒、重复 3 轮，记录平均 / P50 / P95 / P99、Main / Render / GPU、GC 和实际 DrawCall。雷击另外记录生长 / 闪光 / 消退尖峰。

Profiler 的 `CloudRenderInfo` 只是配置摘要。未拆模块的 FPS 差不能全部归因于一个效果；提交 CPU 时间与模块 GPU 时间要分别测量。

“云不可见且自带分布不可用”的历史故障曾由第三方 `MultiPlayerUI.OnSceneLoaded` NRE 中断事件链引起。排查先看日志中的第一个异常；安装目录不要同时保留 `Volken.sr2-mod` 和 `Volken-R.sr2-mod`。当前是否有第三方冲突需重新核实。

## 文档维护规则

唯一规则源为根目录 [AGENTS.md](../AGENTS.md)，包括内容归属、何时记录、替换与删除、待办生命周期和篇幅预算。开工时按规则定位现有内容，收尾替换原记录。

[check-docs.ps1](../tools/check-docs.ps1) 随仓库保存，检查编码、链接、隐私、单文件 / 总量预算、表格、重复、临时文件、编号、Git 异常、固定文档结构和流水记录。自动生成 `.csproj` 与其他本机工具仍可能被忽略；校验脚本不能代替源码 / 状态与事实归属的人工核对。
