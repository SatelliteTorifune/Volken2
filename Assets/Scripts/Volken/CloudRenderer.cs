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

    public CloudRenderer()
    {
        cam = GetComponent<Camera>();
        CloudRenderManualRefresh();
        Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
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

        // === 方案 C: 时序超采样(每帧 1/N 子集步进 + 历史累积) ===
        // 关闭 → _UseTemporal=0,shader 走现状路径(逐字节一致)。
        // 开启 → frameNumber==0 冷启动全步进(_Upscale=1 → 所有格都新鲜),此后每帧按最优采样序列
        //        只步进 1/(upscaleX*upscaleY) 的格子,其余格在 shader 里复用上一帧累积历史。
        var tcfg = layer.config;
        int upX = Mathf.Max(1, tcfg.upscaleX);
        int upY = Mathf.Max(1, tcfg.upscaleY);
        int totalCells = upX * upY;
        if (tcfg.useTemporalUpscale)
        {
            if (layer.temporalSequence == null || layer.temporalSequence.Length != totalCells)
            {
                layer.temporalSequence = UpscalingPixelSequence.FindOptimalSamplingSequence(upX, upY);
                layer.frameNumber = 0;   // 格网变化 → 历史相位作废 → 冷启动全步进一帧
            }

            if (layer.frameNumber == 0)
            {
                // 冷启动/重建:该帧所有格子都步进,避免历史为空时的起步大洞
                mat.SetVector("_SampleCell", Vector2.zero);
                mat.SetVector("_Upscale", Vector2.one);
            }
            else
            {
                int cell = layer.temporalSequence[(layer.frameNumber - 1) % totalCells];
                mat.SetVector("_SampleCell", new Vector2(cell % upX, cell / upX));
                mat.SetVector("_Upscale", new Vector2(upX, upY));
            }
            layer.frameNumber++;
            mat.SetFloat("_UseTemporal", 1f);
        }
        else
        {
            mat.SetVector("_SampleCell", Vector2.zero);
            mat.SetVector("_Upscale", Vector2.one);
            mat.SetFloat("_UseTemporal", 0f);
        }
        mat.SetVector("_LowResSize", new Vector2(
            layer.cloudTex != null ? layer.cloudTex.width : 1f,
            layer.cloudTex != null ? layer.cloudTex.height : 1f));

        // Update reprojection matrix for next frame
        layer.prevViewProjMat = cam.projectionMatrix * cam.worldToCameraMatrix;
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

            // 1. Check RTs for all active layers (create on first frame or resolution change)
            foreach (var layer in activeLayers)
            {
                bool needsCreate = layer.cloudTex == null || !layer.cloudTex.IsCreated() ||
                    Mathf.Abs(layer.currentResolutionScale - layer.config.resolutionScale) > 0.001f;
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
                SetLayerDynamicProperties(layer);

                layer.material.SetTexture("DepthTex", lowResDepthTex);
                layer.material.SetTexture("HistoryTex", layer.historyTex);
                layer.material.SetTexture("HistoryDepthTex", layer.historyDepthTex);
                layer.material.SetTexture("HistoryCloudDepthTex", layer.historyCloudDepthTex);

                // MRT: cloudTex(RGBA) + cloudDepthTex(RFloat)
                var mrt = new RenderBuffer[] { layer.cloudTex.colorBuffer, layer.cloudDepthTex.colorBuffer };
                Graphics.SetRenderTarget(mrt, layer.cloudTex.depthBuffer);
                layer.material.SetPass(cloudsPass);
                Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                // Copy to history
                Graphics.Blit(layer.cloudTex, layer.historyTex);
                Graphics.Blit(lowResDepthTex, layer.historyDepthTex);
                Graphics.Blit(layer.cloudDepthTex, layer.historyCloudDepthTex);
            }

            // 4. Upscale each layer
            int upscalePass = matRef.FindPass("Upscale");
            foreach (var layer in activeLayers)
            {
                layer.material.SetTexture("CombinedDepthTex", combinedDepthTex);
                layer.material.SetTexture("LowResDepthTex", lowResDepthTex);
                layer.material.SetInt("isNativeRes",
                    (layer.cloudTex.width == source.width && layer.cloudTex.height == source.height) ? 1 : 0);
                Graphics.Blit(layer.cloudTex, layer.upscaledCloudTex, layer.material, upscalePass);
            }

            // 5. Chain-composite: iterate layers, applying composite mode
            int compositePass = matRef.FindPass("Composite");
            RenderTexture result = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Graphics.Blit(source, result);

            foreach (var layer in activeLayers)
            {
                matRef.SetTexture("UpscaledCloudTex", layer.upscaledCloudTex);
                matRef.SetTexture("SceneDepthTex", combinedDepthTex);
                matRef.SetFloat("_CompositeMode",
                    layer.config.compositeMode == CompositeMode.Standard ? 1.0f : 0.0f);

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
            // 诊断:不再静默吞掉管线异常(之前无日志导致"无云但无从查")
            Mod.LOG("Volken:CloudRenderer.OnRenderImage ERROR: " + e);
            try { Graphics.Blit(source, destination); } catch { }
        }
    }

    private void OnDestroy()
    {
        ReleaseAllRenderTextures();
    }

}
