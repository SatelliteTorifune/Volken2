using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Terrain.Rendering;
using HarmonyLib;
using UnityEngine;

/*
    Volken 方案 A:水面平面反射合入云(实时反射适配)

    挂钩点:Harmony postfix 到 WaterReflectionPlaneScript 的 3 参私有方法
        private void UpdateReflections(Vector3 position, Vector3 normal, Camera cam)
    此时 cam.Render() 已把反射场景填进 cam.targetTexture(反射 RT,512/256 正方形,
    Default+Linear,16 位深度)。postfix 里用【反射相机参数】跑一次低清粗步长 raymarch,
    再把云 additively 叠加进该 RT;2 参调用者随后 SetGlobalTexture("_WaterReflectionTexture")
    指向同一 RT,主相机同帧稍后渲染水面时就会采样到云倒影。

    关键修正(相对初版分析文档):
    1. 反射相机 transform 的朝向与视图不对齐(3 参只设了 position + worldToCameraMatrix,
       没设 rotation)→ 相机基向量必须从 worldToCameraMatrix 提取,不能用 transform.forward。
    2. raymarch 的相机位置不能用 _WorldSpaceCameraPos(postfix 不在相机渲染上下文内)→
       Clouds.shader 改用显式 _CamPos,主相机路径在 SetLayerDynamicProperties 里也设置。
    3. 反射走独立材质 clone + 独立 RT,不碰主相机共享的 layer.material / cloudTex / 历史。
    4. 跳过 Upscale/TSS 整条链路:Clouds pass(SetRenderTarget + DrawMeshNow)→ 复用主路径
       Composite pass 走 Graphics.Blit 加法合成进反射 RT。
       (不要用 RenderTexture.active + DrawMeshNow:与主路径注释一致,该组合在部分路径上不渲染)
*/

[HarmonyPatch(typeof(WaterReflectionPlaneScript), "UpdateReflections",
    new[] { typeof(Vector3), typeof(Vector3), typeof(Camera) })]
public static class WaterReflectionCloudPatch
{
    private static string _lastError;

    private static void Postfix(WaterReflectionPlaneScript __instance, object[] __args)
    {
        try
        {
            var cam = __args[2] as Camera;
            if (cam == null) return;
            CloudReflectionRenderer.Render(__instance, cam);
        }
        catch (Exception ex)
        {
            // 只在错误信息变化时打一次,避免每帧刷屏。
            if (_lastError != ex.Message)
            {
                _lastError = ex.Message;
                Mod.LOG("Volken:WaterReflectionCloudPatch ERROR: " + ex);
            }
        }
    }
}

public static class CloudReflectionRenderer
{
    private static RenderTexture _cloudTex;
    private static Mesh _fullscreenTriangle;
    private static readonly Dictionary<CloudLayer, Material> _materials = new Dictionary<CloudLayer, Material>();
    private static bool _diagnosed;
    private static bool _loggedFirst;

    // 反射质量因子(对齐 VolRe #7 成本控制):粗步长 + 低光样本,只作用于 clone。
    private const float kReflectionStepScale = 5f;
    private const int kReflectionLightSamples = 2;

    public static void Render(WaterReflectionPlaneScript plane, Camera cam)
    {
        // 一次性诊断:确认 postfix 是否真的被调到、参数是否正常。
        if (!_diagnosed)
        {
            _diagnosed = true;
            int layerCount = -1;
            try { layerCount = Volken.Instance != null ? Volken.Instance.ActiveLayers.Count() : -1; } catch { }
            Mod.LOG("Volken:CloudReflectionRenderer diag: InFlight=" + Game.InFlightScene +
                " targetTexture=" + (cam != null && cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "null") +
                " layers=" + layerCount);
        }

        if (!Game.InFlightScene) return;
        if (Volken.Instance == null) return;
        // 水面反射云开关(ModSettings > Water Reflection,默认关)。
        if (!ModSettings.Instance.WaterReflection) return;

        var craftNode = Game.Instance.FlightScene?.CraftNode;
        if (craftNode == null || craftNode.Parent == null) return;

        RenderTexture rt = cam.targetTexture;
        if (rt == null || rt.width <= 0 || rt.height <= 0) return;

        var activeLayers = Volken.Instance.ActiveLayers.ToList();
        if (activeLayers.Count == 0) return;

        // 反射相机基向量:必须从 worldToCameraMatrix 提取(transform 朝向与视图不对齐)。
        // worldToCameraMatrix 的三行 = 相机空间的 right / up / -fwd 在世界坐标下的分量。
        Matrix4x4 w2c = cam.worldToCameraMatrix;
        Vector3 fwd = new Vector3(-w2c.m20, -w2c.m21, -w2c.m22);
        Vector3 right = new Vector3(w2c.m00, w2c.m01, w2c.m02);
        Vector3 up = new Vector3(w2c.m10, w2c.m11, w2c.m12);
        Vector3 camPos = cam.transform.position;   // position 是镜像后的位置(只有旋转不对)

        if (!IsFinite(fwd) || !IsFinite(right) || !IsFinite(up) || !IsFinite(camPos)) return;

        // 反射相机 fov/aspect 与主相机一致(InitializeReflectionCamera / 2 参 UpdateReflections 设置)。
        float tanHalfFovV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float aspect = Mathf.Max(0.001f, cam.aspect);

        // 反射远裁剪 = WaterReflectionOptions.FarClipPlane(默认 10,000,000 m,见反编译构造)。
        float maxDepth = 10000000f;
        try { if (plane.ReflectionOptions != null) maxDepth = plane.ReflectionOptions.FarClipPlane; } catch { }

        // 与 SetLayerDynamicProperties 一致的天体/太阳参数(只读,不推进任何云状态:
        // runningOffset / accumulatedRotation 直接用主相机上一帧推进后的值,一帧之差可忽略)。
        Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
        var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;
        Vector3 lightDir = sun != null ? sun.transform.forward : Vector3.up;
        float surfaceRadius = 1f;
        try { surfaceRadius = (float)craftNode.Parent.PlanetData.Radius; } catch { }

        // planetToBody(游戏自带云 cubemap 方案 B 用),与主路径一致。
        Matrix4x4 planetToBody = Matrix4x4.identity;
        try
        {
            var referenceFrame = craftNode.ReferenceFrame;
            if (referenceFrame != null)
            {
                var bx = referenceFrame.FrameToPlanetVector(Vector3.right);
                var by = referenceFrame.FrameToPlanetVector(Vector3.up);
                var bz = referenceFrame.FrameToPlanetVector(Vector3.forward);
                planetToBody.m00 = (float)bx.x; planetToBody.m10 = (float)bx.y; planetToBody.m20 = (float)bx.z;
                planetToBody.m01 = (float)by.x; planetToBody.m11 = (float)by.y; planetToBody.m21 = (float)by.z;
                planetToBody.m02 = (float)bz.x; planetToBody.m12 = (float)bz.y; planetToBody.m22 = (float)bz.z;
                planetToBody.m33 = 1f;
            }
        }
        catch { }

        float time = (float)Game.Instance.GameState.GetCurrentTime();

        EnsureResources(rt.width, rt.height);

        int cloudsPass = -1;
        int compositePass = -1;

        foreach (var layer in activeLayers)
        {
            if (layer?.config == null || layer.material == null) continue;

            Material mat = GetReflectionMaterial(layer);
            if (mat == null) continue;

            if (cloudsPass < 0) cloudsPass = mat.FindPass("Clouds");
            if (compositePass < 0) compositePass = mat.FindPass("Composite");
            if (cloudsPass < 0 || compositePass < 0)
            {
                Mod.LOG("Volken:CloudReflectionRenderer pass not found (Clouds=" + cloudsPass + ", Composite=" + compositePass + ")");
                return;
            }

            // 静态参数与主材质同步(clone 可能落后于配置变更)。
            layer.SetStaticShaderProperties(mat);

            // 反射模式:跳过 DepthTex 遮挡 + 显式相机位置。
            mat.SetFloat("_ReflectionMode", 1f);
            mat.SetTexture("DepthTex", _cloudTex);   // 反射分支不会采样,绑上避免空采样
            mat.SetVector("_CamPos", camPos);
            mat.SetVector("_CamFwd", fwd);
            mat.SetVector("_CamRight", right);
            mat.SetVector("_CamUp", up);
            mat.SetFloat("_TanHalfFovV", tanHalfFovV);
            mat.SetFloat("_Aspect", aspect);

            mat.SetFloat("maxDepth", maxDepth);
            mat.SetVector("sphereCenter", planetCenter);
            mat.SetVector("lightDir", lightDir);
            mat.SetVector("cloudOffset", layer.runningOffset);
            mat.SetFloat("currentRotation", layer.accumulatedRotation);
            mat.SetFloat("surfaceRadius", surfaceRadius);
            mat.SetMatrix("planetToBody", planetToBody);
            mat.SetTexture("StockCloudCube", StockCloudMap.Current);

            // 蓝噪声:与主路径同公式,但按反射 RT 尺寸缩放。
            mat.SetVector("blueNoiseOffset", new Vector2(
                Mathf.PerlinNoise(time * 0.5f + layer.layerIndex * 0.3f, 0f) * 2f - 1f,
                Mathf.PerlinNoise(0f, time * 0.5f + layer.layerIndex * 0.3f) * 2f - 1f
            ));
            mat.SetVector("blueNoiseScale", new Vector2(rt.width, rt.height) / 512.0f);

            // 反射质量:粗步长 + 低光样本(只作用于 clone,不影响主相机)。
            mat.SetFloat("stepSize", Mathf.Max(0.01f, layer.config.stepSize) * kReflectionStepScale);
            mat.SetFloat("stepSizeFalloff", layer.config.stepSizeFalloff);
            float lightSamples = Mathf.Max(1f, kReflectionLightSamples);
            mat.SetFloat("numLightSamplePoints", lightSamples);
            mat.SetFloat("lightStepSize", Mathf.Max(0.01f, layer.config.lightMarchDistance / lightSamples));

            // Clouds pass 仍会算 MV(cloudDepth/motionVector),但不绑 MRT → 丢弃;
            // reprojMat 给单位阵避免未初始化矩阵产生 NaN(结果不被使用)。
            mat.SetMatrix("reprojMat", Matrix4x4.identity);
            mat.SetVector("_SampleCell", Vector2.zero);
            mat.SetVector("_Upscale", new Vector2(1f, 1f));
            mat.SetVector("_LowResSize", new Vector2(rt.width, rt.height));
            mat.SetFloat("_UseTemporal", 0f);
            mat.SetFloat("historyBlend", 0f);

            // 1) 低清 raymarch → _cloudTex(反射 RT 分辨率)。
            //    与主路径一致:Graphics.SetRenderTarget + DrawMeshNow(不要用 RenderTexture.active)。
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(_cloudTex);
                GL.Clear(true, true, Color.clear);
                if (!mat.SetPass(cloudsPass)) return;
                Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                // 2) additive 合成进反射 RT(复用主路径 Composite pass,走 Graphics.Blit)。
                //    source = rt,写回 tmp 再拷回 rt(不能同 RT 读写)。
                mat.SetTexture("UpscaledCloudTex", _cloudTex);
                mat.SetTexture("OrbitCloudTex", _cloudTex);   // 轨道云不进反射路径:绑上避免空采样
                mat.SetTexture("SceneDepthTex", _cloudTex);   // additive 分支不使用,绑上避免空采样
                mat.SetFloat("_CompositeMode", 0f);           // additive
                mat.SetFloat("_OrbitFade", 0f);               // 反射相机在水面(低空)→ 恒纯体积云

                var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format);
                Graphics.Blit(rt, tmp, mat, compositePass);
                Graphics.Blit(tmp, rt);
                RenderTexture.ReleaseTemporary(tmp);
            }
            finally
            {
                RenderTexture.active = prev;
            }

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Mod.LOG("Volken:CloudReflectionRenderer ok: rt=" + rt.width + "x" + rt.height +
                    " layers=" + activeLayers.Count + " fov=" + cam.fieldOfView + " aspect=" + aspect +
                    " maxDepth=" + maxDepth + " Clouds=" + cloudsPass + " Composite=" + compositePass);
            }
        }
    }

    private static Material GetReflectionMaterial(CloudLayer layer)
    {
        Material mat;
        if (_materials.TryGetValue(layer, out mat))
        {
            if (mat == null || mat.shader != layer.material.shader)
            {
                _materials.Remove(layer);
                mat = null;
            }
        }
        if (mat == null)
        {
            mat = new Material(layer.material);
            _materials[layer] = mat;
        }
        return mat;
    }

    private static void EnsureResources(int w, int h)
    {
        if (_cloudTex != null && _cloudTex.IsCreated() && _cloudTex.width == w && _cloudTex.height == h)
            return;

        if (_cloudTex != null && _cloudTex.IsCreated())
            _cloudTex.Release();
        _cloudTex = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        _cloudTex.name = "VolkenReflectionCloudTex";
        _cloudTex.Create();

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
    }

    private static bool IsFinite(Vector3 v)
    {
        return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                 float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
