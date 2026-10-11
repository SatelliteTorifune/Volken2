# 雨滴方向诊断日志

> 状态:✅ 已修复 / 本次游戏场景验证通过 / 已归档 / 排查日志已清理。2026-10-11 用户反馈“感觉修好了”,`build=2` / `shader=9` 日志支持该结论:34 组构轴的 Y 反射误差为零,预期方向最大角差 0.17°。随后按用户要求移除本次诊断与临时日志;§2~§7 保留历史记录,不代表当前可调用入口。修复与验收见 §6、§7,收尾见 §8;移动参考帧及非零变体边界尚未修复 / 验收。

## 1. 参考依据与待核实项

- `<JNO_CODE>/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs` 的 `Update`: `SurfaceVelocityFrame = craft.FrameVelocity + referenceFrame.FrameSurfaceVelocity`。`CraftScript.SurfaceVelocity` 返回该帧空间地表速度。
- `<JNO_CODE>/SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs` 的 `UpdateSurfaceVelocity`:参考帧相对地表速度转换到帧坐标,表面锁定时该速度为零。地表静止雨在移动帧中应包含 `-FrameSurfaceVelocity`。
- `<JNO_CODE>/SimpleRockets2/Assets/Scripts/Flight/UI/NavSphereScript.cs` 的地表模式使用 `FlightData.SurfaceVelocity`。`<JNO_D2>/Assets/Resources/flight/common/navsphere/NavSphere.prefab` 绑定速度指示器,对应 `VelocityIndicatorMaterial.mat` 的颜色为绿色。截图中的标记是否采用该模式,仍需结合当帧状态判断。
- 修复前 `RainParticles` 对构轴速度的世界 Y 分量取绝对值;本轮去除该反射,保留有符号的 `FrameVelocity`。粒子积分仍仅包含径向下落,移动参考帧的补偿缺口尚未修复。
- 当前雨 shader 在构轴后按帧空间 X/Y/Z 缩放长宽;碰撞裁切将穿入表面的顶点投回表面。两者均可能改变实际几何轴线。

## 2. 启用与输出

本节为清理前的诊断操作记录。§8 收尾后,正式代码已移除 `RainDirectionDiagnostics`,开发日志开关不再触发本次周期采样。

1. 编译并使用本轮代码,开启模组设置中的开发日志,启用雨。
2. 在游戏日志中搜索 `RainDirection`。首次主相机绘制输出 `installed build=2` / `axisPolicy=signed-frame-velocity`;即使开发日志关闭,也输出这条部署标记。
3. 开发日志开启时,主相机每 5 秒输出一组状态并启动一次异步采样。从前最多 1024 个活跃粒子中选取最多 8 个位于屏幕内且未被碰撞剔除的粒子,约 0.1 秒后再次读取位置并只比较这些 ID。实际间隔以 `elapsed` 为准,包含异步回读与帧率影响。暂停或没有选到可见粒子时不启动运动方向比较。
4. 关闭开发日志、关闭雨、重建 buffer 或销毁雨实例时取消在途诊断。异步回读超过 2 秒报超时并取消;不支持异步回读时保留 CPU 状态日志。

游戏日志路径: `<USERPROFILE>/AppData/LocalLow/Jundroo/SimpleRockets 2/Player.log`。每条采样结果包含 `seq`、源帧号,可按序号与截图时刻对应;时间 `t` 是进程启动后的真实秒数。

## 3. 日志解释

| 日志 / 字段 | 含义与判断 |
|---|---|
| `installed` / `state` | 核对代码 `build=2` 与 `shader=9`,暂停、相机、表面锁定、构轴模式、碰撞、随机变体、透明度与重定位事件。没有新部署标记时先核对实际使用的代码 |
| `velocity` | 实际构轴速度来源、处理后的 `down`、原始 `gravityRaw`、craft 帧速度、相机速度、地表 API 速度、参考帧相对地表速度 |
| `apiResidual` | `SurfaceVelocity - FrameVelocity - FrameSurfaceVelocity` 的模长。应接近零;偏大时先核查 API 更新时序或采样数据,不要直接把其余角差当作根因 |
| `axisSourceResidual` | 传入构轴速度与原始 craft 帧速度的差。配合 `source` 确认是否回退到了相机速度 |
| `rainFrameCurrent/Expected` | 当前径向积分速度与补偿参考帧后的雨速;统一使用帧坐标 |
| `absErrorDeg` / `legacyAbsErrorDeg` | 当前构轴与保留符号的向量夹角 / 旧绝对值算法造成的夹角。`build=2` 且 `stream=True` 时前者应为零;后者保留用于对照修复前问题 |
| `frameErrorDeg` | 去掉 `Abs(Y)` 后的向量与补偿参考帧后的预期向量夹角 |
| `totalErrorDeg` | 当前材质矩阵轴与按 craft 地表 API 速度计算的预期相对雨速夹角;无有效 craft 时记为 `-1`,只在 `expectedAvailable=True` 时分析 |
| `matrixRawErrorDeg` / `rawFallback` | 材质中的轴是否与 C# 当前构轴向量一致,是否因近零速度回退到径向下 |
| `projection@30m` | 在相机前方 30m 投影的当前轴 / 预期轴 / craft 地表速度方向。向量为屏幕像素,正 X 向右、正 Y 向上;截图文件像素 Y 向下,比较时需翻转 Y |
| `particle-reconstructed` | 真实 GPU buffer 中的位置、随机参数、碰撞面;按 shader 9 在 CPU 重建的中心线。它不是 GPU 顶点回读或最终光栅像素 |
| `scaleErrorDeg` | 当前长宽随机缩放及滚转使中心线偏离矩阵轴的角差,含最小像素宽度分支 |
| `clipErrorDeg` / `clipped` | 贴地或海平面裁切造成的中心线角差;整粒塌缩或退化轴的角差为 `-1` |
| `inView` / `eyeDepth` | 位置中心是否在前方屏幕内。中心可见不等于整条雨丝可见,也未计入场景遮挡和 alpha 淡出 |
| `motion` / `measuredFrameVel` | 同一粒子两次真实位置读回的差除以积分时间,不是通过矩阵推算的速度 |
| `selection` / `motion-skipped` | 本组候选数量、选中的屏幕内粒子数量;暂停或没有可见样本时明确跳过运动比较 |
| `integratorResidual` | 真实位移与当前径向积分预测的距离差,单位米。近零说明当前位置积分符合当前 compute 模型,不代表它符合地表相对运动 |
| `discontinuousOrUnexpected` | 重定位、参考帧变化、暂停、出域重生或与当前积分模型不一致均可能触发。此时禁止将测得速度直接当作稳定雨速 |
| `screenDeltaPx` / `angleToAxisScreen` | 两次相机快照投影的实际粒子屏幕位移及其与当前矩阵轴屏幕投影的夹角;包含绕飞 / 变焦影响。仅两次都在屏幕内、积分连续且位移 / 轴未退化时 `directionValid=True`,否则角差为 `-1` |

角度为 `-1` 表示向量退化或预期不可用,不表示方向正确。`frameChanged` 记录应用重定位计数 / 参考帧身份 / 表面锁定变化或超过 0.001 的参考帧旋转变化。粒子经过出域重生时,两次位置差不再代表连续运动。候选窗口也不保证一定覆盖屏幕中的雨滴,以 `selection` 为准。

## 4. 复现顺序

- 保持原有异常航向,固定相机约 15 秒,记录截图与发生时刻。
- craft 保持相同航向和速度,只绕飞相机约 10 秒,检查帧空间向量是否稳定、屏幕运动是否随相机改变。
- 在异常附近转向或改变升降速度,尤其关注构轴速度世界 Y 分量变号。
- 保持同一状态分别观察碰撞关闭 / 开启,每个状态约 10 秒,比较 `scaleErrorDeg` 与 `clipErrorDeg`。

这些是复现建议,不需要修改配置文件或输入旧调试命令。静止、高速平飞、俯冲、参考帧重定位仍需分别确认,单次截图不能证明所有边界条件均正常。

## 5. 开销与验证

- 仅主相机且开发日志开启时执行周期采样。碰撞开启时一组回读最多 64 KiB,关闭时 48 KiB;暂停 / 无可见样本时没有第二次位置回读。平均每 5 秒一组,仍只输出最多 8 个粒子;不调用同步 `GetData` / `WaitForCompletion`,无新增常驻 GPU buffer。
- 两次采样在各自源帧记录材质和相机快照,回调使用快照而非回调时的相机;buffer 重建 / 关闭 / 销毁通过 generation 失效旧回调。
- 使用 Unity 2022.3.62f3 随附 Roslyn 和项目现有引用编译当前 C# 源码通过;原有星环不可达代码警告仍存在。
- `build=1` 在隔离的 `Library/RainDirectionProbe` 工程验证真实 `RainParticles.compute` / `RainParticles.shader` 与诊断代码,12 项检查通过;游戏 API 使用受控桩。
- `build=2` C# 编译通过,独立回归 18 项检查通过:在原 12 项基础上验证有符号构轴 / 材质轴一致、保留旧算法对照、从 1024 候选中跳过 992 个离屏粒子选中可见 ID、两次可见的运动有效标记与暂停跳过。结果见 `Library/RainDirectionProbe/summary.txt` / `probe-build2.log`;游戏 API 为受控桩,不代表游戏修复已验收。工具与输出位于被忽略的 `Library` 中,未恢复已删除的项目编辑器测试入口。未打包,待游戏修复验收。

## 6. 游戏日志归因与本轮修复

已读取用户当前 `Player.log`,修复前备份在 `Library/RainDirectionCheck/player-before-fix.log`。`build=1` / `shader=9` 正常加载,共 25 组诊断。

与截图三种箭头一致的 `seq=16`,源帧 `7774`,真实进程时间 `272.603s`:

| 量 | 日志值 | 解释 |
|---|---|---|
| 径向下 | `(0.928, -0.002, 0.372)` | 世界 Y 几乎属于当地水平面 |
| craft 帧速度 | `(14.780, -92.904, -37.214)` m/s | 负 Y 是主要水平运动分量,不能取绝对值 |
| 当前雨轴 | `(-0.858, -92.932, 42.799)` | 对 Y 速度取绝对值后得到的错误向量 |
| 去掉绝对值后的雨轴 | `(-0.858, 92.876, 42.799)` | Y 分量应为正,与预期地表相对雨速接近 |
| 预期地表相对雨速 | `(-0.836, 92.758, 42.743)` | API 更新差约 `0.1327m/s`,不足以解释大角差 |
| 构轴误差 | `absErrorDeg=130.52`, `totalErrorDeg=130.52` | 主要误差由 Y 反射引起 |
| 当前 / 预期 / craft 屏幕投影 | `(116.15,9.78)` / `(-11.29,-70.25)` / `(11.42,49.82)` | 分别为向右、向下略左、向上略右,与红 / 蓝 / 绿箭头一致;日志屏幕 Y 向上 |
| 参考帧 / 矩阵传递 | `frameSurface=(0,0,0)`, `frameErrorDeg=0`, `matrixRawErrorDeg=0` | 本次不是参考帧补偿或矩阵传递产生的误差 |
| 变体 | `variation=0`, `roll=0`, `minWidthPx=0` | 本次随机缩放问题未参与 |

本轮将 `streakDir` 改为 `down * FallSpeed - axisVel`,保留世界 / 帧空间构轴,允许速度分量保持真实符号。位置积分、shader 几何及碰撞算法保持当前计算。移动参考帧补偿与开启随机变体后的缩放问题仍为独立边界缺口,本次日志没有验证它们正确。

证据限制与诊断改进:

- `seq=9~22` 为暂停状态,`dt=0`;这些组可以证明构轴与屏幕投影错误,不能用于证明当时真实粒子运动方向。未暂停的连续 GPU 样本积分残差接近零,说明粒子位置符合现有径向下落模型。
- 旧诊断固定读取前 8 个粒子,本次 25 组里只有 1 个样本的中心在屏幕内。其余离屏粒子的屏幕位移 / 角差不能用来证明截图中红箭头的实际运动。部分样本经过出域重生,也不能作为连续速度。
- `build=2` 扩大候选窗口并仅输出选中的可见粒子;两次均可见且积分连续才标记 `directionValid=True`,暂停直接跳过运动方向比较。下一轮用于分离几何长轴与可见粒子的实际运动。

## 7. 修复后的游戏验证

2026-10-11 用户反馈“我感觉修好了”。已读取本次 `Player.log`,并保存在 `Library/RainDirectionCheck/player-after-fix.log`;该本地备份被 Git 忽略。日志确认游戏加载 `build=2` / `shader=9`,不是再次运行旧构轴算法。

| 本次量 | 核对结果 |
|---|---|
| 状态 / 构轴采样 | 34 组,其中 8 组暂停 |
| `absErrorDeg` | 34 组均为 0.00° |
| `totalErrorDeg` | 最大 0.17°,相对地表 API 的预期向量已基本一致 |
| `matrixRawErrorDeg` | 最大 0.02°,存在浮点角度计算的微小差值 |
| `frameErrorDeg` | 全为 0.00°,本次均为表面锁定状态 |
| `legacyAbsErrorDeg` | 最大 135.64°,说明本次再次覆盖了旧算法会出错的速度符号条件 |
| 有效运动样本 | 77 个,两次均在屏幕内且位置积分连续;积分残差最大 0.0002m |
| 异步诊断错误 / 超时 | 0 |

关键复现组 `seq=16`,源帧 `9963`,进程真实时间 `276.786s`:

- craft 速度仍包含较大的负世界 Y 分量: `(29.176,-80.591,-23.509)` m/s。
- 修复后的雨轴为 `(-15.256,80.571,29.098)`,地表 API 预期为 `(-15.430,80.486,29.028)`,总角差 `0.13°`;旧绝对值算法在同一状态下会产生 `135.64°` 误差。
- 本组 6 个有效可见粒子的运动方向与当前位置矩阵轴屏幕投影夹角为 `0.14~0.91°`,积分残差约 `0.0001m`。`seq=33` 的 7 个有效样本夹角为 `0.07~0.63°`,`seq=34` 的 2 个有效样本为 `0.00°`。

结合用户视觉反馈,此次“世界 Y 速度取绝对值导致横向雨流”的修复在本次游戏场景下验证通过。运动角差包含相机运动和采样期间的状态变化,其他组存在较大屏幕运动角差,不能将全部 77 个样本都当作稳定视角的一致性通过证据。

本次所有状态均为 `surfaceLocked=True`,且 `variation=0` / `roll=0`;没有重定位事件。验收不覆盖移动参考帧补偿、非零随机长宽变体、重定位或多相机边界。保留这两处已知代码缺口及相关回归入口,不因本轮方向修复通过而标记全部雨系统完成。诊断仍由开发日志控制,本轮未继续修改运行时代码。

## 8. 日志清理与收尾

2026-10-11 用户要求清理相关日志、更新文档并归档:

- 删除本次新增的 `RainDirectionDiagnostics.cs` 及 `.meta`,移除 `RainParticles` 中的实例 / Tick / 重定位记录 / 取消回调,移除 `RainCollision.DiagnosticSurfaces` 诊断访问器。
- 清理后不再输出 `RainDirection installed/state/velocity/axis/selection/particle-reconstructed/motion` 等排查日志,也不再发起本次周期 GPU 异步回读。
- 保留已验证的有符号构轴修复 `down * FallSpeed - axisVel`;雨滴运动、碰撞与水花维持现有计算。
- 删除本次专用 `Library/RainDirectionCheck` / `Library/RainDirectionProbe` 临时目录,包括前后游戏日志备份、验证日志、测试工程与编译产物。§5~§7 引用这些路径仅记录当时证据来源;关键数值已保存在本归档中。
- 当前完整游戏 `Player.log` 与其他主题的诊断由其各自流程维护。本轮删除范围为本次雨方向排查新增的代码与项目内临时文件。
- 清理后 C# 编译通过,仅保留既有星环不可达代码警告;`Assets` 中本次诊断类型 / 成员 / 日志前缀引用为零,专用脚本 / `.meta` / 两个临时目录均已确认删除,方向修复仍保留。文档检查与 `git diff --check` 通过;编译验证生成的临时文件也已删除。
