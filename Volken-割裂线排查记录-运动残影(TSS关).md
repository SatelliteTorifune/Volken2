# Volken-割裂线排查记录 · 运动残影(TSS 关闭)

> 状态:排查中(证据充分,根因待 DIAGPROJ 探针最终确认)
> 关联方案:Volken-方案C-KSA体积云技术移植(BIRP).md
> 创建:2026-08-25(凌晨,用户睡前整理)

---

## 1. 问题描述(症状)

**现象**:屏幕中段出现水平方向的割裂线(硬接缝)。

- **TSS 关 + 运动残影(historyBlend>0)开** → 割裂线可见(经软过渡修复后变淡但仍残留)。
- **TSS 关 + 运动残影关(historyBlend=0)** → 割裂线消失。
- **TSS 开 + 运动残影开** → 割裂线附近云闪烁。

**几何特征(用户实测)**:
1. 割裂线与"摄影机和地面夹角"有关,**越垂直向下看,两条线张开越大**;
2. 截图显示:线上方云厚连续,线下方云稀疏、能看到地表;
3. 割裂线近似水平、贯穿全屏(在行星视图中落在 ~40-45% 屏高处)。

**用户历史观测**:方案C整体正常,仅此割裂线为当前主要残留问题。

---

## 2. 管线背景(与排查相关)

- BIRP 后处理链:[`ImageEffectOpaque`] OnRenderImage → 深度合并 → 低清云 raymarch(MRT:cloudTex + cloudDepthTex,RFloat)→ 深度感知上采样 → 时序混合(方案C)。
- **"运动残影" = historyBlend 滑块**(0.0~0.99,默认 0)。TSS 关时该混合路径仍在 Clouds pass 尾部生效——所以 TSS 关也看得到此问题。
- 深度管线:近相机 _CameraDepthTexture(far≈10km)与远相机 CommandBuffer(→ farDepthTex,far=1e8)经 NearDepth pass 合并为 combinedDepthTex → lowResDepthTex。
- **已知近/远缝**:近相机 far clip≈10km,远相机 near clip 若 >10km 会留空档(历史已修复过一次,改 CommandBuffer)。

---

## 3. 已实施的修复(2026-08-24,软过渡方案——效果:线变淡但未根除)

Clouds.shader fresh 路径历史接受判据从"二值"改为"软过渡":

```hlsl
float depthDiff = abs(currentDepth - historyDepth) / max(currentDepth, 0.001);
// 1) 深度权重软过渡:0.5×threshold → threshold 之间平滑
float depthWeight = 1.0 - smoothstep(historyDepthThreshold * 0.5, historyDepthThreshold, depthDiff);
// 2) 云结束边渐变:cloudSurfaceDist 接近 maxRayDist 时混合淡出(~2 步长)
float edgeFade = saturate((maxRayDist - cloudSurfaceDist) / max(2.0 * stepSize, 1.0));
// 3) 历史处有云才混(用上一帧云面距离)
float prevCloudDepth = HistoryCloudDepthTex.SampleLevel(samplerHistoryCloudDepthTex, reprojUV.xy, 0);
float cloudGate = prevCloudDepth > 0.0 ? 1.0 : 0.0;
bool badSample = (min(reprojUV.x,reprojUV.y) < 0.0) || (max(reprojUV.x,reprojUV.y) > 1.0);
float finalHistoryBlend = badSample ? 0.0 : historyBlend * depthWeight * edgeFade * cloudGate;
o.col = (1.0 - finalHistoryBlend) * raymarchOutput + finalHistoryBlend * history;
```

**结论**:修了混合因子,但割裂线残留 → 根因大概率不在混合因子,而在"历史采样位置(重投影)"。

---

## 4. 诊断日志证据(2026-08-24 深夜 / 2026-08-25 凌晨两轮)

日志源:`C:\Users\usami\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`
触发:按 **F1** 重臂探查(PROBE_FRAMES=15 帧);TSS 关、histBlend=0.90、resScale=0.5。

### 4.1 三个探查会话画像

| 会话 | 高度 | pitch | 云(中心列 alpha 剖面) | 混合状态 |
|---|---|---|---|---|
| 1 | 225~325 | 120° | 全 1.0(中心列无云,相机在云层下) | depthWeight=0,blend 关 |
| 2 | 55,947~56,045 | 113.8° | **上方密云 0.01~0.02,67%~87% 空档(alpha=1.0),87% 薄云 0.18,之下无云** | depthWeight=0(相机在动,历史被拒),blend 关 |
| 3 | 158,942~158,993 | 176.5°(近正俯视) | 全列密云 0.00~0.06(均匀) | depthWeight=1,blend 全开(90%),但 current==history,无可见线 |

**会话 2 = 割裂线现场**:云带空档的上下硬边就是线(上方密云/下方无云,与截图"上厚下疏"一致)。但该会话相机在动 → depthWeight=0 → 混合实际关闭。

### 4.2 决定性发现:重投影位移系统性过大(mode6)

- `DIAGBLEND mode=6`(= |reprojUV − i.uv|):三个会话曲线**几乎一致**——屏幕中心 ~0.07,屏幕上下缘 **~1.0 UV(整整一帧!)**。
- 相机每帧只动 ~20~70 单位;159km 距离下重投影位移应 ≈ **0.0004**。实测 0.07~1.0,**比运动预测大 100~2500 倍,且与相机状态无关**。
- 结论:**history 被采样到错误位置 → 云边残影/鬼影硬边 → 割裂线**。这是固定系统性错位(V 形径向错位 = FOV 或 view 平移不一致),不是运动误差。

### 4.3 其他已排除/次要结论

- 中心列深度场平滑,唯一的硬跳变在行星边缘(limb:表面深度 → 1e8)。近/远深度相机拼接缝在本次探测高度(55km+)未在中心列出现。
- `cloudDepthTex/histCloudDepthTex` 为 RFloat,早期探查误读 alpha(恒 1.0),已修复为读 R 通道。
- `DIAGGHOST`(当前云 vs 上一帧云 alpha 差):三会话均 none → 云场帧间稳定(均匀区收敛;差异只在云边会显现)。

### 4.4 DIAGPROJ 判定(2026-08-25 第三轮)—— FOV/平移假设被否定

本轮相机**几乎静止**(alt=267982 恒定,每帧只动 ~5 单位),DIAGPROJ 实测:

```
DIAGPROJ m00=3.5446 m11=5.6713 m22=-1.0012 m23=-12.0072 camFov=20.00 camAspect=1.6000
         reconTanV=0.1763 projTanV=0.1763 projTanH=0.2821 viewShift=0.0
```

- `projTanV(1/m11)=0.1763 == reconTanV(tan(20°/2))=0.1763` → **投影 FOV 与射线重建 FOV 完全一致**
- `projTanH(1/m00)=0.2821 == reconTanV×aspect(0.1763×1.6)` → **aspect 一致**
- `viewShift=0.0` → **view 矩阵平移与 cam.transform.position 完全一致**
- 且静止相机下 mode6(重投影位移)依旧 0.07~1.0 → **与运动无关,是重投影自身的系统性错位**

**排除**:FOV 不一致、aspect 不一致、view 平移错位、运动误差(位移比运动预测大 100~2500 倍)。

**剩两个嫌疑**:
1. shader 的 `_WorldSpaceCameraPos`(camPos)与 `prevViewProjMat` 所在空间不一致(游戏浮动原点/自改全局);
2. `prevViewProjMat` 陈旧(未每帧更新,或跨了很多帧)。

**待跑探针 DIAGPROJ2**(已加入 C# LogCamDiag,2026-08-25):
```
DIAGPROJ2 wsp=.. camPosShift=.. rpCamPos=.. rpStale=.. projFlip=.. D1000->(..,..) D100000->(..,..) D100000000->(..,..)
```
判定:
- `camPosShift`(=|_WorldSpaceCameraPos 全局 − cam.transform.position|)大 → 空间错位 → 修法 B;
- `rpStale`(=|prevViewProjMat 隐含相机位置 − 当前|)大 → 矩阵陈旧 → 查更新时机;
- CPU 复算中心像素 reprojUV(自洽时应恒为 (0.5,0.5))偏离 → 定位矩阵/空间具体错在哪个深度。

### 4.5 最终根因(2026-08-25 第四轮)—— 重投影 Y 约定镜像,已修复

DIAGPROJ2 实测:`camPosShift=0.0`(_WorldSpaceCameraPos 与 transform 一致,空间错位排除);中心像素 CPU 复算 reprojUV=(0.5,0.5)(中心射线恒如此,不能区分)。但静止相机下 mode6 曲线**完美吻合 `reprojUV.y ≈ 1 − i.uv.y`(纯垂直镜像)**:

| uv.y | 0.067 | 0.133 | 0.467 | 0.533 | 0.933 |
|---|---|---|---|---|---|
| 实测位移 | 0.87 | 0.73 | 0.07 | 0.07 | 0.87 |
| |1−2·uv.y| 预测 | 0.867 | 0.734 | 0.066 | 0.066 | 0.867 |

所有采样点全部对上 → **重投影的 Y 与射线重建的 Y 镜像**。

**机制**:Clouds 顶点着色器用 `v.vertex`(光栅化 GPU clip)重建射线,而 `prevViewProjMat = cam.projectionMatrix × worldToCameraMatrix` 用的是**逻辑投影**(GL 风格,Y-up)。D3D 下 GPU clip 与逻辑投影 Y 约定相反 → reprojUV 与 i.uv 垂直镜像 → **历史被镜像采样** → 云带边缘出现"新鲜边缘 + 镜像历史边缘"的鬼影双线 = 割裂线。TSS 关时 fresh 路径 L745 用同一套,故也可见;TSS 开时序路径 L636 同源,故线附近闪烁。

**修复(2026-08-25,CloudRenderer.cs L283)**:
```csharp
// 用 GPU 投影,使 reprojUV 与 v.vertex 的 clip 约定一致(修 fresh + 时序两条路径)
layer.prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
```
shader 内 `_ProjectionParams.x` 的 Y 翻转保持不变(两个 UV 同约定后翻转自然一致)。

---

## 5. 根因(2026-08-25 已定位并修复)

**重投影矩阵用了逻辑投影,与射线重建的光栅化 clip 约定差一个 Y 翻转 → 历史垂直镜像采样。**

- 已排除(§4.4/§4.5):FOV 不一致、aspect 不一致、view 平移错位、_WorldSpaceCameraPos 空间错位、矩阵陈旧、运动误差。
- 机制:Clouds vert 用 `v.vertex`(GPU clip)重建 viewDir;而 `prevViewProjMat` 用 `cam.projectionMatrix`(逻辑 GL 投影)。D3D 下两者 Y 约定相反 → reprojUV.y ≈ 1−i.uv.y(实测全部吻合)→ 历史采错行 → 云带边缘镜像鬼影。
- 修复:prevViewProjMat 改用 `GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)`(§4.5)。
- 待验证:下一轮跑测确认 mode6 位移 → ~0、割裂线消失。

---

## 6. 当前代码探针清单(全部为诊断用途,正常路径不变)

| 探针 | 位置 | 说明 |
|---|---|---|
| DIAGCAM | C# LogCamDiag | 相机姿态 + 云空间角(cloudPhi/dPhi) |
| DIAGPROJ | C# LogCamDiag | 矩阵一致性(已跑:全部一致,FOV/aspect/view 排除) |
| DIAGPROJ2 | C# LogCamDiag | 空间一致性(已跑:camPosShift=0,中心 UV 恒 0.5;配合 mode6 拟合出 Y 镜像根因) |
| DIAGDEPTH/DIAGCLOUD | C# CenterColumnProfile | 三列(25%/50%/75%)16 段剖面 + maxJump + 云带范围;RFloat 读 R |
| DIAGBLEND mode=1..7 | shader _DiagBlend + C# diag 重渲 | 1=depthWeight 2=edgeFade 3=cloudGate 4=finalBlend 5=depthDiff/thr 6=|reprojUV−i.uv| 7=noCloud;探查帧 0~6 各跑一模式,渲进临时 RT 不污染历史 |
| DIAGGHOST | C# ProbeGhostDiff | 当前云 vs 上一帧云 alpha 逐行差(残影直接度量) |

shader 侧:`float _DiagBlend;`(Clouds.shader L294 附近);diag 块在 fresh 路径 finalHistoryBlend 之后。
C# 侧:`Update()` 按 **F1** 重臂 `_probeFrame=0`。

---

## 7. 候选修法(确认后实施)

**修法 A(投影一致性)**:不再直接取 `cam.projectionMatrix`,改用与射线重建同源的投影:
```csharp
var P = Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, cam.farClipPlane);
layer.prevViewProjMat = P * cam.worldToCameraMatrix;
```
(若游戏在别处改过 projectionMatrix,此修法让重投影与重建/真实渲染一致。)

**修法 B(平移错位)**:若 viewShift 大,用 `cam.worldToCameraMatrix` 的平移反推相机位置,让 shader 的 camPos 与该视图矩阵一致(或统一到同空间)。

**修法 C(降级方案)**:若矩阵问题难根治,在云边用更宽的 edgeFade(云壳厚度量级)+ 软 cloudGate,牺牲部分残影平滑换取无割裂(治标)。

**遗留(已知局限,未纳入本轮)**:
- 云空间重投影只覆盖绕 Y 自转 + 东西风平移;**N/S 方向风未覆盖** → 该方向云动时残影仍可能残留。
- TSS 开时 !isFresh 时序路径的硬 `HistoryTex.Sample(i.uv)` 回退(约 L620-652)仍可能闪烁,未做软处理。

---

## 8. 复测步骤(根因已修,2026-08-25)

1. 保持 TSS 关 + 运动残影开(histBlend 0.9)。
2. 按 **F1**;静止几帧 + 转角度看割裂线(俯视+斜视各一组)。
3. 看 **mode=6**(|reprojUV−i.uv|):修复后静止时应 **≈0**(之前 0.07~1.0)。
4. 目视确认:**割裂线消失**;运动残影平滑是否恢复正常(不再镜像鬼影)。
5. 若 mode6 变成"倒过来的 V"(镜像反了),把 shader 里 reprojUV 的 `_ProjectionParams.x` 翻转条件对调一行即可(两个 UV 都要对调)。
