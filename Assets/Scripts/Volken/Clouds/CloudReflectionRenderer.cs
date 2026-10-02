using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Terrain.Rendering;
using HarmonyLib;
using UnityEngine;

/*
    水面反射合入云:Harmony postfix 到 WaterReflectionPlaneScript 的 3 参 UpdateReflections(position, normal, cam),此时反射场景已渲染进 cam.targetTexture。
    postfix 用反射相机参数跑一次低清粗步长 raymarch,把云 additively 叠进该 RT(主相机同帧稍后渲染水面时就会采样到云倒影)。
    ⚠️ 反射相机只设了 position + worldToCameraMatrix、没设 rotation → 基向量必须从 worldToCameraMatrix 提取;postfix 不在相机渲染上下文内,_WorldSpaceCameraPos 不可用 → 用显式 _CamPos。
    ⚠️ 走独立材质 clone + 独立 RT,不碰主相机共享的 layer.material / cloudTex / 历史;反射相机在水面(低空)→ _OrbitFade 恒 0。
    ⚠️ Clouds pass 用 Graphics.SetRenderTarget + DrawMeshNow,不要用 RenderTexture.active(该组合在部分路径上不渲染)。
*/

namespace Volken.Clouds
{
    

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
                if (_lastError != ex.Message)   // 只在错误信息变化时打一次,避免每帧刷屏
                {
                    _lastError = ex.Message;
                    Mod.Log("Volken:WaterReflectionCloudPatch ERROR: " + ex);
                }
            }
        }
    }

    public static class CloudReflectionRenderer
    {
        private static RenderTexture _cloudTex;
        private static Mesh _fullscreenTriangle;
        private static readonly Dictionary<CloudLayer, Material> _materials = new Dictionary<CloudLayer, Material>();
        private static readonly List<CloudLayer> _activeLayers = new List<CloudLayer>();
        private static bool _diagnosed;
        private static bool _loggedFirst;

        // 反射质量(只作用于 clone 材质)
        private const float kReflectionStepScale = 5f;
        private const int kReflectionLightSamples = 2;

        public static void Render(WaterReflectionPlaneScript plane, Camera cam)
        {
            if (!_diagnosed)   // 一次性诊断:确认 postfix 真被调到、参数正常
            {
                _diagnosed = true;
                int layerCount = -1;
                try { layerCount = Volken.Clouds.VolkenClouds.Instance != null ? Volken.Clouds.VolkenClouds.Instance.ActiveLayers.Count() : -1; } catch { }
                Mod.Log("Volken:CloudReflectionRenderer diag: InFlight=" + Game.InFlightScene +
                    " targetTexture=" + (cam != null && cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "null") +
                    " layers=" + layerCount);
            }

            if (!Game.InFlightScene) return;
            if (Volken.Clouds.VolkenClouds.Instance == null) return;
            if (!ModSettings.Instance.WaterReflection) return;   // 水面反射云开关(ModSettings > Water Reflection,默认关)

            var craftNode = Game.Instance.FlightScene?.CraftNode;
            if (craftNode == null || craftNode.Parent == null) return;

            RenderTexture rt = cam.targetTexture;
            if (rt == null || rt.width <= 0 || rt.height <= 0) return;

            // 复用缓冲:本方法每次水面反射都调,不用 LINQ + 临时 List
            Volken.Clouds.VolkenClouds.Instance.FillActiveLayers(_activeLayers);
            var activeLayers = _activeLayers;
            if (activeLayers.Count == 0) return;

            // ⚠️ 反射相机基向量必须从 worldToCameraMatrix 提取(transform 朝向与视图不对齐):三行分别 = 相机空间的 right / up / -fwd
            Matrix4x4 w2c = cam.worldToCameraMatrix;
            Vector3 fwd = new Vector3(-w2c.m20, -w2c.m21, -w2c.m22);
            Vector3 right = new Vector3(w2c.m00, w2c.m01, w2c.m02);
            Vector3 up = new Vector3(w2c.m10, w2c.m11, w2c.m12);
            Vector3 camPos = cam.transform.position;   // position 是镜像后的(只有旋转不对)

            if (!IsFinite(fwd) || !IsFinite(right) || !IsFinite(up) || !IsFinite(camPos)) return;

            float tanHalfFovV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = Mathf.Max(0.001f, cam.aspect);

            float maxDepth = 10000000f;   // 反射远裁剪 = WaterReflectionOptions.FarClipPlane(默认 10,000,000 m)
            try { if (plane.ReflectionOptions != null) maxDepth = plane.ReflectionOptions.FarClipPlane; } catch { }

            // 天体/太阳参数与 SetLayerDynamicProperties 一致。只读:runningOffset / accumulatedRotation 用主相机上一帧推进后的值,一帧之差可忽略。
            Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;
            Vector3 lightDir = sun != null ? sun.transform.forward : Vector3.up;
            float surfaceRadius = 1f;
            try { surfaceRadius = (float)craftNode.Parent.PlanetData.Radius; } catch { }

            // planetToBody(游戏自带云 cubemap 用),与主路径一致
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
                    Mod.Log("Volken:CloudReflectionRenderer pass not found (Clouds=" + cloudsPass + ", Composite=" + compositePass + ")");
                    return;
                }

                layer.SetStaticShaderProperties(mat);   // 与主材质同步(clone 可能落后于配置变更)

                mat.SetFloat("_ReflectionMode", 1f);     // 跳过 DepthTex 遮挡 + 用显式相机位置
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

                mat.SetVector("blueNoiseOffset", new Vector2(   // 与主路径同公式,但按反射 RT 尺寸缩放
                    Mathf.PerlinNoise(time * 0.5f + layer.layerIndex * 0.3f, 0f) * 2f - 1f,
                    Mathf.PerlinNoise(0f, time * 0.5f + layer.layerIndex * 0.3f) * 2f - 1f
                ));
                mat.SetVector("blueNoiseScale", new Vector2(rt.width, rt.height) / 512.0f);

                mat.SetFloat("stepSize", Mathf.Max(0.01f, layer.config.stepSize) * kReflectionStepScale);   // 粗步长 + 低光样本(只作用于 clone)
                mat.SetFloat("stepSizeFalloff", layer.config.stepSizeFalloff);
                float lightSamples = Mathf.Max(1f, kReflectionLightSamples);
                mat.SetFloat("numLightSamplePoints", lightSamples);
                mat.SetFloat("lightStepSize", Mathf.Max(0.01f, layer.config.lightMarchDistance / lightSamples));

                // Clouds pass 仍会算 MV,但不绑 MRT → 丢弃;reprojMat 给单位阵,避免未初始化矩阵产生 NaN
                mat.SetMatrix("reprojMat", Matrix4x4.identity);
                mat.SetVector("_SampleCell", Vector2.zero);
                mat.SetVector("_Upscale", new Vector2(1f, 1f));
                mat.SetVector("_LowResSize", new Vector2(rt.width, rt.height));
                mat.SetFloat("_UseTemporal", 0f);
                mat.SetFloat("historyBlend", 0f);

                // 低清 raymarch → _cloudTex,再 additive 合成进反射 RT
                RenderTexture prev = RenderTexture.active;
                try
                {
                    Graphics.SetRenderTarget(_cloudTex);
                    GL.Clear(true, true, Color.clear);
                    if (!mat.SetPass(cloudsPass)) return;
                    Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                    mat.SetTexture("UpscaledCloudTex", _cloudTex);
                    mat.SetTexture("OrbitCloudTex", _cloudTex);   // 轨道云不进反射路径:绑上避免空采样
                    mat.SetTexture("SceneDepthTex", _cloudTex);   // additive 分支不使用,绑上避免空采样
                    mat.SetFloat("_CompositeMode", 0f);           // additive
                    mat.SetFloat("_OrbitFade", 0f);               // 反射相机在水面(低空)→ 恒纯体积云

                    var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format);   // 不能同 RT 读写
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
                    Mod.Log("Volken:CloudReflectionRenderer ok: rt=" + rt.width + "x" + rt.height +
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
}
