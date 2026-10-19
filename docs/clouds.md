# 云渲染现状

> 核对日期：2026-10-15。源码标识与历史验证摘要见 [开发与验证](development.md)，游戏待验收项见 [待办](backlog.md)。

## 文件与资源所有权

| 文件 | 职责 |
|---|---|
| [CloudRenderer.cs](../Assets/Scripts/Volken/Clouds/CloudRenderer.cs) | 每相机远近深度、raymarch、TSS、雾与逐层合成；提供 `LinearSceneDepth` |
| [CloudLayer.cs](../Assets/Scripts/Volken/Clouds/CloudLayer.cs) | 每层配置、材质、噪声、风偏移和静态参数修订 |
| [CloudLayerView.cs](../Assets/Scripts/Volken/Clouds/CloudLayerView.cs) | 每相机 × 每云层的 RT、运动数据和历史 |
| [Clouds.shader](../Assets/Scripts/Volken/Clouds/Shader/Clouds.shader) | 体积云、轨道壳面、上采样、合成与雷光遮光缓存生成 |
| [CloudVolumeLighting.cginc](../Assets/Scripts/Volken/Clouds/Shader/CloudVolumeLighting.cginc) | 共用密度、光照和有限光程消光 |
| [CloudNoise.cs](../Assets/Scripts/Volken/Clouds/CloudNoise.cs) / [StockCloudMap.cs](../Assets/Scripts/Volken/Clouds/StockCloudMap.cs) | 程序化噪声和游戏自带云分布缓存 |
| [FarCameraScript.cs](../Assets/Scripts/Volken/Clouds/FarCameraScript.cs) / [SceneDepth.cs](../Assets/Scripts/Volken/Clouds/SceneDepth.cs) | 远相机 CommandBuffer 与独立深度资源 |
| [CloudReflectionRenderer.cs](../Assets/Scripts/Volken/Clouds/CloudReflectionRenderer.cs) | 补入游戏水面反射的体积云 |
| [CloudLightningCache.cs](../Assets/Scripts/Volken/Clouds/CloudLightningCache.cs) | 世界空间雷光光学厚度图集 |
| [CloudGroundShadows.cs](../Assets/Scripts/Volken/Clouds/CloudGroundShadows.cs) | 每相机逐接收点太阳透射率与原生屏幕阴影接入 |

`CloudRenderer` 的 `[ImageEffectOpaque]` 必须保留：先处理不透明场景，再绘制原生透明物体。近远线性场景深度供云合成、雾和本相机雨软粒子；低清深度供体积步进。无有效云层时仍可生成天气需要的深度，附加相机不能借用无关主相机的远深度。

## 体积、分布与光照

- shape / detail 为原 Worley FBM，两张 128³ R8 噪声有完整 mip 链。体积、光照、云影查询和反射密度固定 mip 0；stock 分布也保持 mip 0。
- 不在密度阈值前根据 FOV、朝向或像素足迹改变世界密度。已撤回的 Perlin–Worley 替换、世界密度粗化和随镜头瓦片退场不再作为优化基线。
- 游戏 Clouds cubemap 的 R/G/B 表示低 / 中 / 高云，A 为遮罩。缺失通道回退程序化分布，缺失 A 使用中性遮罩，不把有效云乘成零。
- `stockDensityScale` 只缩放自带分布参与的附加密度项；shape 分布门继续读原分布。它不是覆盖阈值重映射，也不保证阈值处理后的轮廓完全不变。
- 太阳光和游戏环境 RGB 分开。体积及水面反射保留环境夜光，有效云段内累积环境散射，不设置固定夜间发光下限；没有独立月光输入。
- 单段光照使用 Beer 解析权重；很小光学厚度用展开式避免除零。`Upscale` 可对积分后的 RGB / 透射率做 3×3 足迹重建，拒绝场景后方邻点，并保留中心命中距离；这不等于新增射线样本或消除底层周期。

默认光样本不要混淆：`CloudConfig.CreateDefault()` 为 25，`CreateAnotherDefault()` 为 5；`VolumetricClouds` 实际初始化的附加层默认关闭，使用独立 `CreateExtraDefaultConfig()`，光样本为 10。已保存 XML 的值优先于这些默认值。

## 阳光遮蔽

[CloudSunOcclusion.cs](../Assets/Scripts/Volken/Clouds/CloudSunOcclusion.cs) 按每相机位置向太阳及 8 个余弦加权半球方向查询完整密度，每条光程固定 96 个中点、固定 mip 0，覆盖完整可见球壳，不受旧 12km 光照预算截断。9×1 浮点纹理乘法合并启用层，常规每 0.1 秒更新；每相机最多一个在途回读，视线朝向 / FOV 不参与查询。重定位、位移、太阳变化与滑条递增的 `ShaderRevision` 仅请求刷新，保留最后完成值，不因超时清成全亮；层 / 配置 / 材质 / 预设身份、模式切换、离场、环境抑制或资源失败才清结果，旧回读排空后才能再提交。不支持异步回读时整体照明保持恒等。

每层 `sunOcclusionEnabled`（阴天整体照明）和 `groundShadowsEnabled`（局部云影）均默认 false，旧 XML 不启用。直射 / 环境强度默认 0.8 / 0.5，消费时限制 0~1，非有限值回退默认；每层倍率为 `lerp(1,T,strength)`，多层相乘。局部云影启用时同层不再整体衰减直射，环境整体衰减仍仅由阴天开关控制；两种模式都查询原始太阳 T 供耀斑使用。面板开启任一模式同步选 Standard，之后仍可显式选 Additive；天气不修改这些云参数。

[CloudSceneLighting.cs](../Assets/Scripts/Volken/Clouds/CloudSceneLighting.cs) 在相机绘制期间调整游戏太阳方向光、地形昼夜色和环境光，绘制后恢复；远相机使用所属近相机查询，PIP 独立查询，未接入的嵌套反射使用原照明基线。Gamma / Linear 均按实际输出空间处理环境颜色与 BIRP 灯光强度模式，保留日食 alpha；雨 / 雾消费基线与本相机倍率，避免重复衰减，云体不整体乘相机遮蔽量。

局部云影从本相机原生深度重建地表 / 机体接收点，再沿太阳方向用同一完整密度作 96 点积分；查询图每轴至多四分之一屏幕、最长边 256，RGBA 保存逐层相乘透射率和覆写的接收深度，按深度双边重建。`LightEvent.AfterScreenspaceMask` 的独立 query / apply CommandBuffer 原地乘入原生阴影，只衰减直射，保留环境、自发光和实体投影；必须显式绑定本相机 view / 逆 VP / 位置，事件中的 Unity 相机全局量可能已被阴影绘制覆盖，也不能在同一 CB 切走 RT 后用 `CurrentActive` 恢复原阴影目标。空的 ShadowsOnly caster 全片元丢弃，仅确保无真实 caster 时也生成 mask。资源、材质和 caster 按实际近 / 远 / PIP 相机独立，退出作用域移除命令并禁用 caster。

`groundShadowDistance` 默认 20000 米，消费限 1000~100000，非有限值回退默认，末 20% 平滑退场。PreCull 临时扩大原生阴影距离，并同步相机 far、投影及剔除矩阵；禁止保留旧投影 / 剔除矩阵，否则大范围下原生 caster 可能全部被剔除。PreRender 恢复原 far 和自动 / 自定义矩阵状态，绘制裁剪与标准深度沿用原投影；中断与退出也恢复，已绘制的外层相机从嵌套返回时不能再扩 far。扩展时 2/4 级的前 N−1 级覆盖完整旧有效范围 `min(原阴影距离, 原相机 far)`，末级承担新增距离；比例扣除相机 near，避免远相机浪费内级范围，不需扩展时保留原分割，绘制后恢复原距离和分割。需 Forward、原生太阳多级阴影及接收阴影材质；单级返回 `single-cascade` 保留原生投影，不隐式升档。游戏关闭阴影、太阳 cookie、无接收变体的轨道地表不支持。合并旧级联会降低部分实体影精度，扩大剔除会增加候选几何；游戏开销、特殊自定义剔除可见性及最终画质仍待验收。

开启任一模式的层使用视线纯 Beer 消光，关闭旧地表阴影写入 alpha，Standard 不再用近景距离淡化真实透射率；体积 / 反射 / 轨道太阳项消费游戏太阳 RGB、强度和启用状态，轨道深夜门控仍保留。整体照明仍是相机位置近似，半球 8 条查询不等同精确天空积分；云内环境遮蔽见 [待办](backlog.md#已知缺口)。

## 时序与镜头稳定性

当前每帧全量绘制低清格点，使用统一的运动自适应历史混合；没有旧 MV 膨胀 pass，也不靠轮换权重冒充空间超采样。

- 云颜色 alpha 表示透射率，无云为 `(0,0,0,1)`；RFloat 云距离读取 R 通道。
- 云距离和 MV 采用同一 Point 格点。真实零位移不能向邻点借非零运动数据；MV 的 B 保存当前云点在上一相机空间的预期距离。
- 历史云面校验比较历史距离与上述预期上一距离，沿用越界、场景遮挡和运动权重；不能直接与本帧距离比较。
- TSS 关闭且 `historyBlend<=0` 时不读历史，也不执行三次全清历史写回。有历史消费者时仍保留颜色、场景深度和云距离历史。
- 静态参数修改递增层修订，每相机 `PrepareFrame` 按修订或绘制中断清历史。材质与 MRT 数组按层 / 相机复用，合成使用对应层材质。
- 重投影使用 `GL.GetGPUProjectionMatrix(..., true)`。浮动原点事件订阅 `IGameView.ReferenceFrameRecentered(IReferenceFrame, Vector3d, Vector3d)`；位置和速度变化均不超过 0.001 时忽略通知，实际变化或非有限输入仍清历史。

2026-08-25 的 Y 镜像割裂线修复曾获游戏确认。后续缩放 / 旋转卡顿不能据历史验证判定已解决；南北风的非刚性偏移仍未完整纳入云空间重投影。

## 轨道云与合成

当前 `ComputeOrbitFade` 使用海拔归一化后的 `u²(3−2u)`。过渡带两种表示同时绘制，0.001 / 0.999 端点收口，纯轨道时跳过体积 raymarch / TSS；Orbit pass 不可用时回退体积。轨道绘制独立绑定当前场景深度，不依赖上一条体积路径的绑定。

轨道轮廓、覆盖和浮雕使用现有宏观分布及恒定噪声均值标定，可选 albedo 使用固定世界尺度细节。壳面单点表示与体积积分不同，不能承诺轮廓逐像素一致；新过渡曲线和真正反平铺尚未实现。

【决策：2026-10-14】只回退 2D 轨道环境夜光，保留体积 / 反射夜光。当前轨道恢复旧太阳 `dayFactor` 门控：深夜 RGB=0，白天和晨昏沿用旧公式，覆盖和透射率仍存在。

| 合成模式 | 当前语义与限制 |
|---|---|
| Additive | 只增加云 RGB，不用云透射率压暗背景；厚云后方星空仍可能透出 |
| Standard | 使用云透射率衰减合成时已有背景；最终星空遮挡还取决于游戏绘制顺序 |

雾先作用于背景，再按体积命中 / 轨道壳距离处理云贡献；不能重复雾化背景。不同表示的深度不先平均后查雾。未启用阳光遮蔽的 Standard 仍有 `nearThreshold` 经验修正。

## 雷光与水面反射

云内雷光默认关闭。亮帧在固定 33³ 世界格点向光源步进，297×132 图集打包 9×4 切片；同帧相同材质 / 光源 / 预算可复用。观察射线只插值消光，不在每个密度段重新做遮光步进。

主相机雷光独立写低清 HDR MRT，在 Composite 只叠加 RGB，不改变透射率或基础云历史。基础颜色 / 上采样 / 历史保持 ARGB32；光灭立即清当前雷光并绑定黑纹理。暂未支持纯轨道雷光、跨 CloudLayer 遮挡或沿闪电通道多个光源。

水面反射使用独立材质和缓存，步长 ×5、光样本 2、不走 TSS，固定 `_OrbitFade=0`，显式补入游戏反射 RT。反射按自己的环境光与雷光设置绘制，不消费主相机雾。机体 ReflectionProbe 与反射雾适配尚未实现。

修改密度或采样时一起核对体积、廉价光照密度、云影查询和反射；轨道宏观分布另查覆盖 / 过渡。剩余画质与性能工作统一见 [待办](backlog.md)。
