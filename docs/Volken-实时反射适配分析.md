# Volken2 实时反射(Reflection)适配分析

> 分析日期:2026-09
> 分析方式:读 Volken 云渲染源码 + 反编译游戏程序集(SimpleRockets2.dll / ModApi)核实游戏的实时反射实现。
> 相关文档:Volken-体积云优化点分析-VolRe与KSA借鉴.md(#7 反射探头处理)

---

## 0. 结论速览

- **现状:未适配。** 云只渲染在主相机(NearCamera)的 `OnRenderImage` 里;游戏的**水面平面反射相机**与**机体反射探头**都渲染不到云。
- **根因**:`OnRenderImage` 是"按相机"的图像特效。游戏的 `WaterReflectionCamera` 是手动 `cam.Render()` 的独立 Camera(只有 Camera 组件、无任何图像特效),`ReflectionProbeScript` 走 Unity cubemap 实时反射探头 —— 两者都不会经过 Volken 的 `OnRenderImage`。
- **适配可行**:`Clouds.shader` 的 raymarch 是"相机无关"的(观察射线由 C# 每帧传入的 `_CamFwd/_CamRight/_CamUp/_TanHalfFovV/_Aspect` 构造),只要把反射相机的参数喂进去就能从反射视角正确 raymarch;且反射纹理在 **LateUpdate** 阶段就填好、早于主相机同一帧渲染水面,游戏填完后把云合入反射纹理即可被水面采样到。
- **主攻方向 = 水面平面反射**(收益大、成本可控);**机体 cubemap 探头后置**(6 面各 raymarch,工程量大、视觉收益小)。

---

## 1. 项目现状(云渲染架构)

- `CloudRenderer`(`[ImageEffectOpaque]` `OnRenderImage`)挂在 **NearCamera**(游戏视图主相机)上,屏幕空间后处理合成云。
- `FarCameraScript` 用 **CommandBuffer** 在远相机 `AfterForwardOpaque` 抓线性深度 `farDepthTex`(不用 OnRenderImage,避免割裂线)。
- 每帧流程:远近深度合并 → 低清(每层 Cloud pass 全量 raymarch,MRT 颜色/云面距离/MV)→ MV 3×3 膨胀 → 时序上采样(Upscale,TSS)→ 逐层链式合成(Composite)。
- 时序历史(TSS)与深度纹理都是**主相机屏幕空间**的。

## 2. 游戏自带的实时反射系统(反编译依据)

### 2.1 水面平面反射 `Assets.Scripts.Terrain.Rendering.WaterReflectionPlaneScript`

- `Create(parent, mainCamera, referenceFrame)` 生成 "WaterReflectionPlane",`LateUpdate` 里定位水面平面并调 `UpdateReflections`。
- `InitializeReflectionCamera`:`new GameObject("WaterReflectionCamera").AddComponent<Camera>()` —— **只有 Camera**,`enabled=false`、`depthTextureMode=0`、`clearFlags=SolidColor`,无任何图像特效组件。
- `UpdateReflections(Vector3, Vector3)`:
  - `_reflectionCamera.targetTexture = GetReflectionTexture(Resolution)`(CraftAndTerrain=512 / CraftOnly=256,ARGB + 16 位深度)。
  - `cullingMask = reflectionOptions.Layers`(CraftAndTerrain=-1543503869,CraftOnly=-2147483645)。
  - 临时把主相机 `farClipPlane` 设为反射远裁剪(反射完还原);转一圈 skybox 旋转对齐。
  - 调 3 参 `UpdateReflections(position, normal, cam)` → 反射矩阵 + 斜切投影 → `GL.invertCulling=true; cam.Render(); GL.invertCulling=false`。
  - `Shader.SetGlobalTexture("_WaterReflectionTexture", val)` → 水面 shader 采样。
- 关键:反射相机是**独立对象、手动 Render、无图像特效** → Volken 云不可能出现在反射里。

### 2.2 机体反射探头 `Assets.Scripts.Craft.ReflectionProbeScript`

- Unity `ReflectionProbe`:`mode=Realtime`、`refreshMode=EveryFrame`(受 CraftReflectionsQuality.Realtime 控制)。
- 从机体位置渲染 **6 面 cubemap**,cullingMask = 四球面(603979793)或缩放空间(257)。
- 屏幕空间后处理云同样进不了 cubemap。

## 3. 为什么现在反射里没有云(根因)

1. `OnRenderImage` 只对"挂了该组件的相机"生效;云只挂在 NearCamera。
2. 反射相机没有 CloudRenderer/任何图像特效,手动 `cam.Render()` 直接出场景。
3. 即使把 CloudRenderer 硬挂到反射相机,现有代码也假定是主相机:
   - `SetLayerDynamicProperties` 用 `cam = GetComponent<Camera>()`(挂载相机)设置**共享的 `layer.material`** 与共享 RT(cloudTex/history/…) —— 与主相机冲突;
   - `DepthTex`(combinedDepthTex/lowResDepthTex)是主相机屏幕空间,反射相机 UV 对不上,遮挡必然错;
   - TSS 历史同样是主相机屏幕空间,不能复用。
   - 即文档里"**Volken2 现状走 OnRenderImage,未区分反射相机**"的含义。

## 4. 适配方案

### 方案 A(推荐,主收益):水面平面反射合入云

挂钩点(Harmony):
- 首选:**postfix 到 3 参 `WaterReflectionPlaneScript.UpdateReflections(Vector3, Vector3, Camera cam)`**。此时 `cam.Render()` 已把场景填进 `cam.targetTexture`(= 反射 RT),在 postfix 里把云合入该 RT;随后 2 参调用者才 `SetGlobalTexture` 指到同一 RT。
- 或 postfix 到 2 参 `UpdateReflections(Vector3, Vector3)`(全局纹理已设,用 `Shader.GetGlobalTexture("_WaterReflectionTexture")` 取 RT)。
- 顺序已验证:反射在 **LateUpdate** 填充 → 主相机**同一帧稍后**渲染水面采样该纹理 → LateUpdate 里合入的云会被水面看到。

每帧要做的(用反射相机参数):
1. 把反射相机的 `_CamFwd/_CamRight/_CamUp/_TanHalfFovV/_Aspect/clipPlanes`、`maxDepth`(= 反射 far clip)喂给云材质。
2. 用**独立 RT 与独立材质实例**(克隆 layer material),不要碰主相机的 `layer.cloudTex/historyTex/lowResDepthTex`。
3. 反射场景**关闭 TSS**(独立历史或 `_UseTemporal=0`),直接以反射分辨率 raymarch(256/512 本就不大)。
4. **深度遮挡跳过**:反射相机没有对应深纹理,不绑 `DepthTex`(或绑"全远"深度)→ 云不被地面遮挡;水面反射视角主要看天空,遮挡影响小,可后续再补反射深度。
5. **成本控制**(对齐 VolRe #7):粗步长(约 5×)、低光样本(`numLightSamplePoints` 小)、可只渲染 Main 层、蓝噪声抖动保持采样质量。

改动点:
- `CloudRenderer` 把"相机属性设置"抽成 `SetLayerDynamicPropertiesForCamera(Camera c, CloudLayer layer, bool reflection)`,主相机与反射相机共用。
- `Clouds.shader` 增加 reflection 模式(可关 DepthTex 遮挡、可切粗步长/低光样本)。
- 新增 `CloudReflectionRenderer`(或在 Harmony 里直接组织一次 raymarch → 合成)。

### 方案 B(后置):机体反射探头 `ReflectionProbeScript`

- cubemap 6 面各做一次 raymarch,工程量大、开销高;机体部件小、多数 gloss 低,视觉收益有限。
- 除非后续专门做"低清粗步长探头云",否则建议跳过/保持现状。

### 方案 C(兜底,不算适配)

- 不渲染反射云,但让水面在低空更依赖反射、高空把云色轻微混进反射强度;仅作为"无反射云"的视觉兜底。

## 5. 现有代码需要动的点(清单)

| 位置 | 现状 | 适配需要 |
|---|---|---|
| `CloudRenderer.SetLayerDynamicProperties` | 写死用挂载相机 | 抽成按相机参数化;反射相机分支(关 TSS/关深度/粗步长) |
| `CloudRenderer.OnRenderImage` | 假定主相机、共享材质/RT | 反射路径走独立材质/独立 RT,或新组件 |
| `CloudLayer` RT 集合 | 主相机屏幕空间历史 | 反射不复用历史;单独 RT 或直接合入反射纹理 |
| `Clouds.shader` | DepthTex 遮挡 + TSS | reflection 模式:跳过遮挡、可选粗步长/低光样本 |
| 材质实例 | 每层共享一个 material | 反射用 clone(material 每帧相机属性会被两个相机互踩) |

## 6. 实施顺序建议

1. 方案 A 最小闭环:挂钩 3 参 UpdateReflections → 用反射相机参数跑一次低清粗步长 raymarch → 叠加进反射 RT(先不做遮挡、先关 TSS)。
2. 验证:贴水低空 → 水面应看到云的倒影;核对性能开销(粗步长 + 低分辨率应可控)。
3. 质量迭代:反射深度遮挡、层数取舍、蓝噪声/时序。
4. 再评估方案 B(机体探头)。

## 7. 注意事项

- **浮动原点**(`ReferenceFrameRecentered`):反射相机位置同样会因原点重置平移,反射云路径也要清时序/冷启动(主相机已有修复逻辑)。
- **SOI 切换 / 无大气**:无大气时不渲染云,反射云也应同步禁用(跟随 `layer.config.enabled`)。
- **质量联动**:水面反射有 CraftAndTerrain(512)/CraftOnly(256)/None 三档,反射云分辨率/开关应跟随 `WaterQualitySettings.ReflectionQuality`。
- **HDR**:反射 RT 是 LDR-ish(Default 格式),云高亮进反射要防溢出/色带。
- **不重复劳动**:本文档 #7 已标记"未区分反射相机",方案 A 就是补上这一环。
