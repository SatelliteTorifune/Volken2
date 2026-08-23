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
                if (layer?.config != null) layer.config.enabled = false;
            }
        }
        else
        {
            bool hasAtmo = newParent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            foreach (var layer in Volken.Instance.layers)
            {
                if (layer?.config != null)
                    layer.config.enabled = hasAtmo && layer.config.enabled;
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

            // 3. Render each layer (independent raymarch)
            int cloudsPass = matRef.FindPass("Clouds");
            foreach (var layer in activeLayers)
            {
                SetLayerDynamicProperties(layer);

                layer.material.SetTexture("DepthTex", lowResDepthTex);
                layer.material.SetTexture("HistoryTex", layer.historyTex);
                layer.material.SetTexture("HistoryDepthTex", layer.historyDepthTex);

                Graphics.Blit(null, layer.cloudTex, layer.material, cloudsPass);

                // Copy to history
                Graphics.Blit(layer.cloudTex, layer.historyTex);
                Graphics.Blit(lowResDepthTex, layer.historyDepthTex);
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
        catch (Exception)
        {
            // Log silently — pipeline error, fallback to source
            try { Graphics.Blit(source, destination); } catch { }
        }
    }

    private void OnDestroy()
    {
        ReleaseAllRenderTextures();
    }
}
