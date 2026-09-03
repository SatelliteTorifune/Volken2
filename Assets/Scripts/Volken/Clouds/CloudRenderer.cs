using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;

/*
    Volken Pipeline Overview (Multi-Layer):

    1. Write depth from far camera to a render texture
    2. Write depth from near camera to the same texture
    3. Downsample the combined depth texture for later use in depth aware upscaling
    4. For each active layer:
       a. Set dynamic shader properties (wind, rotation, reprojection matrix)
       b. Render volumetrics to layer.cloudTex (optionally blend with history buffer)
       c. Copy output to history buffer
    5. For each active layer: Upscale layer.cloudTex to full resolution
    6. Chain-composite all layers onto the scene:
       - Additive mode: result.rgb += cloudColor (zero visual interference)
       - Standard mode: result = src * transmittance + cloudColor (physical occlusion)
*/

public class CloudRenderer : MonoBehaviour
{
    // === Shared depth render targets (one set for all layers) ===
    private RenderTexture combinedDepthTex;
    private RenderTexture lowResDepthTex;
    private Camera cam;
    private static Mesh _fullscreenTriangle; // 阶段二 MRT 全屏三角形

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
            Mod.LOG("Volken:CloudRenderer cannot subscribe ReferenceFrameRecentered: " + ex.Message);
        }
    }

    private void OnPlayerChangedSoi(ICraftNode playerCraftNode, IPlanetNode newParent)
    {
        if (playerCraftNode.Parent.Parent == null)
        {
            // Sun has no clouds
            foreach (var layer in Volken.Instance.layers)
            {
                if (layer?.config != null)
                {
                    layer.config.enabled = false;
                    layer.frameNumber = 0;   // 方案 C:切换天体 → 历史失效 → 冷启动全步进
                    layer.prevCloudAngle = float.NaN; // 云转角相位作废,首帧回退世界空间重投影
                }
            }
        }
        else
        {
            bool hasAtmo = newParent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            foreach (var layer in Volken.Instance.layers)
            {
                if (layer?.config != null)
                {
                    layer.config.enabled = hasAtmo && layer.config.enabled;
                    layer.frameNumber = 0;   // 方案 C:切换天体 → 历史失效 → 冷启动全步进
                    layer.prevCloudAngle = float.NaN; // 云转角相位作废,首帧回退世界空间重投影
                }
            }
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
            foreach (var layer in Volken.Instance.layers)
            {
                if (layer == null) continue;
                layer.frameNumber = 0;
                ClearTemporalHistory(layer);
            }
            Mod.LOG("Volken:CloudRenderer frame recentered Δ=" + positionDelta.magnitude.ToString("F1") + "m — TSS history cleared");
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken:CloudRenderer OnReferenceFrameRecentered ERROR: " + ex.Message);
        }
    }

    /// <summary>清空某层时序历史(颜色/场景深度/云面距离),使 Upscale 的 validHist 全 0 → 全走本帧。</summary>
    private void ClearTemporalHistory(CloudLayer layer)
    {
        var prevActive = RenderTexture.active;
        var rt = layer.historyTex;
        if (rt != null && rt.IsCreated()) { RenderTexture.active = rt; GL.Clear(true, true, Color.clear); }
        rt = layer.historyDepthTex;
        if (rt != null && rt.IsCreated()) { RenderTexture.active = rt; GL.Clear(true, true, Color.clear); }
        rt = layer.historyCloudDepthTex;
        if (rt != null && rt.IsCreated()) { RenderTexture.active = rt; GL.Clear(true, true, Color.clear); }
        RenderTexture.active = prevActive;
    }

    public void CloudRenderManualRefresh()
    {
        CreateSharedRenderTextures();
        SetAllLayersShaderProperties();
        // Ensure each layer has its RTs created
        foreach (var layer in Volken.Instance.layers)
        {
            if (layer != null && layer.config != null)
            {
                layer.currentResolutionScale = layer.config.resolutionScale;
            }
        }
    }

    private void CreateSharedRenderTextures()
    {
        var res = Screen.currentResolution;

        if (combinedDepthTex != null && combinedDepthTex.IsCreated())
            combinedDepthTex.Release();
        combinedDepthTex = new RenderTexture(res.width, res.height, 0, RenderTextureFormat.RFloat);
        combinedDepthTex.Create();

        // Low-res depth RT will be recreated when needed (depends on layer resolution)
        // We create a default one here
        if (lowResDepthTex != null && lowResDepthTex.IsCreated())
            lowResDepthTex.Release();
        Vector2Int lowRes = Vector2Int.RoundToInt(0.5f * new Vector2(res.width, res.height));
        lowResDepthTex = new RenderTexture(Mathf.Max(1, lowRes.x), Mathf.Max(1, lowRes.y), 0, RenderTextureFormat.RFloat);
        lowResDepthTex.Create();
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

        foreach (var layer in Volken.Instance.layers)
        {
            layer?.ReleaseRenderTextures();
        }
    }

    /// <summary>
    /// Called by Volken.ValueChanged() when any layer's config changes.
    /// Re-applies static shader properties for all layers.
    /// </summary>
    public void SetAllLayersShaderProperties()
    {
        try
        {
            foreach (var layer in Volken.Instance.layers)
            {
                if (layer?.config == null || layer.material == null) continue;

                layer.SetStaticShaderProperties();
            }
        }
        catch (Exception)
        {
            Mod.LOG("Volken:CloudRenderer.SetAllLayersShaderProperties" + Environment.StackTrace);
        }
    }

    /// <summary>
    /// Sets per-frame dynamic properties for a specific layer.
    /// </summary>
    public void SetLayerDynamicProperties(CloudLayer layer)
    {
        if (layer?.config == null || layer.material == null) return;

        var craftNode = Game.Instance.FlightScene.CraftNode;
        Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
        var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;
        float deltaTime = (float)Game.Instance.FlightScene.TimeManager.DeltaTime;

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

        var mat = layer.material;
        mat.SetFloat("currentRotation", layer.accumulatedRotation);
        mat.SetFloat("maxDepth", 0.9f * FarCameraScript.maxFarDepth);
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
        float dPhi = float.IsNaN(layer.prevCloudAngle) ? 0.0f : cloudPhi - layer.prevCloudAngle;
        mat.SetMatrix("reprojMat", layer.prevViewProjMat * BuildCloudSpaceRepro(dPhi, planetCenter));
        layer.prevCloudAngle = cloudPhi;
        // 阶段二:观察射线用相机 transform 轴直接构造(NDC 来自 clip 坐标,无投影矩阵约定歧义)。
        // 注意:不要用 cameraToWorldMatrix 的第2列当 fwd——Unity 视图约定里那是 -forward,会反向。
        mat.SetVector("_CamPos", cam.transform.position);
        mat.SetFloat("_ReflectionMode", 0f);
        mat.SetVector("_CamFwd", cam.transform.forward);
        mat.SetVector("_CamRight", cam.transform.right);
        mat.SetVector("_CamUp", cam.transform.up);
        mat.SetFloat("_TanHalfFovV", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        mat.SetFloat("_Aspect", cam.aspect);
        mat.SetVector("clipPlanes", new Vector2(cam.nearClipPlane, cam.farClipPlane));

        mat.SetFloat("_NearThreshold", layer.config.nearThreshold);

        // Per-layer resolution-aware blue noise scale
        mat.SetVector("blueNoiseScale",
            layer.currentResolutionScale * new Vector2(Screen.width, Screen.height) / 512.0f);

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
            if (layer.temporalSequence == null || layer.temporalSequence.Length != totalCells)
            {
                layer.temporalSequence = UpscalingPixelSequence.FindOptimalSamplingSequence(upX, upY);
                layer.frameNumber = 0;   // 格网变化 → 历史相位作废 → 冷启动
            }
            // 冷启动无需特判全步进:历史为空时 Upscale 走"本帧有效"分支 → 全屏直接拿本帧 raymarch。
            int cell = layer.temporalSequence[layer.frameNumber % totalCells];
            mat.SetVector("_SampleCell", new Vector2(cell % upX, cell / upX));
            mat.SetVector("_Upscale", new Vector2(upX, upY));
            mat.SetFloat("_UseTemporal", 1f);
            layer.frameNumber++;
        }
        else
        {
            mat.SetVector("_SampleCell", Vector2.zero);
            mat.SetVector("_Upscale", new Vector2(upX, upY));
            mat.SetFloat("_UseTemporal", 0f);
        }
        mat.SetVector("_LowResSize", new Vector2(
            layer.cloudTex != null ? layer.cloudTex.width : 1f,
            layer.cloudTex != null ? layer.cloudTex.height : 1f));

        // Update reprojection matrix for next frame
        // 2026-08-25 割裂线根因修复:重投影必须用【GPU 投影】(GL.GetGPUProjectionMatrix)而非逻辑
        // cam.projectionMatrix。Clouds 顶点着色器用 v.vertex(光栅化 clip)重建射线,D3D 下 GPU
        // clip 与逻辑投影 Y 约定相反;此前用逻辑投影 → reprojUV 与 i.uv 垂直镜像 → 历史采错行
        // → 云带边缘镜像鬼影 = 割裂线(运动残影开时可见,时序开时闪烁)。改用 GPU 投影后二者一致。
        layer.prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
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

    [ImageEffectOpaque]
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        try
        {
            // 0. Validate
            if (FarCameraScript.farDepthTex == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            var activeLayers = Volken.Instance.ActiveLayers.ToList();
            if (activeLayers.Count == 0)
            {
                Graphics.Blit(source, destination);
                return;
            }

            // 1. Check RTs for all active layers (create on first frame or config change:
            //    分辨率 / TSS 开关 / 格网变化都会改变 cloudRes 与历史尺寸 → 重建)
            foreach (var layer in activeLayers)
            {
                bool tss = layer.config.useTemporalUpscale;
                int upX = Mathf.Max(1, layer.config.upscaleX);
                int upY = Mathf.Max(1, layer.config.upscaleY);
                float orbitRes = Mathf.Clamp(layer.config.orbitResolutionScale, 0.1f, 1f);
                bool needsCreate = layer.cloudTex == null || !layer.cloudTex.IsCreated() ||
                    Mathf.Abs(layer.currentResolutionScale - layer.config.resolutionScale) > 0.001f ||
                    Mathf.Abs(layer.currentOrbitRes - orbitRes) > 0.001f ||
                    layer.currentTemporal != (tss ? 1 : 0) ||
                    layer.currentUpX != upX || layer.currentUpY != upY;
                if (needsCreate)
                {
                    layer.ReleaseRenderTextures();
                    layer.currentResolutionScale = layer.config.resolutionScale;
                    layer.CreateRenderTextures(Screen.width, Screen.height);
                }
            }

            // Find the max low-res size needed across all layers (for shared lowResDepthTex)
            int maxLowW = 1, maxLowH = 1;
            foreach (var layer in activeLayers)
            {
                if (layer.cloudTex != null)
                {
                    maxLowW = Mathf.Max(maxLowW, layer.cloudTex.width);
                    maxLowH = Mathf.Max(maxLowH, layer.cloudTex.height);
                }
            }
            EnsureLowResDepthTex(new Vector2Int(maxLowW, maxLowH));

            // 2. Depth processing (shared, once)
            var matRef = activeLayers[0].material; // any layer's material works for depth passes
            int nearDepthPass = matRef.FindPass("NearDepth");
            int downsamplePass = matRef.FindPass("DownsampleDepth");
            Graphics.Blit(FarCameraScript.farDepthTex, combinedDepthTex, matRef, nearDepthPass);
            Graphics.Blit(combinedDepthTex, lowResDepthTex, matRef, downsamplePass);

            // 2.5 轨道云:相机海拔 → 每层淡入因子(0=纯体积云,1=纯 2D)。
            //     海拔分派与 KSA 一致:camAlt < start → 仅体积;start~end → 两者+交叉淡入;> end → 仅 2D。
            int orbitPass = matRef.FindPass("OrbitClouds");
            float camAlt = ComputeCameraAltitude();
            foreach (var layer in activeLayers)
            {
                layer.orbitFade = ComputeOrbitFade(layer.config, camAlt);
                bool orbitOnly = layer.orbitFade >= 0.999f;
                // 进入纯 2D 的瞬间清时序历史,防止切回体积云时旧历史残影(冷启动路径已在 Upscale 内)
                if (orbitOnly && !layer.orbitOnlyLastFrame)
                    ClearTemporalHistory(layer);
                layer.orbitOnlyLastFrame = orbitOnly;
            }

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
                // 所有层都推进风/自转/重投影矩阵(2D 轨道 pass 也依赖 currentRotation/cloudOffset;
                // prevViewProjMat 保持新鲜,切回体积云时重投影不失效)
                SetLayerDynamicProperties(layer);
                // 高空纯 2D:跳过体积 raymarch(本特性的性能大头;Upscale/历史也一并跳过)
                if (layer.orbitFade >= 0.999f) continue;

                layer.material.SetTexture("DepthTex", lowResDepthTex);   // Clouds pass 地面遮挡用
                // MRT: cloudTex(RGBA) + cloudDepthTex(RFloat) + cloudMVTex(RG 本帧运动矢量)
                var mrt = new RenderBuffer[] { layer.cloudTex.colorBuffer, layer.cloudDepthTex.colorBuffer, layer.cloudMVTex.colorBuffer };
                Graphics.SetRenderTarget(mrt, layer.cloudTex.depthBuffer);
                layer.material.SetPass(cloudsPass);
                Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                // 运动矢量膨胀:cloudMVTex → tmp1 → tmp2 → cloudMVDilatedTex(3 次 3×3)。
                // KSA 结构下这是【本帧】膨胀,供同帧 Upscale 使用(消除 1 帧滞后)。
                int dilatePass = layer.material.FindPass("DilateMV");
                if (dilatePass >= 0)
                {
                    var mvTmp1 = RenderTexture.GetTemporary(layer.cloudMVTex.width, layer.cloudMVTex.height, 0, layer.cloudMVTex.format);
                    var mvTmp2 = RenderTexture.GetTemporary(layer.cloudMVTex.width, layer.cloudMVTex.height, 0, layer.cloudMVTex.format);
                    Graphics.Blit(layer.cloudMVTex, mvTmp1, layer.material, dilatePass);
                    Graphics.Blit(mvTmp1, mvTmp2, layer.material, dilatePass);
                    Graphics.Blit(mvTmp2, layer.cloudMVDilatedTex, layer.material, dilatePass);
                    RenderTexture.ReleaseTemporary(mvTmp1);
                    RenderTexture.ReleaseTemporary(mvTmp2);
                }
            }

            // 3.5 轨道云(2D 壳着色):每层渲染到 orbitCloudTex(仅当淡入因子>0;pass 缺失时优雅降级为纯体积云)
            if (orbitPass >= 0)
            {
                foreach (var layer in activeLayers)
                {
                    if (layer.orbitFade <= 0.001f) continue;
                    // Blit 的 source 不被 OrbitClouds pass 采样(纯壳着色),仅作为合法非空输入
                    Graphics.Blit(lowResDepthTex, layer.orbitCloudTex, layer.material, orbitPass);
                }
            }

            // 4. Upscale each layer (KSA 时序核心,走 Graphics.Blit 单目标输出,_MainTex 自动绑 cloudTex。
            //    不用 MRT+DrawMeshNow:此前双 MRT(0 深度)在该路径上不渲染 → upscaled 恒黑 → 看不到云)
            int upscalePass = matRef.FindPass("Upscale");
            foreach (var layer in activeLayers)
            {
                if (layer.orbitFade >= 0.999f) continue;   // 高空纯 2D:体积时序链路整条跳过

                var mat = layer.material;
                mat.SetTexture("CloudDepthTex", layer.cloudDepthTex);
                mat.SetTexture("CloudMVDilatedTex", layer.cloudMVDilatedTex);   // 本帧膨胀 MV
                mat.SetTexture("CombinedDepthTex", combinedDepthTex);
                mat.SetTexture("HistoryTex", layer.historyTex);
                mat.SetTexture("HistoryDepthTex", layer.historyDepthTex);
                mat.SetTexture("HistoryCloudDepthTex", layer.historyCloudDepthTex);
                Graphics.Blit(layer.cloudTex, layer.upscaledCloudTex, layer.material, upscalePass);

                // 时序写回:全清上采样结果 → 历史(下一帧在 Upscale 里按 MV 重投影采样);
                // 云面距离历史 = 本帧低清 cloudDepth 上采样到全清(供下一帧 cloudGate 校验)
                Graphics.Blit(layer.upscaledCloudTex, layer.historyTex);
                Graphics.Blit(combinedDepthTex, layer.historyDepthTex);
                Graphics.Blit(layer.cloudDepthTex, layer.historyCloudDepthTex);
            }

            // 5. Chain-composite: iterate layers, applying composite mode
            int compositePass = matRef.FindPass("Composite");
            RenderTexture result = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Graphics.Blit(source, result);

            foreach (var layer in activeLayers)
            {
                matRef.SetTexture("UpscaledCloudTex", layer.upscaledCloudTex);
                matRef.SetTexture("OrbitCloudTex", layer.orbitCloudTex);
                matRef.SetTexture("SceneDepthTex", combinedDepthTex);
                matRef.SetFloat("_CompositeMode",
                    layer.config.compositeMode == CompositeMode.Standard ? 1.0f : 0.0f);
                // 交叉淡入因子:orbit pass 不可用 → 强制 0(纯体积云,等同未开启本特性)
                matRef.SetFloat("_OrbitFade", orbitPass >= 0 ? layer.orbitFade : 0f);

                var temp = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
                Graphics.Blit(result, temp, matRef, compositePass);
                RenderTexture.ReleaseTemporary(result);
                result = temp;
            }

            Graphics.Blit(result, destination);
            RenderTexture.ReleaseTemporary(result);
        }
        catch (Exception e)
        {
            Mod.LOG("Volken:CloudRenderer.OnRenderImage ERROR: " + e);
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
