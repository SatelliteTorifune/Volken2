# Volken 水体系统大修可行性分析

> 基于反编译源码 `C:\renko\shitProgram\jnoCode` 与解包工程 `C:\renko\unityProjects\JNOd2`(Unity 2022.3)。
> 目标:评估"把 SR2 整个水体系统做得更好看"的可行路径、代价与风险。

---

## 1. 现状:游戏水体是怎么搭的

### 1.1 几何
- 海洋是**行星四叉球(QuadSphere)的一个水层**,不是独立平面。顶点结构见
  `Assets.Scripts.Terrain.MeshDataWater.WaterVertex`(position / normal / half4 color / uv1 / uv2 / uv3)。
- 水跟随行星曲率,顶点带 normal(球面法线),uv2/uv3 供海岸泡沫、波浪偏移等用。
- 所以水面不是一张大平面,不能像普通小场景那样直接换平面网格;它随 LOD 细分,
  还有远相机(FarCamera)渲染路径(见 `WaterMaterialModifier.SetShaderLod` 的 LOD 阶梯)。

### 1.2 材质与 shader
- 材质:`PlanetQuadWaterMaterial`,shader 为 `Jundroo/SR Standard/SrStandardWaterShader`。
- 解包出的 .shader 是 **DummyShaderTextExporter**——真正的 HLSL 编译进了 player,
  **源码不可得**,只保留了 Properties(这正是"大修"最大的约束,见 §3)。
- 但 Properties 保留了全部暴露参数:波浪两张法线贴图 + 三种混合、移动方式、
  透明度深度、折射/反射扭曲强度、Fresnel、泡沫、曲面细分边长、波幅/波长/波速/波时间/波偏移、
  大气与光照质量 keyword。

### 1.3 中央调度:WaterMaterialModifier
`ModApi.Planet.Modifiers.Material.WaterMaterialModifier` 是水体渲染的"总管":
- 实例化水材质,把 `PlanetWaterConfig`(每颗行星/生物群系的水配置)灌进材质;
- 按 `WaterQualitySettings` 开关 keyword(`WATER_NORMAL_MAPS_BLENDED/BLENDED_FAST`)、
  折射(`Transparency`)、反射(`Reflections`)、波浪(`Waves`);
- 用 **shader.maximumLOD 阶梯**(100/200/300/400/510/520/530/540)组合出
  折射/反射/波浪/法线混合的功能开关;
- 每帧更新 `_WaveTime`,并在参考系重定位时更新 `_WaveOffset`。

### 1.4 波浪(CPU 与 GPU 两套)
- GPU:曲面细分(`_TessellationEdgeLength`)+ 波位移(`_WaveAmplitude/_WaveLength/_WaveSpeed`)
  + 两张法线贴图混合;远距离由 `_MaxDisplacementDist` 淡出。
- CPU(浮力/取水高):`ReferenceFrame.GetWaterWaveOffset` = 三个正交方向正弦波之和
  `A·(sin k(x−ct)+sin k(y−ct)+sin k(z−ct))`,按生物群系 `WaveAmplitudeScale` 缩放,
  且**离岸 20m 内线性淡出**(`clamp01(agl/20)`)。
- 已知隐患:**CPU 浮力波浪与 GPU 视觉波浪不是同一套模型**,改一边不动另一边会
  造成"船在视觉水面之上下漂"。

### 1.5 反射 / 折射 / 水下
- 反射:`WaterReflectionPlaneScript` 平面反射(512/256,CraftOnly 或 CraftAndTerrain),
  写 `_WaterReflectionTexture` + 天空盒 `_Rotation`。**Volken 方案 A 就是挂在这里**。
- 折射:`WaterTransparencyCameraScript` 用一个 CommandBuffer(相机事件 16)
  抓屏到 `_WaterRefractionTexture`,透明水开启时生效。
- 水下:五个后期 shader(SrBlur/SrEdge/SrFisheye/SrQuad/SrVortex)+
  `PlanetWaterConfig` 的水下颜色/暗色/强度/光衰减,由
  `UnderwaterBlur/Distortion/ExitEffect` 三个质量开关控制。

### 1.6 配置面(能直接调的"旋钮")
`ModApi.Planet.PlanetWaterConfig` 每颗行星/生物群系都有:色深渐变、泡沫色/深度/强度、
Fresnel、反射/折射扭曲、反射强度、纹理强度、透明度深度/强度、波幅/波长/波速、
水下色/强度/暗色/光衰减、金属度、平滑度、发光度。

---

## 2. 可行路线(按代价/收益排序)

### A. 运行时改材质参数 —— 最便宜,先做
游戏已经把几乎所有外观参数暴露在材质 + `PlanetWaterConfig` 上,而且
`WaterMaterialModifier` 每个质量变更都会重新写一遍。mod 可以直接:
- 强制 `WATER_NORMAL_MAPS_BLENDED`(最高法线质量)、`Reflections=CraftAndTerrain`、
  `Waves=true`、`Transparency=true`;
- 调高 `_ReflectionDistortionStrength`、`_FresnelBias`、`_FoamStrength`、波幅/波长;
- 按行星改 `PlanetWaterConfig`(颜色渐变、泡沫、水下色)。

Volken 的 `ForceSetting` 已经在干这件事(按高度切透明)。收益即时、风险几乎为零。
**结论:完全可行,建议作为第一阶段。**

### B. 水下观感 —— 低代价
水下后期 + 水下颜色已经是完整链路,只是被质量预设关掉或参数保守。mod 强制开启
`UnderwaterBlur/Distortion/ExitEffect` 并按行星调 `_underwaterColor/_underwaterDarkColor/
_underwaterLightFade*`,即可明显改善。**可行,风险低。**

### C. 反射增强 —— 中代价(方案 A 已完成一半)
- 方案 A(反射云)已落地并加开关。
- 进一步:把反射分辨率/格式提高、或做屏幕空间反射 SSR 补平面反射漏掉的部分。
- 约束:反射 RT 由 `WaterReflectionOptions`(512/256、LDR)控制,反射相机 culling mask
  只含 craft/terrain;天空是天空盒 + 我们的云补进去的。SSR 要在主相机后处理里做,
  与 Volken 的 CloudRenderer 后处理同一条链,**可行但需小心性能**。

### D. 波浪真实化 —— 中~高代价,最大坑在"两套波浪一致性"
- 只加 GPU 波浪(调高波幅/波长/细分):便宜,但**船会与视觉水面脱节**,必须同步改 CPU。
- 把 CPU 的 3 正弦换成 Gerstner,并让 shader 用同一套解析式:需要**替换 shader**(
  见 E),同时重写 `GetWaterWaveOffset`(Harmony patch)。中等工程。
- 真正的 FFT 海洋:要 compute + 置换贴图 + 全新 shader,对这个"行星级 + 可飞远"的游戏
  收益边际小,**不建议首期做**。

### E. 整换水体 shader(真正的"大修")—— 高代价、高收益、但受限
因为原 shader 源码不可得,要彻底重写水体只能:**mod 自带一个水 shader
(asset bundle,和 Volken 云 `Hidden/Volken/Clouds` 同套路),再把 `PlanetQuadWaterMaterial`
的 shader 换掉**。需要满足的硬约束:
1. 网格格式:quad-sphere 的 `WaterVertex`(position/normal/color/uv1/uv2/uv3),
   曲面细分与 LOD 都要兼容;
2. `WaterMaterialModifier` 会在质量变更时重设 `shader.maximumLOD` 和 keyword——
   要么 Harmony patch 掉这个行为,要么自定义 shader 容忍/忽略这些调用;
3. 行星尺度:浮点精度(浮点原点重定位)、远相机(FarCamera)另一条 LOD 路径、
   参考系 `_WaveOffset/_Rotation` 约定都要照搬;
4. 契约:`_WaterReflectionTexture`(平面反射)、`_WaterRefractionTexture`(抓屏)、
   天空盒 `_Rotation`、`_WaveTime` 这些"输入"要保持,否则反射/折射/昼夜全断。

工作量估计:一个高质量 BIRP 水 shader(带 Gerstner/泡沫/Fresnel/深浅色/折射抓屏/反射采样)
+ 一个 `WaterShaderSwapper` 管理器 + 若干 Harmony patch 兜住 `WaterMaterialModifier`。
**可行,但这是数周级的独立子项目**,而且每颗行星的参数仍需接回 `PlanetWaterConfig`。

---

## 3. 结论

| 路线 | 代价 | 观感收益 | 风险 | 建议 |
|---|---|---|---|---|
| A 运行时调参 | 很低 | 中高(立竿见影) | 低 | **立即做** |
| B 水下 | 低 | 中 | 低 | 紧接着做 |
| C 反射增强 | 中 | 中(云已加) | 中(性能) | 视性能预算 |
| D 波浪一致性 | 中~高 | 中高 | 高(物理脱节) | 需谨慎 |
| E 整换 shader | 高(数周) | 高 | 中高 | 作为可选终局 |

**总判断:值得做,但"整个大修"应从 A/B 的零风险调参起步,而不是一上来换 shader。**
游戏水体的"原料"其实很好(曲面细分 + 双法线混合 + 平面反射 + 抓屏折射 + 深度泡沫 +
水下后期 + 每行星色彩配置都已存在),只是默认质量预设把很多功能压低了、参数偏保守。
先用 A/B 把这些"已有的漂亮功能"顶满,大概率就能满足观感需求;只有仍不满意时,
才需要 D/E 去动波浪模型和 shader 本体。

---

## 4. 给 Volken 的具体落地建议

1. **新增一个 `WaterEnhancer`(MonoBehaviourBase)**:
   - `ModSettings` 里加分组:反射云(已有 `WaterReflection`)、"强化水面/水下"开关;
   - 每帧/每 N 秒对水材质 SetFloat/EnableKeyword(参考 `WaterMaterialModifier.UpdateShaderData`);
   - 强制 Blended 法线、按需抬反射扭曲/Fresnel/泡沫/波幅;
   - 同步改 `Game.Instance.QualitySettings.Water` 的 Reflections/Transparency/Waves 目标值
     (注意只当用户开开关时才改,避免覆盖用户选择——同 `ForceSetting` 的尊重原则)。
2. **反射云开关已接好**:`CloudReflectionRenderer.Render` 开头读
   `ModSettings.Instance.WaterReflection`(默认关)。
3. 水下:开关开时强制 `UnderwaterBlur/Distortion/ExitEffect`,并按行星 `PlanetWaterConfig`
   调 `_underwaterColorIntensity/_underwaterLightFadeDepth`。
4. 波浪:先在 A 阶段只调幅度/波长/细分,同时**不改 CPU 波浪**(避免物理脱节);
   若脱节明显,再进入 D(重写 CPU 解析式 + 换 shader)。

---

## 5. 附:关键源码位置速查

- 水材质总管:`ModApi/Planet/Modifiers/Material/WaterMaterialModifier.cs`
- 水配置:`ModApi/Planet/PlanetWaterConfig.cs`
- 水质量:`ModApi/Settings/WaterQualitySettings.cs`
- 反射:`SimpleRockets2/Assets/Scripts/Terrain/Rendering/WaterReflectionPlaneScript.cs`
- 折射:`SimpleRockets2/Assets/Scripts/Terrain/Rendering/WaterTransparencyCameraScript.cs`
- 水网格顶点:`SimpleRockets2/Assets/Scripts/Terrain/MeshDataWater.cs`
- CPU 波浪/取水高:`SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs`
  (`GetWaterPosBelowPoint` / `GetWaterWaveOffset`)
- 解包材质与 shader 属性:`JNOd2/Assets/Resources/planets/materials/PlanetQuadWaterMaterial.mat`、
  `JNOd2/Assets/Shader/Jundroo_SR Standard_SrStandardWaterShader.shader`(dummy,仅属性)
