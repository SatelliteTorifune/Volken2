# 雨滴与水花自发光修复

> 状态:✅ 雨滴自发光修复保留;水花入射光乘法已撤回,原显示路径恢复,C# 编译及 Gamma / Linear 各 115 项专项检查通过,游戏画面待验收。
> 日期:2026-10-14
> 触发:用户反馈雨滴发光,`emissiveness` 调至 0 仍然亮。
> 关联:[雨实施归档](archive/sp2-rain-particledomain-port-2026-09-28.md) §10、[水面溅射](rain-water-splash-fix-2026-10-09.md)、[雾光照](fog-implementation-plan-2026-10-09.md) §13。

## 1. 根因与语义

雨滴旧片元输出是纹理乘固定浅蓝色,随后乘 `1 + _Emission`。它没有环境光或太阳光输入,因此 `_Emission=0` 仅取消额外增益,基础 RGB 仍是固定亮色。地面水花与水面涟漪 / 飞溅也直接输出固定颜色,只叠加雾,同样会在暗处显亮。

【决策:2026-10-14】雨滴先接受游戏环境光和太阳光,再叠加用户指定的自发光;0 表示没有独立自发光。保留 `rain.brightness` XML 字段、默认 0、0~3 面板范围,三语标签改为雨滴自发光。既有正值现在表示独立发光强度,不再表示对固定未照明颜色的乘法增益。

## 2. 实现

| 位置 | 当前行为 |
|---|---|
| `RainParticles.EvaluateIncidentLight` | 读取游戏 `RenderSettings.ambientLight` 和当前 `GameView.SunLight`;复用 `FogLighting.WorkingColor` 的颜色空间转换,环境项不设下限,不随太阳高度再次压暗 |
| 每相机 `Tick` | 用已有相机径向上向量计算太阳高度,太阳在地平线以下为 0,在高度余弦 0~0.1 内平滑进入;太阳项乘真实 intensity、启用状态和颜色 alpha 中的行星遮挡值。独立预览用 `RenderSettings.sun`,不访问游戏单例 |
| `RainParticles.shader` | `RGB = 纹理 × 雨色 × (入射光 + max(0, _Emission))`,然后走原有雾;雨 shader 与 C# 部署哨兵均更新至 10 |
| `RainCollision.Draw / DrawSplashes` | 首轮传递入射光给两个水花材质;已按 §5 撤回,恢复原参数与绑定 |
| `RainSplashes / RainWaterSplashes` | 首轮给水花颜色乘入射光;已按 §5 撤回,恢复原有颜色 / 雾路径 |

雨与水花的 alpha、深度淡出、域边界、距离衰减、海拔 / 水下门控、碰撞预算和雨声逻辑保持原有路径。没有增加捕获相机、贴图、RT、GPU 回读或逐帧日志。太阳为局部径向高度门控的近似光照,未新增地形 / 云影遮挡或局部灯光采样。

无入射光且自发光为 0 时,雨滴 RGB 为 0,仍保留透明遮挡。夜间环境光与已照亮的雾仍可以贡献雨滴颜色。水花维持修复前的显示方式,见 §5;不再受本次新增的雨滴光照输入影响。

## 3. 首轮验证(历史结果,未覆盖本次水花回归)

- `dotnet build Volken.csproj --no-restore --verbosity quiet` 通过:0 错误,3 条既有警告(程序集版本冲突及星环不可达代码)。
- 独立 BIRP 工程位于被忽略的 `.utmp/rain-lighting-validation/`;复制当前三个雨 / 水花 shader、碰撞 compute / 驱动与雾 include。光照纯函数从当前 `RainParticles.cs` 提取,独立驱动使用最小资产加载替身,实际完整 C# 由原工程编译验证。
- Unity 2022.3.62f3、D3D11、RTX 5060 Laptop GPU:Gamma / Linear 各 **99 通过 / 0 失败**,每轮包含 46 项光照 / shader / GPU 检查与 53 项既有水面 / 碰撞回归;未使用 `-nographics`。
- 实际旧 shader 在自发光为 0、未输入光照时仍输出亮雨;当前 shader 在相同黑环境下 RGB=0,alpha 与旧版一致。独立自发光在黑环境中仍可见,单位入射光保持原白天亮度,暗环境按输入比例变暗;环境色 Gamma / Linear 转换、太阳地平线 / 开关 / 遮挡、负自发光和完全透明均通过。
- 地面与水面水花在黑环境中 RGB=0、alpha 不变;暗环境按比例变暗。既有 GPU 碰撞、涟漪扩张、低角度 / 俯视、写深度水面、前景遮挡与正式 `RainCollision.Draw` 间接绘制通过。旧水面测试的固定 0.1 亮度门槛仅适合 Gamma;本轮按实际色彩空间转换测试平面颜色后核对,没有因此改正式效果。
- 输出与日志在 `.utmp/rain-lighting-validation/`:Gamma 的 `lighting-result-gamma.txt` / `water-result-gamma.txt`、Linear 的 `lighting-result.txt` / `project/Library/VolkenRainWaterProbe/summary.txt`,以及 `unity-gamma.log` / `unity-linear-final.log`。测试工具只在忽略目录,未恢复正式 `VolkenTests/Editor` 菜单。

## 4. 游戏验收

重新构建并部署包含 shader 的资源包,仅更新程序集不能替换旧雨 shader。按本项目既有流程由用户打包安装;开发接口 `RainParticles.DiagStatus()` 应报告 shader 版本 10。

自发光设为 0,检查白天、夜间和日出 / 日落:雨滴随环境变化,暗处没有恒定浅蓝亮色,两种水花保持原有溅落显示。随后在暗处提高自发光,确认只增加雨滴发光;恢复 0 后消失。检查主视图 / PIP、雾开关、透明度 1、暂停、重定位和切场景。独立 GPU 通过不能替代这些游戏验收。

## 5. 水花消失回归修复(2026-10-14)

用户复测报告本次改动后碰撞溅落效果消失。首轮把只针对雨滴的光照修改扩大到两个水花 shader,原水花色乘新增 `_RainIncidentLight`:为 0 时完全黑,低环境光时明显压低亮度。碰撞 compute、发射预算和槽寿命均未改动;首轮的单位入射光测试只能证明白天基准,没有按原水花显示强度验收夜间对比度。

本次读取的游戏日志最后写入时间为 2026-10-14 04:24:31,碰撞 compute / 深度 / 两个水花 shader 均报告已加载;夜间 Linear 环境 RGB 为 0.10462,太阳关闭。日志未含本次活槽统计,因此不能由资源就绪推断水花实际生成状态,也不能把此次反馈直接归结为碰撞计算失效。

【决策:2026-10-14】撤回两种水花的入射光乘法及 `RainCollision.Draw` 新增光照参数,恢复修改前的水花 RGB / 雾路径和材质绑定;保留雨滴的入射光 / 独立自发光修复、shader 哨兵 10 和三语标签。地面环、水面涟漪 / 竖直飞溅、碰撞和深度遮挡恢复原契约,不添加水花光照下限或新配置。

源码核对:`RainCollision.cs`、`RainSplashes.shader`、`RainWaterSplashes.shader` 已恢复到本次雨光照改动前的版本。独立回归保存首轮有问题的 shader 作为对照,验证零光输入复现消失、游戏夜光输入复现压暗、恢复后水花显示与原版逐像素一致,并保留雨滴不自发光的检查。

专项结果:Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop GPU,Gamma / Linear 各 **115 通过 / 0 失败**(62 项雨光照 / 水花回归对照检查 + 53 项水面 / 碰撞回归)。旧修改在零光输入下 RGB 为 0;输入日志中的 0.10462 时亮度约为原水花的 10.46%,恢复后亮度与原水花一致,alpha / 轮廓不变。在两种非黑夜间背景上恢复可见对比度,完全透明及地面 / 水面互斥仍生效,正式 `RainCollision.Draw` 绘制和前景深度遮挡通过。

`dotnet build Volken.csproj --no-restore --verbosity quiet` 再次通过:0 错误,3 条既有警告。验证工程在忽略目录 `.utmp/rain-splash-regression/`,结果为 `result-gamma.txt` / `water-result-gamma.txt`、`splash-regression-result.txt` / `project/Library/VolkenRainWaterProbe/summary.txt`,日志 `unity-gamma.log` / `unity-linear.log`;未恢复正式编辑器测试入口。以上为独立验证,用户打包部署后的游戏画面仍待复测。
