# 雨滴水面涟漪与飞溅修复(2026-10-09)

> 状态:🚧 原工程代码已接入,53 项独立 GPU 回归通过;游戏水面观感与部署待验收。
> 触发:用户反馈雨滴溅到水面没有可见效果,要求水面专用溅射 shader。
> 关联:[雨实施归档](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.8、[帧率优化清单](proposals/frame-rate-optimization-audit-2026-10-08.md) §3。本轮沿用原工程已有雾修改,按现有约定不负责打包安装游戏模组。

## 1. 现状与证据

- 既有 compute 已区分几何与解析海平面,`_SplashNormals.w=1` 表示水面命中;旧 `RainSplashes.shader` 只画贴面的平面环,水面标记只改变亮度,没有独立竖直飞溅。
- 直接读取游戏原始 `resources.assets` 中 `SrStandardWaterShader` 的 ParsedForm:透明 SubShader 队列为 `Transparent-50`,ForwardBase 写深度;另有 `Geometry+100` 的不透明回退,主 pass 同样写深度。不能依据导出 DummyShader 判断原始行为。
- 原水花位置为解析水面上方 0.025 m;游戏水面有波浪位移。硬件深度测试可能把解析平面上的水花挡在波峰之后,低角度观察平面环还会缩成细线。这些是源码 / 资产证据,尚未用此次用户画面确定唯一根因。
- 当前游戏日志确认碰撞资源加载、碰撞 / 水花开关开启,但没有本次水面专属活槽统计;日志里部分相机 ASL 约 200 m,不等于近距离水面测试。默认水花范围仍为相机周围 25 m,远处海面不承诺逐滴效果。

## 2. 实现

| 文件 | 改动 |
|---|---|
| `Assets/Scripts/Volken/Weather/Rain/Shader/RainWaterSplashes.shader` | 水面专用单 pass:水平双涟漪随年龄扩张,亮环 / 暗边提高水面反差;沿表面法线向上、朝观察方向展开的冠状飞溅与离散液滴;保留 `FogCommon` 雾处理 |
| `Assets/Scripts/Volken/Weather/Rain/RainCollision.cs` | 加载新材质,独立双 quad mesh / 间接参数;复用现有命中池,分别绘制地面与水面;资源缺失进入状态与资产日志;Dispose / 重建释放新资源 |
| `Assets/Scripts/Volken/Weather/Rain/Shader/RainSplashes.shader` | 地面路径只绘制非水面命中;已有雾代码保留 |
| `Assets/Scripts/Volken/Weather/Rain/RainParticles.cs` | 向水花传递本相机缓存的 `CloudRenderer.LinearSceneDepth` |
| `Assets/ModData.asset` | 登记新 shader GUID,不表示游戏包已经更新 |

水面 shader 的网格 z=0 表示水平环,z=1 表示竖直飞溅。使用命中表面法线构轴,不写死世界 Y;水面 / 地面标记互斥,死槽、过期、距离外和完全透明样本不输出片元。

**遮挡**:本相机线性场景深度可用时,水面材质关闭硬件深度比较,在片元阶段以线性 eye-depth 检查场景遮挡,并用 4 cm 软边淡出;不依赖原生透明水面写入的硬件深度。没有该深度图时退回 `LessEqual`,避免无条件透过物体绘制。深度必须属于当前相机,缺失 / 未包含某类物体时的遮挡仍需游戏验证;不透明水回退是否进入线性深度也须专项回归。

池容量仍为每相机 2048 槽、每帧最多 128 次发射请求获准尝试入池。共享碰撞捕获 / Prepare / Resolve 与生成预算,新增一次水面间接绘制、每槽两个 quad;不新增场景捕获、逐帧 GPU 回读或全屏水面后处理。CPU / GPU 帧时间尚未测量。

现有水花距离、寿命、半径、密度与雨视觉透明度继续生效;没有新增面板配置或修改 XML 默认值。浮动原点与雨关停沿用原池重置,雨声仍独立于视觉透明度。

## 3. 验证

以下为删除编辑器工具前的历史验证记录;2026-10-09 已按用户要求移除 `RainWaterSplashGpuProbe.cs` 及其菜单入口。

- `dotnet build Volken.csproj --no-restore`:最终增量编译 0 错误、1 条既有程序集引用警告。
- 当时独立空 BIRP 工程复制了该测试、雨碰撞 compute、两个水花 shader 和 `FogCommon.cginc`,保持相对目录;将正式 `RainCollision.cs` 复制到独立工程的 Editor 目录,提供仅加载本地资产 / 转发日志的 `Assets.Scripts.Mod` 和该驱动所需静态字段的 `Volken.Weather.RainParticles` 替身。启用了 imgui / imageconversion 模块,使用 Unity 2022.3.62f3、D3D11 与实际图形设备,未使用 `-nographics`。
- **实际结果:53 通过 / 0 失败**,Unity 2022.3.62f3 / D3D11 / RTX 5060 Laptop GPU。覆盖解析平面 / 半径 600 万米球面 / 横向重力水面生成与高度,屋顶优先于海面;俯视 / 斜视 / 低角度可见性,写深度水面遮挡对照,前景深度遮挡,水面 / 地面互斥,死槽 / 过期 / 完全透明,涟漪扩张。
- 额外运行正式 `RainCollision.Draw`:默认新材质初始化、12 索引 / 2048 实例参数、8 顶点双 quad、`Graphics.RenderMeshIndirect` 可见性与场景深度遮挡均通过。没有靠测试材质的 instancing 开关掩盖实际初始化差异。
- 模拟水面升至解析平面上方 0.2 m 且写硬件深度,俯视对照中新路径改变 2667 个像素,硬件 `LessEqual` 路径为 0;涟漪随年龄扩张,图像宽度由 54 增至 160 像素。这是测试场景证据,不是游戏性能或观感测量。
- 输出在 `Library/RainWaterSplashProbe/Library/VolkenRainWaterProbe/`,日志 `Library/RainWaterSplashProbe/probe-native-final.log`;均被 Git 忽略。包含 summary 和真实 GPU 渲染的 `water-ripple.png`,未发现 shader 编译 / Unity 运行错误。
- 本次不构建资源包;测试结果不代表原生水面全画质档或已安装版本的真机验收。

## 4. 游戏验收

1. 原工程重新打包部署后,开发代码调用 `RainParticles.DiagStatus()` 后应出现 `waterShader=True`(游戏日志按钮已在 [UI 清理](archive/user-ui-cleanup-2026-10-09.md) 中移除);仅程序集更新不能加载新 shader。按本轮约定由用户处理打包安装。
2. 距水面 25 m 内,雨、碰撞与水花开启,透明度 <1、密度 >0;检查俯视扩散环与贴水低角度飞溅,分别测透明 / 不透明水、波浪与反射。
3. 移动机体 / 甲板 / 岸边遮挡、主相机 / PIP、无云但有深度、深度资源缺失、雾开关、暂停 / 重定位 / 离场恢复。
4. 同镜头比较开关水花的平均 / P95 帧时间;不要把固定池或单次额外绘制直接换算成 FPS。
