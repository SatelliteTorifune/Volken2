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

    // === 割裂线排查诊断(2026-08-24,排查完可整体删除) ===
    private int _probeFrame;
    private const int PROBE_FRAMES = 15;     // 启动/重臂后连续探查的帧数
    private const bool DIAG_PROBE = true;    // 总开关

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

    // === 割裂线排查诊断(2026-08-24) ===
    // 排查:按 F1 重新武装诊断探查(重跑 PROBE_FRAMES 帧)
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1))
        {
            _probeFrame = 0;
            Mod.LOG("Volken:DIAG probe re-armed (F1)");
        }
    }

    // 排查:相机姿态 + 一层参数(每探查帧打一次)
    private void LogCamDiag()
    {
        try
        {
            var craftNode = Game.Instance.FlightScene.CraftNode;
            Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            float planetRadius = (float)craftNode.Parent.PlanetData.Radius;
            Vector3 p = cam.transform.position;
            float dist = (p - planetCenter).magnitude;
            float altitude = dist - planetRadius;
            Vector3 nadir = (planetCenter - p).normalized;
            float pitch = Vector3.Angle(cam.transform.forward, -nadir); // 0=水平,90=正俯视行星中心
            var l0 = Volken.Instance.ActiveLayers.FirstOrDefault();
            float cloudPhi = (l0 == null) ? 0f : l0.accumulatedRotation + 2f * Mathf.PI * l0.runningOffset.x;
            float dPhi = (l0 == null || float.IsNaN(l0.prevCloudAngle)) ? 0f : cloudPhi - l0.prevCloudAngle; // 本帧云空间旋转增量
            Mod.LOG($"Volken:DIAGCAM alt={altitude:F0} distPlanet={dist:F0} radius={planetRadius:F0} pitch={pitch:F1} camPos={p:F1} near={cam.nearClipPlane:F1} far={cam.farClipPlane:F0} maxFarDepth={FarCameraScript.maxFarDepth:F0} resScale={(l0?.config.resolutionScale ?? -1f):F3} histBlend={(l0?.config.historyBlend ?? -1f):F2} temporal={(l0?.config.useTemporalUpscale ?? false)} frameNo={(l0?.frameNumber ?? -1)} accRot={(l0?.accumulatedRotation ?? 0f):F3} prevCloudAngle={(l0?.prevCloudAngle ?? 0f):F3} offX={(l0?.runningOffset.x ?? 0f):F3} cloudPhi={cloudPhi:F3} dPhi={dPhi:F3} screen={Screen.width}x{Screen.height}");

            // === 决定性探针:重投影矩阵 vs 射线重建是否一致 ===
            // reprojUV 位移(mode6)在静态相机下应为 ~0;若 prevViewProjMat 与 viewDir 重建
            // 的 FOV/平移不一致,会产生固定的径向错位 → 历史采样错位 → 云边残影/割裂线。
            try
            {
                var P = cam.projectionMatrix;
                float projTanHalfV = Mathf.Abs(1f / P.m11);   // 投影矩阵隐含的 tan(半垂直视场)
                float projTanHalfH = Mathf.Abs(1f / P.m00);   // tan(半水平视场)
                float reconTanHalfV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var invView = cam.worldToCameraMatrix.inverse;
                Vector3 camPosFromView = new Vector3(invView.m03, invView.m13, invView.m23);
                float viewShift = (p - camPosFromView).magnitude; // view矩阵平移 vs transform 位置差
                Mod.LOG($"Volken:DIAGPROJ m00={P.m00:F4} m11={P.m11:F4} m22={P.m22:F4} m23={P.m23:F4} camFov={cam.fieldOfView:F2} camAspect={cam.aspect:F4} reconTanV={reconTanHalfV:F4} projTanV={projTanHalfV:F4} projTanH={projTanHalfH:F4} viewShift={viewShift:F1}");

                // === DIAGPROJ2:空间一致性 ===
                // mode6 位移在静止相机下仍 0.07~1.0,而 FOV/平移全部自洽 →
                // 嫌疑:shader 的 _WorldSpaceCameraPos(camPos)与 prevViewProjMat 所在空间不一致,
                // 或 prevViewProjMat 陈旧。此探针在 CPU 上直接复算中心像素的重投影。
                try
                {
                    Vector3 wsp = Shader.GetGlobalVector("_WorldSpaceCameraPos");
                    float camPosShift = (wsp - p).magnitude;                 // 渲染用相机位置 vs transform 差
                    var rp = l0 != null ? l0.prevViewProjMat : Matrix4x4.identity;
                    var invRP = rp.inverse;
                    Vector3 rpCamPos = new Vector3(invRP.m03, invRP.m13, invRP.m23);
                    float rpStale = (rpCamPos - p).magnitude;                // prevViewProjMat 隐含相机位置 vs 当前
                    float projFlip = Shader.GetGlobalVector("_ProjectionParams").x; // <0 = D3D Y 翻转
                    // 中心像素(ndc=(0,0) 前方射线)在几个深度下的 reprojUV;自洽时应恒为 (0.5,0.5)
                    Vector3 fwd = cam.transform.forward;
                    var sb2 = new System.Text.StringBuilder();
                    foreach (float D in new float[] { 1000f, 100000f, 100000000f })
                    {
                        Vector3 Pw = wsp + D * fwd;
                        Vector4 clip = rp * new Vector4(Pw.x, Pw.y, Pw.z, 1f);
                        Vector2 ruv = new Vector2(0.5f * (clip.x / clip.w) + 0.5f, 0.5f * (clip.y / clip.w) + 0.5f);
                        if (projFlip < 0f) ruv.y = 1f - ruv.y;               // 模拟 shader Y 翻转
                        sb2.Append(" D" + D + "->(" + ruv.x.ToString("F3") + "," + ruv.y.ToString("F3") + ")");
                    }
                    Mod.LOG($"Volken:DIAGPROJ2 wsp={wsp} camPosShift={camPosShift:F1} rpCamPos={rpCamPos} rpStale={rpStale:F1} projFlip={projFlip:F2}{sb2}");
                }
                catch (Exception e3) { Mod.LOG("Volken:DIAGPROJ2 error " + e3); }
            }
            catch (Exception e2) { Mod.LOG("Volken:DIAGPROJ error " + e2); }
        }
        catch (Exception e) { Mod.LOG("Volken:DIAGCAM error " + e); }
    }

    // 排查:读回 RT 中心列,输出 16 段粗剖面 + 最大跳变行 + 云带范围(定位割裂线/深度缝的屏幕行)
    private static string CenterColumnProfile(RenderTexture rt, bool depth)
    {
        if (rt == null || !rt.IsCreated()) return "(null)";
        int W = rt.width, H = rt.height;
        var old = RenderTexture.active;
        var tex = new Texture2D(W, H, depth ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        int x = W / 2;
        var sb = new System.Text.StringBuilder();
        sb.Append(" centerCol x=" + x + " [");
        for (int i = 0; i < 16; i++)
        {
            int y = Mathf.Clamp(H * i / 15, 0, H - 1);
            if (depth)
                sb.Append((i == 0 ? "" : ",") + tex.GetPixel(x, y).r.ToString("F0"));
            else
            {
                var c = tex.GetPixel(x, y);
                sb.Append((i == 0 ? "" : ",") + "a" + c.a.ToString("F2"));
            }
        }
        sb.Append(" ]");
        // 最大跳变行
        float maxJump = -1f; int maxRow = -1;
        float prev = depth ? tex.GetPixel(x, 0).r : tex.GetPixel(x, 0).a;
        for (int y = 1; y < H; y++)
        {
            float cur = depth ? tex.GetPixel(x, y).r : tex.GetPixel(x, y).a;
            float j = Mathf.Abs(cur - prev);
            if (j > maxJump) { maxJump = j; maxRow = y; }
            prev = cur;
        }
        if (maxRow >= 0)
            sb.Append(" | maxJump y=" + maxRow + " (" + ((float)maxRow / H).ToString("P0") + " from bottom) d=" + maxJump.ToString("F2"));
        if (depth)
        {
            if (maxRow >= 0)
                sb.Append(" val@jump=" + tex.GetPixel(x, maxRow).r.ToString("F0") + "<-" + tex.GetPixel(x, Mathf.Max(0, maxRow - 1)).r.ToString("F0"));
        }
        else
        {
            // 颜色类:云带范围(alpha<0.9 的首尾行) + 最密行(alpha 最小)
            int first = -1, last = -1; float minA = 2f; int minRow = -1;
            for (int y = 0; y < H; y++)
            {
                float a = tex.GetPixel(x, y).a;
                if (a < 0.9f) { if (first < 0) first = y; last = y; }
                if (a < minA) { minA = a; minRow = y; }
            }
            if (first >= 0)
                sb.Append(" | cloudRows y=" + first + ".." + last + " (" + ((float)first / H).ToString("P0") + ".." + ((float)last / H).ToString("P0") + " from bottom) minA=" + minA.ToString("F2") + "@y" + minRow);
            else
                sb.Append(" | noCloud(col alpha>=0.9)");
        }
        // 两侧列(25% / 75%)紧凑摘要:捕获左右两条线的屏幕行
        foreach (int cx in new int[] { W / 4, 3 * W / 4 })
        {
            int cfirst = -1, clast = -1; float cminA = 2f; int cminRow = -1;
            float cmaxJump = -1f; int cmaxRow = -1;
            float cprev = depth ? tex.GetPixel(cx, 0).r : tex.GetPixel(cx, 0).a;
            for (int y = 0; y < H; y++)
            {
                float cur = depth ? tex.GetPixel(cx, y).r : tex.GetPixel(cx, y).a;
                float j = Mathf.Abs(cur - cprev);
                if (j > cmaxJump) { cmaxJump = j; cmaxRow = y; }
                cprev = cur;
                if (!depth)
                {
                    if (cur < 0.9f) { if (cfirst < 0) cfirst = y; clast = y; }
                    if (cur < cminA) { cminA = cur; cminRow = y; }
                }
            }
            if (depth)
                sb.Append(" | x" + cx + " maxJump y=" + cmaxRow + " (" + ((float)cmaxRow / H).ToString("P0") + ") d=" + cmaxJump.ToString("F2"));
            else if (cfirst >= 0)
                sb.Append(" | x" + cx + " cloudRows y=" + cfirst + ".." + clast + " (" + ((float)cfirst / H).ToString("P0") + ".." + ((float)clast / H).ToString("P0") + ") minA=" + cminA.ToString("F2") + "@y" + cminRow);
            else
                sb.Append(" | x" + cx + " noCloud");
        }
        UnityEngine.Object.Destroy(tex);
        return sb.ToString();
    }

    private void ProbeDepthRT(RenderTexture rt, string label)
    {
        if (rt == null || !rt.IsCreated()) return;
        Mod.LOG("Volken:DIAGDEPTH " + label + " " + rt.width + "x" + rt.height + CenterColumnProfile(rt, true));
    }
    private void ProbeCloudRT(RenderTexture rt, string label)
    {
        if (rt == null || !rt.IsCreated()) return;
        Mod.LOG("Volken:DIAGCLOUD " + label + " " + rt.width + "x" + rt.height + CenterColumnProfile(rt, false));
    }

    // 排查:云(当前帧) vs 历史(上一帧)逐行差异 → 残影/鬼影行(运动残影的直接度量)
    private void ProbeGhostDiff(CloudLayer l0)
    {
        try
        {
            if (l0 == null || l0.cloudTex == null || l0.historyTex == null) return;
            if (!l0.cloudTex.IsCreated() || !l0.historyTex.IsCreated()) return;
            int W = l0.cloudTex.width, H = l0.cloudTex.height;
            var old = RenderTexture.active;
            var tc = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var th = new Texture2D(W, H, TextureFormat.RGBA32, false);
            RenderTexture.active = l0.cloudTex; tc.ReadPixels(new Rect(0, 0, W, H), 0, 0); tc.Apply();
            RenderTexture.active = l0.historyTex; th.ReadPixels(new Rect(0, 0, W, H), 0, 0); th.Apply();
            RenderTexture.active = old;
            var sb = new System.Text.StringBuilder();
            foreach (int cx in new int[] { W / 4, W / 2, 3 * W / 4 })
            {
                int nDiff = 0, first = -1, last = -1, maxRow = -1; float maxD = -1f;
                for (int y = 0; y < H; y++)
                {
                    float dc = tc.GetPixel(cx, y).a, dh = th.GetPixel(cx, y).a;
                    float d = Mathf.Abs(dc - dh);
                    if (d > 0.05f)
                    {
                        nDiff++; if (first < 0) first = y; last = y;
                        if (d > maxD) { maxD = d; maxRow = y; }
                    }
                }
                sb.Append(" | x" + cx + " diffRows=" + (first >= 0
                    ? first + ".." + last + " (" + ((float)first / H).ToString("P0") + ".." + ((float)last / H).ToString("P0") + ") n=" + nDiff + " max|dA|=" + maxD.ToString("F2") + "@y" + maxRow
                    : "none"));
            }
            UnityEngine.Object.Destroy(tc); UnityEngine.Object.Destroy(th);
            Mod.LOG("Volken:DIAGGHOST hist-vs-curr alpha差异(运动残影直接度量)" + sb);
        }
        catch (Exception e) { Mod.LOG("Volken:DIAGGHOST error " + e); }
    }

    private void RunDiagProbe(List<CloudLayer> layers)
    {
        LogCamDiag();
        ProbeDepthRT(combinedDepthTex, "combinedDepth");
        ProbeDepthRT(lowResDepthTex, "lowResDepth");
        var l0 = layers.Count > 0 ? layers[0] : null;
        if (l0 != null)
        {
            ProbeCloudRT(l0.cloudTex, "cloudTex");
            ProbeCloudRT(l0.upscaledCloudTex, "upscaledCloudTex");
            ProbeCloudRT(l0.historyTex, "histTex");               // 上一帧云内容(残影/鬼影对比)
            ProbeDepthRT(l0.cloudDepthTex, "cloudDepthTex");       // RFloat → R 通道=云面距离(米)
            ProbeDepthRT(l0.historyCloudDepthTex, "histCloudDepthTex");
            ProbeDepthRT(l0.historyDepthTex, "histDepthTex");
            ProbeGhostDiff(l0);
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

            // === 排查:割裂线诊断探查(前 PROBE_FRAMES 帧 / F8 重新武装) ===
            if (DIAG_PROBE && _probeFrame < PROBE_FRAMES)
            {
                try
                {
                    // 前 7 帧:把 layer0 的 Clouds pass 以 _DiagBlend=1..7 重渲进临时 RT,
                    // 读回 depthWeight/edgeFade/cloudGate/finalBlend/depthDiff/位移/noCloud 的列剖面(不污染 cloudTex/历史)
                    var l0 = activeLayers.Count > 0 ? activeLayers[0] : null;
                    if (l0 != null && _probeFrame < 7)
                    {
                        int mode = _probeFrame + 1;
                        l0.material.SetFloat("_DiagBlend", mode);
                        var diagRT = RenderTexture.GetTemporary(l0.cloudTex.width, l0.cloudTex.height, 0, RenderTextureFormat.ARGB32);
                        Graphics.SetRenderTarget(diagRT);
                        l0.material.SetPass(cloudsPass);
                        Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);
                        Mod.LOG("Volken:DIAGBLEND mode=" + mode + " " + CenterColumnProfile(diagRT, false));
                        RenderTexture.ReleaseTemporary(diagRT);
                        l0.material.SetFloat("_DiagBlend", 0);
                    }
                    RunDiagProbe(activeLayers);
                }
                catch (Exception e) { Mod.LOG("Volken:DIAG probe error: " + e); }
                _probeFrame++;
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
