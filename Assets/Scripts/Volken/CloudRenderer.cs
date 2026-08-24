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
    private int _diagPatternMode;            // 排障:本帧渲染的诊断图案模式(2=当前射线,3=原版内置矩阵射线)
    private int _diagPendingMode;             // 排障:待生效的模式
    private int _diagFrameNo;                // 排障:已记录的逐帧诊断帧数
    private int _probeRunCount;               // 排障:垂直分布探测已运行的次数(前 5 帧各跑一次)
    private bool _diagPatternPending;         // 排障:图案在下一帧绘制前生效(draw 后再置位太晚)
    private bool _diagSavePending;            // 排障:最终帧截图(存 PNG 供直接查看)
    private bool _diagCaptureRequested;        // 排障:F8 手动触发:任意场景截图+读回云深/场景深

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
        mat.SetMatrix("reprojMat", layer.prevViewProjMat);
        // 阶段二:观察射线用相机 transform 轴直接构造(NDC 来自 clip 坐标,无投影矩阵约定歧义)。
        // 注意:不要用 cameraToWorldMatrix 的第2列当 fwd——Unity 视图约定里那是 -forward,会反向。
        mat.SetVector("_CamFwd", cam.transform.forward);
        mat.SetVector("_CamRight", cam.transform.right);
        mat.SetVector("_CamUp", cam.transform.up);
        mat.SetFloat("_TanHalfFovV", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        mat.SetFloat("_Aspect", cam.aspect);
        mat.SetVector("clipPlanes", new Vector2(cam.nearClipPlane, cam.farClipPlane));

        // === 诊断(阶段二排障):打印一次相机/球壳几何 ===
        if (!layer.diagLogged)
        {
            layer.diagLogged = true;
            float planetRadius = (float)Game.Instance.FlightScene.CraftNode.Parent.PlanetData.Radius;
            Mod.LOG($"Volken:DIAG layer={layer.layerIndex} cam.pos={cam.transform.position} fwd={cam.transform.forward} up={cam.transform.up} fov={cam.fieldOfView} aspect={cam.aspect}");
            Mod.LOG($"Volken:DIAG layer={layer.layerIndex} sphereCenter={planetCenter} planetRadius={planetRadius} maxCloudH={layer.config.maxCloudHeight} distCamToPlanet={(cam.transform.position - planetCenter).magnitude:F1}");
            Mod.LOG($"Volken:DIAG layer={layer.layerIndex} camToWorld={RenderDiagMatrix(cam.cameraToWorldMatrix)}");
        }
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

        // 排障:打印一次时序参数 + 重投影矩阵
        if (layer.diagLogged && layer.diagTemporalLogged == false)
        {
            layer.diagTemporalLogged = true;
            Mod.LOG($"Volken:DIAG layer={layer.layerIndex} useTemporal={tcfg.useTemporalUpscale} upX={upX} upY={upY} frameNumber={layer.frameNumber} historyBlend={layer.config.historyBlend} threshold={layer.config.historyDepthThreshold} prevViewProj={RenderDiagMatrix(layer.prevViewProjMat)}");
        }

        // Update reprojection matrix for next frame
        layer.prevViewProjMat = cam.projectionMatrix * cam.worldToCameraMatrix;
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

    // 排障:按 F8 手动触发一次诊断(截图+读回云深),可在太空场景使用
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1))
        {
            _diagCaptureRequested = true;
            Mod.LOG("Volken:DIAG F8 requested capture");
        }
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

            // 排障:上一帧末请求的图案帧,在本帧 draw 前生效
            if (_diagPatternPending)
            {
                _diagPatternMode = _diagPendingMode;
                _diagPatternPending = false;
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
                layer.material.SetFloat("_DiagPattern", (float)_diagPatternMode);

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

            // === 诊断:前 8 帧逐帧打印相机/时序/RT 状态(观察时序推进与相机一致性) ===
            if (_diagFrameNo < 8 && activeLayers.Count > 0)
            {
                var l0 = activeLayers[0];
                var t = cam.transform;
                float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                Vector3 planetCenter = Game.Instance.FlightScene.CraftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                float farNear = FarCameraScript.maxFarDepth; // 远相机 far 裁剪面(每次 OnPreRender 更新)
                Mod.LOG($"Volken:FRAME#{_diagFrameNo} cam.pos={t.position.ToString("F1")} fwd={t.forward.ToString("F2")} right={t.right.ToString("F2")} up={t.up.ToString("F2")} fov={cam.fieldOfView.ToString("F1")} aspect={cam.aspect.ToString("F3")} tanV={tanV.ToString("F4")} distPlanet={(t.position - planetCenter).magnitude.ToString("F0")} camNear={cam.nearClipPlane.ToString("F1")} camFar={cam.farClipPlane.ToString("F0")} farCamFar={farNear.ToString("F0")}");
                Mod.LOG($"Volken:FRAME#{_diagFrameNo} useTemporal={l0.material.GetFloat("_UseTemporal")} sampleCell={l0.material.GetVector("_SampleCell")} upscale={l0.material.GetVector("_Upscale")} frameNumber={l0.frameNumber} cloudTex={l0.cloudTex.width}x{l0.cloudTex.height} depthTex={lowResDepthTex.width}x{lowResDepthTex.height} histDepth={l0.historyDepthTex.width}x{l0.historyDepthTex.height} prevViewProj={RenderDiagMatrix(l0.prevViewProjMat)}");
                // 第 4 帧末请求模式2(当前射线),第 5 帧末请求模式3(原版内置矩阵射线)
                if (_diagFrameNo == 4)
                {
                    _diagPatternPending = true;
                    _diagPendingMode = 2;
                    Mod.LOG("Volken:DIAG pattern mode2 (current ray) fires next frame");
                }
                if (_diagFrameNo == 5)
                {
                    _diagPatternPending = true;
                    _diagPendingMode = 3;
                    Mod.LOG("Volken:DIAG pattern mode3 (original builtin ray) fires next frame");
                }
                if (_diagFrameNo == 6)
                {
                    _diagPatternPending = true;
                    _diagPendingMode = 4;
                    Mod.LOG("Volken:DIAG pattern mode4 (verify _WorldSpaceCameraPos) fires next frame");
                }
                if (_diagFrameNo == 7)
                {
                    _diagSavePending = true;
                    Mod.LOG("Volken:DIAG will save final-frame screenshots next frame");
                }
                _diagFrameNo++;
            }

            // === 诊断(阶段二排障):读回 cloudTex 统计云像素垂直分布(前 5 帧各一次,看时序是否让云崩坏) ===
            foreach (var layer in activeLayers)
            {
                if (layer.diagLogged && _probeRunCount < 5)
                {
                    try
                    {
                        var rt = layer.cloudTex;
                        var old = RenderTexture.active;
                        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        tex.Apply();
                        RenderTexture.active = old;
                        int[] rows = new int[32];
                        int total = 0;
                        for (int gy = 0; gy < 32; gy++)
                        {
                            int y = gy * rt.height / 32;
                            for (int gx = 0; gx < 32; gx++)
                            {
                                int x = gx * rt.width / 32;
                                var c = tex.GetPixel(x, y);
                                if (c.a > 0.02f || c.r + c.g + c.b > 0.02f) { rows[gy]++; total++; }
                            }
                        }
                        int top = 0, mid = 0, bot = 0;
                        for (int y = 0; y < 32; y++)
                        {
                            if (y < 11) top += rows[y];
                            else if (y < 21) mid += rows[y];
                            else bot += rows[y];
                        }
                        // 找到云垂直范围的边界
                        int first = -1, last = -1;
                        for (int y = 0; y < 32; y++) { if (rows[y] > 0) { if (first < 0) first = y; last = y; } }
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} VERT#{_probeRunCount} cloudTex vertical: top={top} mid={mid} bot={bot} total={total} firstRow={first} lastRow={last} (row0=底, row31=顶)");
                        UnityEngine.Object.Destroy(tex);
                    }
                    catch (Exception ex) { Mod.LOG("Volken:DIAG probe ERROR: " + ex); }
                }
            }
            if (_probeRunCount < 5) _probeRunCount++;

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

            // === 诊断:测试图案帧读回 cloudTex + upscaledCloudTex 四角颜色 ===
            if (_diagPatternMode != 0)
            {
                int mode = _diagPatternMode;
                try
                {
                    foreach (var layer in activeLayers)
                    {
                        string s1 = ProbeCorners(layer.cloudTex);
                        string s2 = ProbeCorners(layer.upscaledCloudTex);
                        string g1 = ProbeGrid(layer.cloudTex);
                        Mod.LOG($"Volken:DIAG mode={mode} layer={layer.layerIndex} pattern cloudTex={s1}");
                        Mod.LOG($"Volken:DIAG mode={mode} layer={layer.layerIndex} pattern upscaled={s2}");
                        Mod.LOG($"Volken:DIAG mode={mode} layer={layer.layerIndex} grid cloudTex={g1}");
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} grid cloudDepth={ProbeRGrid(layer.cloudDepthTex)}");
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} grid histCloudDepth={ProbeRGrid(layer.historyCloudDepthTex)}");
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} grid lowResDepth={ProbeRGrid(lowResDepthTex)}");
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} grid histDepth={ProbeRGrid(layer.historyDepthTex)}");
                        Mod.LOG($"Volken:DIAG layer={layer.layerIndex} grid upscaled={ProbeGrid(layer.upscaledCloudTex)}");
                    }
                }
                catch (Exception ex) { Mod.LOG("Volken:DIAG pattern probe ERROR: " + ex); }
                finally
                {
                    _diagPatternMode = 0;
                    foreach (var layer in activeLayers)
                        layer.material.SetFloat("_DiagPattern", 0f);
                }
            }

            Graphics.Blit(result, destination);
            // === 排障:F8 手动触发(任意场景)读回云深/场景深 + 存真实云截图 ===
            if (_diagCaptureRequested || _diagSavePending)
            {
                _diagCaptureRequested = false;
                _diagSavePending = false;
                try
                {
                    Mod.LOG($"Volken:DIAG F8CAP depth lowRes={ProbeRGrid(lowResDepthTex)} ");
                    foreach (var layer in activeLayers)
                    {
                        Mod.LOG($"Volken:DIAG F8CAP layer={layer.layerIndex} cloudDepth={ProbeRGrid(layer.cloudDepthTex)}");
                        Mod.LOG($"Volken:DIAG F8CAP layer={layer.layerIndex} histCloudDepth={ProbeRGrid(layer.historyCloudDepthTex)}");
                        Mod.LOG($"Volken:DIAG F8CAP layer={layer.layerIndex} cloudTex={ProbeGrid(layer.cloudTex)}");
                    }
                    string dir = System.IO.Path.Combine(Application.persistentDataPath, "volken_diag");
                    System.IO.Directory.CreateDirectory(dir);
                    int stamp = System.Environment.TickCount;
                    SaveRTAsPng(result, System.IO.Path.Combine(dir, $"result_{stamp}.png"));
                    foreach (var layer in activeLayers)
                    {
                        SaveRTAsPng(layer.cloudTex, System.IO.Path.Combine(dir, $"cloudTex_{stamp}.png"));
                        SaveRTAsPng(layer.upscaledCloudTex, System.IO.Path.Combine(dir, $"upscaled_{stamp}.png"));
                    }
                }
                catch (Exception se) { Mod.LOG("Volken:DIAG F8CAP ERROR: " + se); }
            }
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

    // 排障:把 RT 保存为 PNG(注意:ReadPixels 读出的图像 y=0 是图像顶部,对应 RT 顶部)
    private static void SaveRTAsPng(RenderTexture rt, string path)
    {
        var old = RenderTexture.active;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.Destroy(tex);
        Mod.LOG("Volken:DIAG saved PNG " + path);
    }

    private static string RenderDiagMatrix(Matrix4x4 m)
    {
        return $"({m.m00:F2},{m.m01:F2},{m.m02:F2},{m.m03:F2})({m.m10:F2},{m.m11:F2},{m.m12:F2},{m.m13:F2})({m.m20:F2},{m.m21:F2},{m.m22:F2},{m.m23:F2})";
    }

    // 读回 RT 的真·顶/中/底 + 左右(全高比例采样,低清 RT 也不失真)
    private static string ProbeCorners(RenderTexture rt)
    {
        int W = rt.width, H = rt.height;
        var old = RenderTexture.active;
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        // GetPixel:y=0 是图像底部(ReadPixels 约定)
        var bl = tex.GetPixel(0, 0);            // 左下
        var tl = tex.GetPixel(0, H - 1);        // 左上
        var br = tex.GetPixel(W - 1, 0);        // 右下
        var tr = tex.GetPixel(W - 1, H - 1);    // 右上
        var ctr = tex.GetPixel(W / 2, H / 2);   // 中心
        UnityEngine.Object.Destroy(tex);
        string F(Color c) => $"({c.r.ToString("F2")},{c.g.ToString("F2")})";
        return $"BL={F(bl)} TL={F(tl)} BR={F(br)} TR={F(tr)} C={F(ctr)}";
    }

    // 5x3 网格读回 RFloat RT 的 R 通道(云深/场景深,单位=米,>100000 说明无云/远景)
    private static string ProbeRGrid(RenderTexture rt)
    {
        var old = RenderTexture.active;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < 3; row++)
        {
            int y = row == 0 ? 0 : (row == 1 ? rt.height / 2 : rt.height - 1);
            y = Mathf.Clamp(y, 0, rt.height - 1);
            for (int col = 0; col < 5; col++)
            {
                int x = (col * rt.width * 2 + rt.width) / 10;
                x = Mathf.Clamp(x, 0, rt.width - 1);
                float v = tex.GetPixel(x, y).r;
                sb.Append(v >= 100000f ? "far" : v.ToString("F0"));
                sb.Append(",");
            }
            sb.Append(" | ");
        }
        UnityEngine.Object.Destroy(tex);
        return sb.ToString();
    }

    // 5x3 网格(5列 x 3行:底/中/顶)采样图案帧,输出 RGB 三元组
    private static string ProbeGrid(RenderTexture rt)
    {
        var old = RenderTexture.active;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < 3; row++)
        {
            int y = row == 0 ? 0 : (row == 1 ? rt.height / 2 : rt.height - 1); // 底/中/顶
            y = Mathf.Clamp(y, 0, rt.height - 1);
            for (int col = 0; col < 5; col++)
            {
                int x = (col * rt.width * 2 + rt.width) / 10;
                x = Mathf.Clamp(x, 0, rt.width - 1);
                var c = tex.GetPixel(x, y);
                sb.Append($"({c.r.ToString("F2")},{c.g.ToString("F2")},{c.b.ToString("F2")})");
            }
            sb.Append(" | ");
        }
        UnityEngine.Object.Destroy(tex);
        return sb.ToString();
    }
}
