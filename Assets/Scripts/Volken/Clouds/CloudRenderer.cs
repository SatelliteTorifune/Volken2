using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;
/*
    Volken Pipeline Overview (Multi-Layer, Multi-Camera):
    1. Write depth from far camera to a render texture
    2. Write depth from near camera to the same texture
    3. Downsample the combined depth texture for later use in depth aware upscaling
    4. For each active layer:
       a. Set dynamic shader properties (wind, rotation, reprojection matrix)
       b. Render volumetrics to view.cloudTex (optionally blend with history buffer)
       c. Copy output to history buffer
    5. For each active layer: Upscale view.cloudTex to full resolution
    6. Chain-composite all layers onto the scene:
       - Additive mode: result.rgb += cloudColor (zero visual interference)
       - Standard mode: result = src * transmittance + cloudColor (physical occlusion)
    Multi-Camera (PIP 等额外摄像机) 设计:
    - 每个 CloudRenderer 实例 = 一台相机,所有渲染目标 / 时序历史 / 云空间重投影都在
      CloudLayerView(本实例独享)里,与其它相机完全隔离 —— 不同相机互不踩坏对方的 RT/历史。
    - farDepthSource 指向"这台相机的远相机"的 FarCameraScript(主视角 = 游戏 FarCamera;
      额外相机 = 与其共享同一 targetTexture 的更低 depth 相机,即 PIP 的 scaled-space 克隆)。
    - 风/自转累积量是全球共享的 CloudLayer 状态,每帧只推进一次(AdvanceGlobalCloudState)。
    - 保留 [ImageEffectOpaque](必须):体积云在【不透明 pass 之后、透明 pass 之前】合成,
      透明物体(水面/玻璃/UI)才能正确遮挡云。去掉后云被画在所有透明内容之上(大问题)。
      原"开 PIP 后全部云消失 + 报错"的根因是全局共享状态(static farDepthTex + 全局一套
      RT/历史,多相机互相 Release/重建),已由每相机隔离修复;属性本身每相机独立生效,与
      是否多相机无关。
*/

namespace Volken.Clouds
{
    

    public class CloudRenderer : MonoBehaviour
    {
        // === Shared depth render targets (one set per CloudRenderer instance / camera) ===
        private RenderTexture combinedDepthTex;
        private RenderTexture lowResDepthTex;
        private Camera cam;
        private static Mesh _fullscreenTriangle; // 阶段二 MRT 全屏三角形

        /// <summary>
        /// 本实例的远相机深度源(主视角 = 游戏 FarCamera 的 FarCameraScript;PIP = 与克隆主相机
        /// 共享同一 targetTexture 的更低 depth 相机上的 FarCameraScript)。
        /// </summary>
        public FarCameraScript farDepthSource;

        /// <summary>本相机独享的每层渲染状态(与其它 CloudRenderer 实例完全隔离)。</summary>
        private readonly Dictionary<CloudLayer, CloudLayerView> _views = new Dictionary<CloudLayer, CloudLayerView>();

        /// <summary>
        /// 本相机本帧的**线性场景深度纹理**(远深度 + 近深度合并后的 RFloat)。
        ///
        /// 【当前无消费者 —— 2026-09-27】这个只读口当初是为天气系统的**雾通道**
        /// (FogRenderer)加的:雾需要"沿视线的场景距离"才能算积分终点,而本工程里
        /// 唯一可用的线性深度就是这张图(`DepthCapture.cs` 是死代码,它 Shader.Find 的
        /// `Hidden/DepthLinear` 在工程里根本不存在)。雾已按用户要求整体移除,
        /// 所以现在**没有任何代码读它**。
        ///
        /// 之所以保留:它零成本(只是一个属性),而且是"将来任何需要场景深度以米为单位"
        /// 的功能(重做雾、水下效果、深度相关的后处理)最自然的入口。
        /// 若确定不再需要,删除这个属性即可(不影响云管线,云内部直接用 combinedDepthTex 字段)。
        /// </summary>
        public RenderTexture LinearSceneDepth => combinedDepthTex;


        // 全球风/自转推进:每帧只推进一次(所有相机共享同一全局云状态;推进两次 = 云速翻倍)
        private static int _lastGlobalAdvanceFrame = -1;

        // KSA 完整结构:非新鲜格的本帧 raymarch 混合权重(lerp(重投影历史, 本帧, _TssBlend))。
        // 本帧分量越大越追运动(不拖影),历史降噪越弱;0.5 平衡追踪与降噪。
        private const float kTssFreshBlend = 0.5f;

        public CloudRenderer()
        {
            cam = GetComponent<Camera>();
            CloudRenderManualRefresh();
            Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
            // 游戏重置坐标原点(浮动原点)时,上一帧存的 prevViewProjMat 是旧原点矩阵,
            // 本帧世界位置是新原点 → 时序重投影失效 → 云偏移。订阅 ModApi IGameView 的
            // ReferenceFrameRecentered 事件,在回调里清空时序历史(冷启动),见 OnReferenceFrameRecentered。
            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered += OnReferenceFrameRecentered;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer cannot subscribe ReferenceFrameRecentered: " + ex.Message);
            }
        }

        private void OnEnable()
        {
            // 本相机需要自己的深度纹理:Clouds.shader 的 NearDepth pass 采样 _CameraDepthTexture。
            // 额外相机(JNO 不会帮它开 depthTextureMode)必须由我们自己开。
            if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;
            TryResolveFarDepthSource();
        }

        private void OnPlayerChangedSoi(ICraftNode playerCraftNode, IPlanetNode newParent)
        {
            if (playerCraftNode.Parent.Parent == null)
            {
                // Sun has no clouds
                foreach (var layer in Volken.Core.VolkenMod.Instance.layers)
                {
                    if (layer?.config != null)
                    {
                        layer.config.enabled = false;
                    }
                }
            }
            else
            {
                bool hasAtmo = newParent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
                foreach (var layer in Volken.Core.VolkenMod.Instance.layers)
                {
                    if (layer?.config != null)
                    {
                        layer.config.enabled = hasAtmo && layer.config.enabled;
                    }
                }
            }
            // 方案 C:切换天体 → 本相机历史失效 → 冷启动全步进(每相机各清各的)
            foreach (var view in _views.Values)
            {
                view.frameNumber = 0;
                view.prevCloudAngle = float.NaN; // 云转角相位作废,首帧回退世界空间重投影
            }
        }

        /// <summary>
        /// 游戏重置坐标原点(浮动原点 GameViewScript.RecenterReferenceFrame,触发:离帧中心 >5000m /
        /// 帧速度 >1000m/s / 时间加速每帧 / 表面锁定状态切换)时,Unity 世界坐标在两帧间整体平移
        /// positionDelta:上一帧存的 prevViewProjMat 是旧原点矩阵,本帧世界位置(含 sphereCenter)是
        /// 新原点 → 时序重投影 UV 错位 → 云偏移。
        /// 处理:清空全部时序历史 + frameNumber=0 强制冷启动 → Upscale 的 validHist 全 0 → 全走本帧
        /// 新鲜 raymarch(云不偏移)。不动 prevCloudAngle(云自转/风相位与原点重置无关)。
        /// </summary>
        private void OnReferenceFrameRecentered(ModApi.Flight.GameView.IReferenceFrame referenceFrame, Vector3d positionDelta, Vector3d velocityDelta)
        {
            try
            {
                Mod.Log("Volken:CloudRenderer frame recentered Δ=" + positionDelta.magnitude.ToString("F1") + "m — TSS history cleared");
                foreach (var view in _views.Values)
                {
                    view.frameNumber = 0;
                    view.ClearHistory();
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer OnReferenceFrameRecentered ERROR: " + ex.Message);
            }
        }

        public void CloudRenderManualRefresh()
        {
            BuildViews();
            CreateSharedRenderTextures();
            SetAllLayersShaderProperties();
            // Ensure each layer has its RTs created
            foreach (var kv in _views)
            {
                if (kv.Key != null && kv.Key.config != null)
                {
                    kv.Value.currentResolutionScale = kv.Key.config.resolutionScale;
                }
            }
        }

        /// <summary>把 _views 与 Volken.Core.VolkenMod.Instance.layers 对齐(层列表运行时不变,防御性重建)。</summary>
        private void BuildViews()
        {
            if (Volken.Core.VolkenMod.Instance == null) return;
            var stale = _views.Keys.Where(k => !Volken.Core.VolkenMod.Instance.layers.Contains(k)).ToList();
            foreach (var k in stale)
            {
                _views[k].ReleaseRenderTextures();
                _views.Remove(k);
            }
            foreach (var layer in Volken.Core.VolkenMod.Instance.layers)
            {
                if (layer != null && !_views.ContainsKey(layer)) _views[layer] = new CloudLayerView(layer);
            }
        }

        private CloudLayerView GetView(CloudLayer layer)
        {
            CloudLayerView view;
            _views.TryGetValue(layer, out view);
            return view;
        }

        private void CreateSharedRenderTextures()
        {
            var res = Screen.currentResolution;
            EnsureDepthTextures(Mathf.Max(1, res.width), Mathf.Max(1, res.height));
            EnsureLowResDepthTex(new Vector2Int(Mathf.Max(1, res.width / 2), Mathf.Max(1, res.height / 2)));
        }

        /// <summary>按【本相机输出尺寸】创建/重建全清深度纹理(不再写死 Screen.currentResolution)。</summary>
        private void EnsureDepthTextures(int renderW, int renderH)
        {
            if (combinedDepthTex != null && combinedDepthTex.IsCreated() &&
                combinedDepthTex.width == renderW && combinedDepthTex.height == renderH)
                return;

            if (combinedDepthTex != null && combinedDepthTex.IsCreated())
                combinedDepthTex.Release();
            combinedDepthTex = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat);
            combinedDepthTex.Create();
        }

        private void EnsureLowResDepthTex(Vector2Int targetSize)
        {
            if (lowResDepthTex != null &&
                lowResDepthTex.width == targetSize.x &&
                lowResDepthTex.height == targetSize.y)
                return;

            if (lowResDepthTex != null && lowResDepthTex.IsCreated())
                lowResDepthTex.Release();

            lowResDepthTex = new RenderTexture(Mathf.Max(1, targetSize.x), Mathf.Max(1, targetSize.y), 0, RenderTextureFormat.RFloat);
            lowResDepthTex.Create();
        }

        private void ReleaseAllRenderTextures()
        {
            if (combinedDepthTex != null && combinedDepthTex.IsCreated())
                combinedDepthTex.Release();
            if (lowResDepthTex != null && lowResDepthTex.IsCreated())
                lowResDepthTex.Release();

            foreach (var view in _views.Values)
            {
                view?.ReleaseRenderTextures();
            }
        }

        /// <summary>
        /// Called by Volken.Core.VolkenMod.ValueChanged() when any layer's config changes.
        /// Re-applies static shader properties for all layers.
        /// </summary>
        public void SetAllLayersShaderProperties()
        {
            try
            {
                foreach (var layer in Volken.Core.VolkenMod.Instance.layers)
                {
                    if (layer?.config == null || layer.material == null) continue;

                    layer.SetStaticShaderProperties();
                }
            }
            catch (Exception)
            {
                Mod.Log("Volken:CloudRenderer.SetAllLayersShaderProperties" + Environment.StackTrace);
            }
        }

        /// <summary>
        /// 解析本实例的远相机深度源(纯通用规则,不依赖任何 mod 的名字/类型/函数):
        /// 1) 与本相机共享同一 targetTexture 的"更低 depth 相机"(= PIP 的 scaled-space 克隆相机,
        ///    PigeonEye 把克隆太空相机挂在克隆主相机下且共用一个渲染目标)。优先子相机,其次任意相机。
        /// 2) 主视角回退:游戏 FarCamera(Volken.Core.VolkenMod.Instance.farCam)。
        /// </summary>
        public void TryResolveFarDepthSource()
        {
            if (farDepthSource != null) return;
            try
            {
                Camera spaceCam = null;
                if (cam != null)
                {
                    foreach (Camera child in GetComponentsInChildren<Camera>(true))
                    {
                        if (child == cam) continue;
                        if (child.targetTexture == cam.targetTexture && child.depth < cam.depth)
                        {
                            spaceCam = child;
                            break;
                        }
                    }
                }
                if (spaceCam == null && cam != null && cam.targetTexture != null)
                {
                    foreach (Camera other in UnityEngine.Object.FindObjectsOfType<Camera>())
                    {
                        if (other == null || other == cam) continue;
                        if (other.targetTexture == cam.targetTexture && other.depth < cam.depth)
                        {
                            spaceCam = other;
                            break;
                        }
                    }
                }
                if (spaceCam != null)
                {
                    var fcs = spaceCam.GetComponent<FarCameraScript>();
                    if (fcs == null) fcs = spaceCam.gameObject.AddComponent<FarCameraScript>();
                    farDepthSource = fcs;
                    return;
                }
                // 主视角:回退到游戏 FarCamera
                if (Volken.Core.VolkenMod.Instance?.farCam != null) farDepthSource = Volken.Core.VolkenMod.Instance.farCam;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer.TryResolveFarDepthSource ERROR: " + ex.Message);
            }
        }

        /// <summary>
        /// 全球风/自转推进(所有相机共享):每帧只执行一次。
        /// 旧实现把这段写在 SetLayerDynamicProperties 里,每台渲染相机都会推一次 → 有 N 台额外
        /// 相机时云速/自转 N 倍加速。
        /// </summary>
        private static void AdvanceGlobalCloudState()
        {
            if (_lastGlobalAdvanceFrame == Time.frameCount) return;
            _lastGlobalAdvanceFrame = Time.frameCount;

            if (Volken.Core.VolkenMod.Instance == null) return;
            var craftNode = Game.Instance?.FlightScene?.CraftNode;
            if (craftNode == null || craftNode.ReferenceFrame == null || craftNode.CraftScript == null) return;
            float deltaTime = (float)Game.Instance.FlightScene.TimeManager.DeltaTime;

            foreach (var layer in Volken.Core.VolkenMod.Instance.layers)
            {
                if (layer?.config == null) continue;

                // Wind with direction
                Vector3 north = craftNode.ReferenceFrame.PlanetToFrameVector(craftNode.CraftScript.FlightData.North);
                Vector3 east = craftNode.ReferenceFrame.PlanetToFrameVector(craftNode.CraftScript.FlightData.East);
                float rad = Mathf.Deg2Rad * layer.config.windDirection;
                Vector3 windDir = Mathf.Cos(rad) * north + Mathf.Sin(rad) * east;
                float speedFactor = GetWindSpeedFactor(layer.config.windDirection);

                // Update running offset (don't modify config.offset directly)
                layer.runningOffset += layer.config.windSpeed * 0.1f * speedFactor * deltaTime * windDir;
                layer.runningOffset.x -= Mathf.Floor(layer.runningOffset.x);
                layer.runningOffset.y -= Mathf.Floor(layer.runningOffset.y);
                layer.runningOffset.z -= Mathf.Floor(layer.runningOffset.z);

                // Self-rotation
                layer.accumulatedRotation += layer.config.globalRotationAngular * 5e-4f * deltaTime;
            }
        }

        /// <summary>
        /// Sets per-frame dynamic properties for a specific layer on THIS camera.
        /// </summary>
        public void SetLayerDynamicProperties(CloudLayer layer, CloudLayerView view, int renderW, int renderH)
        {
            if (layer?.config == null || layer.material == null || view == null) return;

            var craftNode = Game.Instance.FlightScene.CraftNode;
            Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;

            var mat = layer.material;
            mat.SetFloat("currentRotation", layer.accumulatedRotation);
            mat.SetFloat("maxDepth", 0.9f * (farDepthSource != null ? farDepthSource.maxFarDepth : cam.farClipPlane));
            mat.SetVector("sphereCenter", planetCenter);
            mat.SetVector("lightDir", sun.transform.forward);
            mat.SetVector("cloudOffset", layer.runningOffset);
            float time = (float)Game.Instance.GameState.GetCurrentTime();
            mat.SetVector("blueNoiseOffset", new Vector2(
                Mathf.PerlinNoise(time * 0.5f + layer.layerIndex * 0.3f, 0f) * 2f - 1f,
                Mathf.PerlinNoise(0f, time * 0.5f + layer.layerIndex * 0.3f) * 2f - 1f
            ));
            // === 方案 C §5:历史重投影必须在"云空间"做 ===
            // 云自身在运动(currentRotation 自转 + runningOffset 风平移),纯世界空间 prevViewProj 假设云静止,
            // 云一动 → reprojUV 采到旧位置的云 → 运动残影/鬼影;云水平运动(绕Y自转+东西风)使残影横向拖拽,
            // 混合因子 depthWeight/badSample 是二值翻动 → 在残影边缘形成水平割裂线。
            // 该历史混合路径在 Clouds pass 尾部恒在(时序开关关闭也生效),故时序关也会看到此问题。
            // 近似版:把"云转角增量 Δφ"复合进重投影——本帧云面位置先按云的转动平移回上一帧的云位置,
            // 再乘上一帧 view-proj → reprojUV 指向"同一云特征"上一帧的位置,残影/割裂随之消失。
            // φ = 自转累积角 + 风平移折算的经度角(2π·runningOffset.x,spherical.x 单位 1=2π)。
            float cloudPhi = layer.accumulatedRotation + 2.0f * Mathf.PI * layer.runningOffset.x;
            float dPhi = float.IsNaN(view.prevCloudAngle) ? 0.0f : cloudPhi - view.prevCloudAngle;
            mat.SetMatrix("reprojMat", view.prevViewProjMat * BuildCloudSpaceRepro(dPhi, planetCenter));
            view.prevCloudAngle = cloudPhi;
            // 阶段二:观察射线用相机 transform 轴直接构造(NDC 来自 clip 坐标,无投影矩阵约定歧义)。
            // 注意:不要用 cameraToWorldMatrix 的第2列当 fwd——Unity 视图约定里那是 -forward,会反向。
            mat.SetVector("_CamPos", cam.transform.position);
            mat.SetFloat("_ReflectionMode", 0f);
            mat.SetVector("_CamFwd", cam.transform.forward);
            mat.SetVector("_CamRight", cam.transform.right);
            mat.SetVector("_CamUp", cam.transform.up);
            mat.SetFloat("_TanHalfFovV", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // 宽高比:以本相机当前渲染目标(source)为准 —— 额外相机的 targetTexture 宽高比才是其真实视场
            mat.SetFloat("_Aspect", renderW / (float)Mathf.Max(1, renderH));
            mat.SetVector("clipPlanes", new Vector2(cam.nearClipPlane, cam.farClipPlane));

            mat.SetFloat("_NearThreshold", layer.config.nearThreshold);

            // Per-layer resolution-aware blue noise scale(按本相机输出尺寸)
            mat.SetVector("blueNoiseScale",
                layer.config.resolutionScale * new Vector2(renderW, renderH) / 512.0f);

            // Surface radius (shared across layers)
            mat.SetFloat("surfaceRadius", (float)Game.Instance.FlightScene.CraftNode.Parent.PlanetData.Radius);

            // 方案 B: 游戏自带云 cubemap + 参考系→星球本体系旋转(每帧更新,因参考系随飞行/轨道变化)
            mat.SetTexture("StockCloudCube", StockCloudMap.Current);
            var referenceFrame = craftNode.ReferenceFrame;
            if (referenceFrame != null)
            {
                Matrix4x4 bodyFromFrame = new Matrix4x4();
                var bx = referenceFrame.FrameToPlanetVector(Vector3.right);
                var by = referenceFrame.FrameToPlanetVector(Vector3.up);
                var bz = referenceFrame.FrameToPlanetVector(Vector3.forward);
                bodyFromFrame.m00 = (float)bx.x; bodyFromFrame.m10 = (float)bx.y; bodyFromFrame.m20 = (float)bx.z;
                bodyFromFrame.m01 = (float)by.x; bodyFromFrame.m11 = (float)by.y; bodyFromFrame.m21 = (float)by.z;
                bodyFromFrame.m02 = (float)bz.x; bodyFromFrame.m12 = (float)bz.y; bodyFromFrame.m22 = (float)bz.z;
                bodyFromFrame.m33 = 1f;
                mat.SetMatrix("planetToBody", bodyFromFrame);
            }

            // === 方案 C: KSA 完整结构(2026-08-25) ===
            // 每帧流程:Clouds pass 低清全量 raymarch(+ 本帧 MV)→ DilateMV 膨胀本帧 MV →
            // Upscale pass 全清时序累积(新鲜格取本帧、非新鲜格 lerp(重投影历史, 本帧))。
            // 每个像素每帧都有【本帧】raymarch 数据 → 运动/缩放也不再拖影(不依赖滞后的上一帧数据),
            // 因此运动门控已移除;格网只决定"哪些格把本帧直接写历史"。
            var tcfg = layer.config;
            int upX = Mathf.Max(1, tcfg.upscaleX);
            int upY = Mathf.Max(1, tcfg.upscaleY);
            int totalCells = upX * upY;

            // 时序混合权重:
            //   _TssBlend  = 非新鲜格的本帧分量(追运动;越大越追、历史降噪越弱)
            //   historyBlend = TSS 关时的历史权重(运动残影 0.90);TSS 开时 shader 不使用
            mat.SetFloat("_TssBlend", kTssFreshBlend);
            mat.SetFloat("historyBlend", tcfg.historyBlend);

            if (tcfg.useTemporalUpscale)
            {
                if (view.temporalSequence == null || view.temporalSequence.Length != totalCells)
                {
                    view.temporalSequence = UpscalingPixelSequence.FindOptimalSamplingSequence(upX, upY);
                    view.frameNumber = 0;   // 格网变化 → 历史相位作废 → 冷启动
                }
                // 冷启动无需特判全步进:历史为空时 Upscale 走"本帧有效"分支 → 全屏直接拿本帧 raymarch。
                int cell = view.temporalSequence[view.frameNumber % totalCells];
                mat.SetVector("_SampleCell", new Vector2(cell % upX, cell / upX));
                mat.SetVector("_Upscale", new Vector2(upX, upY));
                mat.SetFloat("_UseTemporal", 1f);
                view.frameNumber++;
            }
            else
            {
                mat.SetVector("_SampleCell", Vector2.zero);
                mat.SetVector("_Upscale", new Vector2(upX, upY));
                mat.SetFloat("_UseTemporal", 0f);
            }
            mat.SetVector("_LowResSize", new Vector2(
                view.cloudTex != null ? view.cloudTex.width : 1f,
                view.cloudTex != null ? view.cloudTex.height : 1f));

            // Update reprojection matrix for next frame
            // 2026-08-25 割裂线根因修复:重投影必须用【GPU 投影】(GL.GetGPUProjectionMatrix)而非逻辑
            // cam.projectionMatrix。Clouds 顶点着色器用 v.vertex(光栅化 clip)重建射线,D3D 下 GPU
            // clip 与逻辑投影 Y 约定相反;此前用逻辑投影 → reprojUV 与 i.uv 垂直镜像 → 历史采错行
            // → 云带边缘镜像鬼影 = 割裂线(运动残影开时可见,时序开时闪烁)。改用 GPU 投影后二者一致。
            view.prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
        }

        /// <summary>
        /// 构造"绕行星中心 C 的 Y 轴旋转 +dPhi"的仿射矩阵(云空间重投影用,方案 C §5 近似版)。
        /// 约定与 shader SampleDensity 的旋转一致(R_y(+θ): x'=x·cos−z·sin, z'=x·sin+z·cos):
        /// 把当前帧的云面世界位置按云的转动平移回上一帧的云位置,再由 prevViewProjMat 投影。
        /// 等价于 invert(prevWorldToCloud) * worldToCloud(本帧)(文档 §5 公式)。
        /// </summary>
        private static Matrix4x4 BuildCloudSpaceRepro(float dPhi, Vector3 center)
        {
            float ca = Mathf.Cos(dPhi);
            float sa = Mathf.Sin(dPhi);
            var R = new Matrix4x4();
            R.m00 = ca; R.m02 = -sa;
            R.m11 = 1f;
            R.m20 = sa; R.m22 = ca;
            R.m33 = 1f;
            // 平移项 t = C − R·C(使变换为绕 C 旋转: R·(P−C)+C )
            Vector3 rC = new Vector3(
                R.m00 * center.x + R.m02 * center.z,
                center.y,
                R.m20 * center.x + R.m22 * center.z);
            R.m03 = center.x - rC.x;
            R.m13 = center.y - rC.y;
            R.m23 = center.z - rC.z;
            return R;
        }

        private static float GetWindSpeedFactor(float directionDeg)
        {
            float angle = directionDeg % 360f;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;

            float absAngle = Mathf.Abs(angle);
            if (absAngle < 45f || absAngle > 135f) return 2.0f;
            if (absAngle > 60f && absAngle < 120f) return 1.0f;
            float t = Mathf.InverseLerp(45f, 60f, absAngle);
            return Mathf.Lerp(2.0f, 1.0f, t);
        }

        /// <summary>
        /// 相机海拔 = |camPos − 行星中心| − 行星半径(米)。任何异常都回退 0(低空 → 纯体积云)。
        /// </summary>
        private float ComputeCameraAltitude()
        {
            try
            {
                var craftNode = Game.Instance.FlightScene.CraftNode;
                if (craftNode == null || craftNode.ReferenceFrame == null || craftNode.Parent == null)
                    return 0f;
                Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                float surfaceRadius = (float)craftNode.Parent.PlanetData.Radius;
                return (cam.transform.position - planetCenter).magnitude - surfaceRadius;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 海拔淡入因子:0 = 纯体积云,1 = 纯 2D 轨道云。
        /// useOrbitClouds 关闭 → 恒 0(完全保持现状,零回归)。
        /// smoothstep 保证过渡带两端导数 0,淡入不突兀。
        /// </summary>
        private static float ComputeOrbitFade(CloudConfig cfg, float camAlt)
        {
            if (cfg == null || !cfg.useOrbitClouds) return 0f;
            float start = Mathf.Max(0f, cfg.orbitTransitionStartAltitude);
            float end = Mathf.Max(start + 1f, cfg.orbitTransitionEndAltitude);
            float t = Mathf.Clamp01(Mathf.InverseLerp(start, end, camAlt));
            return t * t * (3f - 2f * t); // smoothstep
        }

        private float _lastOrbitDiagLogTime = -999f;
        private Vector3 _lastDiagCamPos;
        private float _lastDiagCamTime = -999f;

        /// <summary>
        /// 轨道云诊断日志(2s 节流):打印运行态参数(相机海拔/速度/每层淡入/体积云是否运行/RT 尺寸/漂移状态/真实配置),
        /// 用于定位 2D 与体积云"范围差一圈"或"速度不同"的根因。
        /// </summary>
        private void LogOrbitDiagnostics(List<CloudLayerView> activeViews, float camAlt, int orbitPass)
        {
            bool anyOrbit = false;
            foreach (var view in activeViews)
                if (view.layer.config != null && view.layer.config.useOrbitClouds) { anyOrbit = true; break; }
            if (!anyOrbit) return;

            if (Time.realtimeSinceStartup - _lastOrbitDiagLogTime < 2f) return;
            _lastOrbitDiagLogTime = Time.realtimeSinceStartup;

            // 相机速度(2s 窗口平均,米/秒)—— 排查"速度"差异
            float camSpeed = 0f;
            if (_lastDiagCamTime > 0f)
            {
                float dt = Mathf.Max(1e-3f, Time.realtimeSinceStartup - _lastDiagCamTime);
                camSpeed = Vector3.Distance(cam.transform.position, _lastDiagCamPos) / dt;
            }
            _lastDiagCamPos = cam.transform.position;
            _lastDiagCamTime = Time.realtimeSinceStartup;

            // 角速度对比(游戏本体 2D 云随行星自转:WorldToCloud = WorldToPlanet × YPR(angularSpeed)):
            // 行星自转角速度(rad/s,IPlanetData.AngularVelocity)vs 云场旋转率(globalRotationAngular×5e-4)。
            // 若两者不一致 → 云相对地表漂移(角速度症状);两者都应在体积云/2D 间同步(共用 currentRotation)。
            double planetSpin = 0.0;
            try
            {
                planetSpin = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AngularVelocity;
            }
            catch { }

            var sb = new System.Text.StringBuilder();
            sb.Append("Volken:OrbitDiag camAlt=").Append(camAlt.ToString("F0")).Append("m speed=").Append(camSpeed.ToString("F1")).Append("m/s orbitPass=").Append(orbitPass >= 0 ? "yes" : "missing");
            sb.Append(" planetSpin=").Append(planetSpin.ToString("G4")).Append("rad/s");
            if (lowResDepthTex != null)
                sb.Append(" lowDepth=").Append(lowResDepthTex.width).Append('x').Append(lowResDepthTex.height);
            LogProjectionCheck(sb);
            foreach (var view in activeViews)
            {
                var c = view.layer.config;
                sb.Append(" | L").Append(view.layer.layerIndex)
                  .Append(" fade=").Append(view.orbitFade.ToString("F3"))
                  .Append(" volRun=").Append(view.orbitFade < 0.999f ? 1 : 0)
                  .Append(" orbitRT=").Append(view.orbitCloudTex != null ? view.orbitCloudTex.width + "x" + view.orbitCloudTex.height : "null");
                if (c == null) continue;
                // 漂移/自转状态(2D 与体积云共用同一材质 uniform,验证"速度"是否一致)
                sb.Append(" rot=").Append(view.layer.accumulatedRotation.ToString("F3"))
                  .Append(" off=").Append(view.layer.runningOffset.ToString("F2"))
                  .Append(" wind=").Append(c.windSpeed.ToString("F2")).Append('@').Append(c.windDirection.ToString("F0"))
                  .Append(" globRot=").Append(c.globalRotationAngular.ToString("F2"))
                  .Append(" rotRate=").Append((c.globalRotationAngular * 5e-4f).ToString("G4")).Append("rad/s")
                  .Append(" tss=").Append(c.useTemporalUpscale ? 1 : 0)
                  .Append(" histBlend=").Append(c.historyBlend.ToString("F2"))
                  .Append(" resScale=").Append(c.resolutionScale.ToString("F2"))
                  .Append(" h=").Append(c.layerHeights)
                  .Append(" sp=").Append(c.layerSpreads)
                  .Append(" w=").Append(c.layerStrengths)
                  .Append(" dens=").Append(c.density.ToString("F4"))
                  .Append(" abs=").Append(c.absorption.ToString("F4"))
                  .Append(" cov=").Append(c.coverage.ToString("F3"))
                  .Append(" maxH=").Append(c.maxCloudHeight.ToString("F0"))
                  .Append(" step=").Append(c.stepSize.ToString("F0"))
                  .Append(" comp=").Append(c.compositeMode)
                  .Append(" stock=").Append(c.useStockCloudMap ? 1 : 0)
                  .Append(" stockDensityScale=").Append(c.stockDensityScale.ToString("F2"))
                  .Append(" fadeBand=").Append(c.orbitTransitionStartAltitude.ToString("F0")).Append('-').Append(c.orbitTransitionEndAltitude.ToString("F0"))
                  .Append(" boost=").Append(c.orbitDensityBoost.ToString("F2"))
                  .Append(" bright=").Append(c.orbitBrightness.ToString("F2"));
            }
            Mod.Log(sb.ToString());
        }

        private bool _projChecked;

        /// <summary>
        /// 投影一致性校验(每会话一次):CPU 端分别用"显式相机轴"和"相机投影矩阵"重建 viewDir,
        /// 在 9×9 NDC 网格上量最大角差 —— 直接判断两条渲染路径是否会产生随相机旋转而变的错位。
        /// </summary>
        private void LogProjectionCheck(System.Text.StringBuilder sb)
        {
            if (cam == null) return;
            sb.Append(" fov=").Append(cam.fieldOfView.ToString("F2"))
              .Append(" aspect=").Append(cam.aspect.ToString("F4"))
              .Append(" ortho=").Append(cam.orthographic ? 1 : 0);
            if (_projChecked) return;
            _projChecked = true;
            try
            {
                float tanFovV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float aspect = cam.aspect;
                Matrix4x4 invProj = cam.projectionMatrix.inverse;
                Matrix4x4 camToWorld = cam.cameraToWorldMatrix;
                Vector3 fwd = cam.transform.forward, right = cam.transform.right, up = cam.transform.up;
                float maxDeg = 0f;
                for (int gy = 0; gy <= 8; gy++)
                for (int gx = 0; gx <= 8; gx++)
                {
                    float ndcX = gx / 4f - 1f, ndcY = gy / 4f - 1f;
                    Vector3 vExplicit = (fwd + right * (ndcX * tanFovV * aspect) - up * (ndcY * tanFovV)).normalized;
                    // 与 2D 旧 shader 的重建一致:clip=(ndc,0,-1) → invProj → CameraToWorld
                    Vector4 unproj = invProj * new Vector4(ndcX, ndcY, 0f, -1f);
                    Vector3 vMatrix = camToWorld.MultiplyVector(new Vector3(unproj.x, unproj.y, unproj.z)).normalized;
                    float deg = Mathf.Rad2Deg * Mathf.Acos(Mathf.Clamp(Vector3.Dot(vExplicit, vMatrix), -1f, 1f));
                    if (deg > maxDeg) maxDeg = deg;
                }
                Mod.Log("Volken:ProjCheck maxViewDirDelta=" + maxDeg.ToString("F4") + "deg (显式轴 vs 逆投影矩阵;≈0 = 两路径一致)");
            }
            catch (Exception e) { Mod.Log("Volken:ProjCheck ERROR " + e.Message); }
        }

        private float _lastCoverageLogTime = -999f;

        /// <summary>
        /// 覆盖探针(2s 节流,任一层的 orbitDebugMode>0 时启用):
        /// GPU 回读体积云 cloudTex 与 2D orbitCloudTex,量化两者的屏幕覆盖比例 + 相对行星边缘的"圈宽"像素 ——
        /// 直接给出"范围差"数字,替代目测。
        /// 几何:行星剪影是圆,屏幕半径 = focal·tan(asin(R/d)),圆心 = 行星中心屏幕投影(与相机朝向无关);
        /// R03/R15 = 亮度阈值 0.03/0.15 的云最远像素半径;ring = maxR − limb(>0 表示云伸出行星边缘的像素)。
        /// </summary>
        private void LogCloudCoverage(List<CloudLayerView> activeViews, float camAlt, Vector3 planetCenter, float surfaceRadius)
        {
            // 调试探针(GPU 回读):仅 debug 模式(ModSettings.DevMode)下启用
            try { if (ModSettings.Instance == null || !ModSettings.Instance.DevMode) return; }
            catch { return; }

            bool anyDebug = false;
            foreach (var view in activeViews)
                if (view.layer.config != null && view.layer.config.orbitDebugMode > 0.5f) { anyDebug = true; break; }
            if (!anyDebug) return;
            if (cam == null) return;

            if (Time.realtimeSinceStartup - _lastCoverageLogTime < 2f) return;
            _lastCoverageLogTime = Time.realtimeSinceStartup;

            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float focalPx = (cam.pixelHeight * 0.5f) / Mathf.Tan(Mathf.Max(halfFov, 1e-4f));
            float dist = Mathf.Max(camAlt + surfaceRadius, surfaceRadius * 1.001f);
            float limbPx = focalPx * Mathf.Tan(Mathf.Asin(Mathf.Clamp(surfaceRadius / dist, 0f, 0.999f)));
            Vector2 planetScreen = cam.WorldToScreenPoint(planetCenter);   // 左下原点,像素

            foreach (var view in activeViews)
            {
                var layer = view.layer;
                if (layer.config == null || !layer.config.useOrbitClouds) continue;
                if (view.orbitCloudTex == null) continue;
                var sb = new System.Text.StringBuilder();
                sb.Append("Volken:Coverage L").Append(layer.layerIndex)
                  .Append(" alt=").Append(camAlt.ToString("F0"))
                  .Append(" fade=").Append(view.orbitFade.ToString("F3"))
                  .Append(" pCtr=").Append(planetScreen.x.ToString("F0")).Append(',').Append(planetScreen.y.ToString("F0"))
                  .Append(" limb=").Append(limbPx.ToString("F0")).Append("px");
                // 2D 轨道云(仅本帧确实渲染过:淡入>0)
                if (view.orbitFade > 0.001f)
                    MeasureCloudRT(view.orbitCloudTex, planetScreen, limbPx, "2d", sb);
                else
                    sb.Append(" 2d=skip");
                // 体积云(仅本帧确实渲染过:淡入<0.999;否则 cloudTex 是陈旧帧)
                if (view.cloudTex != null && view.orbitFade < 0.999f)
                    MeasureCloudRT(view.cloudTex, planetScreen, limbPx, "vol", sb);
                else
                    sb.Append(" vol=skip");
                Mod.Log(sb.ToString());
            }
        }

        private static void MeasureCloudRT(RenderTexture rt, Vector2 planetScreenPx, float limbPx, string tag, System.Text.StringBuilder sb)
        {
            var prev = RenderTexture.active;
            var tmp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tmp.Apply();
            RenderTexture.active = prev;

            var px = tmp.GetPixels32();
            int total = px.Length;
            float cx = planetScreenPx.x * rt.width / (float)Screen.width;
            float cy = planetScreenPx.y * rt.height / (float)Screen.height;
            float limbRt = limbPx * rt.width / (float)Screen.width;
            int count03 = 0, count15 = 0;
            float maxR03 = 0f, maxR15 = 0f;
            for (int i = 0; i < total; i++)
            {
                var c = px[i];
                float lum = (c.r + c.g + c.b) / 765f;
                if (lum <= 0.03f) continue;
                int x = i % rt.width, y = i / rt.width;
                float dx = x - cx, dy = y - cy;
                float rad = Mathf.Sqrt(dx * dx + dy * dy);
                count03++;
                if (rad > maxR03) maxR03 = rad;
                if (lum > 0.15f)
                {
                    count15++;
                    if (rad > maxR15) maxR15 = rad;
                }
            }
            if (Application.isPlaying) UnityEngine.Object.DestroyImmediate(tmp);
            else UnityEngine.Object.Destroy(tmp);

            sb.Append(' ').Append(tag).Append("=").Append((count03 * 100f / total).ToString("F1")).Append('%')
              .Append(" R03=").Append(maxR03.ToString("F0")).Append("px")
              .Append(" R15=").Append(maxR15.ToString("F0")).Append("px")
              .Append(" ring03=").Append((maxR03 - limbRt).ToString("F1")).Append("px")
              .Append(" ring15=").Append((maxR15 - limbRt).ToString("F1")).Append("px");
        }

        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            try
            {
                // 0. Validate
                TryResolveFarDepthSource();
                RenderTexture farDepthTex = farDepthSource != null ? farDepthSource.farDepthTex : null;
                if (farDepthTex == null)
                {
                    Graphics.Blit(source, destination);
                    return;
                }

                var activeLayers = Volken.Core.VolkenMod.Instance.ActiveLayers.ToList();
                if (activeLayers.Count == 0)
                {
                    Graphics.Blit(source, destination);
                    return;
                }

                BuildViews();
                int renderW = Mathf.Max(1, source.width);
                int renderH = Mathf.Max(1, source.height);

                // 1. Check RTs for all active layers (create on first frame or config/size change.
                //    分辨率 / TSS 开关 / 格网变化都会改变 cloudRes 与历史尺寸 → 重建;按【本相机】输出尺寸)
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    bool tss = layer.config.useTemporalUpscale;
                    int upX = Mathf.Max(1, layer.config.upscaleX);
                    int upY = Mathf.Max(1, layer.config.upscaleY);
                    float orbitRes = Mathf.Clamp(layer.config.orbitResolutionScale, 0.1f, 1f);
                    bool needsCreate = !view.IsCreated ||
                        view.currentW != renderW || view.currentH != renderH ||
                        Mathf.Abs(view.currentResolutionScale - layer.config.resolutionScale) > 0.001f ||
                        Mathf.Abs(view.currentOrbitRes - orbitRes) > 0.001f ||
                        view.currentTemporal != (tss ? 1 : 0) ||
                        view.currentUpX != upX || view.currentUpY != upY;
                    if (needsCreate)
                    {
                        view.ReleaseRenderTextures();
                        view.currentResolutionScale = layer.config.resolutionScale;
                        view.CreateRenderTextures(renderW, renderH);
                    }
                }

                // Find the max low-res size needed across all layers (for shared lowResDepthTex)
                int maxLowW = 1, maxLowH = 1;
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view != null && view.cloudTex != null)
                    {
                        maxLowW = Mathf.Max(maxLowW, view.cloudTex.width);
                        maxLowH = Mathf.Max(maxLowH, view.cloudTex.height);
                    }
                }
                EnsureDepthTextures(renderW, renderH);
                EnsureLowResDepthTex(new Vector2Int(maxLowW, maxLowH));

                // 2. Depth processing (once per camera)
                var matRef = activeLayers[0].material; // any layer's material works for depth passes
                int nearDepthPass = matRef.FindPass("NearDepth");
                int downsamplePass = matRef.FindPass("DownsampleDepth");
                Graphics.Blit(farDepthTex, combinedDepthTex, matRef, nearDepthPass);
                Graphics.Blit(combinedDepthTex, lowResDepthTex, matRef, downsamplePass);

                // 2.5 轨道云:相机海拔 → 每层淡入因子(0=纯体积云,1=纯 2D),按本相机海拔分派
                int orbitPass = matRef.FindPass("OrbitClouds");
                float camAlt = ComputeCameraAltitude();
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    view.orbitFade = ComputeOrbitFade(layer.config, camAlt);
                    bool orbitOnly = view.orbitFade >= 0.999f;
                    // 进入纯 2D 的瞬间清时序历史,防止切回体积云时旧历史残影(冷启动路径已在 Upscale 内)
                    if (orbitOnly && !view.orbitOnlyLastFrame)
                        view.ClearHistory();
                    view.orbitOnlyLastFrame = orbitOnly;
                }
                var activeViews = activeLayers.Select(l => GetView(l)).Where(v => v != null).ToList();
                LogOrbitDiagnostics(activeViews, camAlt, orbitPass);

                // 全球风/自转推进:每帧一次(所有相机共享,避免 N 台相机时云速 N 倍)
                AdvanceGlobalCloudState();

                // 3. Render each layer (independent raymarch, MRT: color + cloud depth)
                int cloudsPass = matRef.FindPass("Clouds");
                // 阶段二: 全屏三角形(一次构建,复用)
                if (_fullscreenTriangle == null)
                {
                    _fullscreenTriangle = new Mesh();
                    _fullscreenTriangle.vertices = new Vector3[] {
                        new Vector3(-1f, -1f, 0f),
                        new Vector3( 3f, -1f, 0f),
                        new Vector3(-1f,  3f, 0f),
                    };
                    _fullscreenTriangle.uv = new Vector2[] {
                        new Vector2(0f, 0f),
                        new Vector2(2f, 0f),
                        new Vector2(0f, 2f),
                    };
                    _fullscreenTriangle.triangles = new int[] { 0, 1, 2 };
                    _fullscreenTriangle.UploadMeshData(true);
                }

                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    // 相机相关动态属性(reprojMat/_CamPos/_SampleCell 等,每相机每层各自设置;
                    // 风/自转已由 AdvanceGlobalCloudState 每帧推进一次)
                    SetLayerDynamicProperties(layer, view, renderW, renderH);
                    // 高空纯 2D:跳过体积 raymarch(本特性的性能大头;Upscale/历史也一并跳过)
                    if (view.orbitFade >= 0.999f) continue;

                    layer.material.SetTexture("DepthTex", lowResDepthTex);   // Clouds pass 地面遮挡用
                    // MRT: cloudTex(RGBA) + cloudDepthTex(RFloat) + cloudMVTex(RG 本帧运动矢量)
                    var mrt = new RenderBuffer[] { view.cloudTex.colorBuffer, view.cloudDepthTex.colorBuffer, view.cloudMVTex.colorBuffer };
                    Graphics.SetRenderTarget(mrt, view.cloudTex.depthBuffer);
                    layer.material.SetPass(cloudsPass);
                    Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                    // 运动矢量膨胀:cloudMVTex → tmp1 → tmp2 → cloudMVDilatedTex(3 次 3×3)。
                    // KSA 结构下这是【本帧】膨胀,供同帧 Upscale 使用(消除 1 帧滞后)。
                    int dilatePass = layer.material.FindPass("DilateMV");
                    if (dilatePass >= 0)
                    {
                        var mvTmp1 = RenderTexture.GetTemporary(view.cloudMVTex.width, view.cloudMVTex.height, 0, view.cloudMVTex.format);
                        var mvTmp2 = RenderTexture.GetTemporary(view.cloudMVTex.width, view.cloudMVTex.height, 0, view.cloudMVTex.format);
                        Graphics.Blit(view.cloudMVTex, mvTmp1, layer.material, dilatePass);
                        Graphics.Blit(mvTmp1, mvTmp2, layer.material, dilatePass);
                        Graphics.Blit(mvTmp2, view.cloudMVDilatedTex, layer.material, dilatePass);
                        RenderTexture.ReleaseTemporary(mvTmp1);
                        RenderTexture.ReleaseTemporary(mvTmp2);
                    }
                }

                // 3.5 轨道云(2D 壳着色):每层渲染到 view.orbitCloudTex(仅当淡入因子>0;pass 缺失时优雅降级为纯体积云)
                if (orbitPass >= 0)
                {
                    foreach (var layer in activeLayers)
                    {
                        var view = GetView(layer);
                        if (view == null) continue;
                        if (view.orbitFade <= 0.001f) continue;
                        // Blit 的 source 不被 OrbitClouds pass 采样(纯壳着色),仅作为合法非空输入
                        Graphics.Blit(lowResDepthTex, view.orbitCloudTex, layer.material, orbitPass);
                    }
                }

                // 4. Upscale each layer (KSA 时序核心,走 Graphics.Blit 单目标输出,_MainTex 自动绑 cloudTex。
                //    不用 MRT+DrawMeshNow:此前双 MRT(0 深度)在该路径上不渲染 → upscaled 恒黑 → 看不到云)
                int upscalePass = matRef.FindPass("Upscale");
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    if (view.orbitFade >= 0.999f) continue;   // 高空纯 2D:体积时序链路整条跳过

                    var mat = layer.material;
                    mat.SetTexture("CloudDepthTex", view.cloudDepthTex);
                    mat.SetTexture("CloudMVDilatedTex", view.cloudMVDilatedTex);   // 本帧膨胀 MV
                    mat.SetTexture("CombinedDepthTex", combinedDepthTex);
                    mat.SetTexture("HistoryTex", view.historyTex);
                    mat.SetTexture("HistoryDepthTex", view.historyDepthTex);
                    mat.SetTexture("HistoryCloudDepthTex", view.historyCloudDepthTex);
                    Graphics.Blit(view.cloudTex, view.upscaledCloudTex, mat, upscalePass);

                    // 时序写回:全清上采样结果 → 历史(下一帧在 Upscale 里按 MV 重投影采样);
                    // 云面距离历史 = 本帧低清 cloudDepth 上采样到全清(供下一帧 cloudGate 校验)
                    Graphics.Blit(view.upscaledCloudTex, view.historyTex);
                    Graphics.Blit(combinedDepthTex, view.historyDepthTex);
                    Graphics.Blit(view.cloudDepthTex, view.historyCloudDepthTex);
                }

                // 5. Chain-composite: iterate layers, applying composite mode
                int compositePass = matRef.FindPass("Composite");
                RenderTexture result = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                Graphics.Blit(source, result);

                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    matRef.SetTexture("UpscaledCloudTex", view.upscaledCloudTex);
                    matRef.SetTexture("OrbitCloudTex", view.orbitCloudTex);
                    matRef.SetTexture("SceneDepthTex", combinedDepthTex);
                    matRef.SetFloat("_CompositeMode",
                        layer.config.compositeMode == CompositeMode.Standard ? 1.0f : 0.0f);
                    // 交叉淡入因子:orbit pass 不可用 → 强制 0(纯体积云,等同未开启本特性)
                    matRef.SetFloat("_OrbitFade", orbitPass >= 0 ? view.orbitFade : 0f);

                    var temp = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                    Graphics.Blit(result, temp, matRef, compositePass);
                    RenderTexture.ReleaseTemporary(result);
                    result = temp;
                }

                // 5.5 覆盖探针(仅 debug 模式 DevMode + orbitDebugMode>0 时):量 2D/体积云的屏幕范围差
                try
                {
                    var probeCraft = Game.Instance.FlightScene.CraftNode;
                    if (probeCraft != null && probeCraft.ReferenceFrame != null && probeCraft.Parent != null)
                    {
                        Vector3 probeCenter = probeCraft.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                        float probeRadius = (float)probeCraft.Parent.PlanetData.Radius;
                        LogCloudCoverage(activeViews, camAlt, probeCenter, probeRadius);
                    }
                }
                catch { }

                Graphics.Blit(result, destination);
                RenderTexture.ReleaseTemporary(result);
            }
            catch (Exception e)
            {
                Mod.Log("Volken:CloudRenderer.OnRenderImage ERROR: " + e);
                try { Graphics.Blit(source, destination); } catch { }
            }
        }

        private void OnDestroy()
        {
            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered -= OnReferenceFrameRecentered;
            }
            catch { }
            ReleaseAllRenderTextures();
        }

    }

}
