using System;
using Assets.Scripts;
using UnityEngine;

/// <summary>
/// 封装单个云层的完整状态：配置 + 材质 + 噪声纹理 + 渲染目标。
/// 每个 CloudLayer 实例完全独立，零参数共享。
/// </summary>
public class CloudLayer
{
    // === 标识 ===
    public int layerIndex;
    public string displayName;     

    // === 配置 ===
    public CloudConfig config;
    public string currentConfigName = "Default";

    // === 材质 (独立实例，同一 Clouds.shader) ===
    public Material material;

    // === 渲染目标 ===
    public RenderTexture cloudTex;
    public RenderTexture upscaledCloudTex;
    public RenderTexture historyTex;
    public RenderTexture historyDepthTex;
    public RenderTexture cloudDepthTex;       // current frame cloud surface distance (MRT output)
    public RenderTexture historyCloudDepthTex; // previous frame cloud surface distance (for reprojection)
    public RenderTexture cloudMVTex;           // 本帧新鲜格运动矢量(reprojUV−i.uv,MRT 第三通道)
    public RenderTexture cloudMVDilatedTex;    // 3×3 膨胀后的运动矢量(供下一帧 !isFresh 重投影)
    public float currentResolutionScale;

    // === 噪声纹理 (完全独立，不同种子) ===
    public CloudNoise noise;
    public RenderTexture worleyTex;
    public RenderTexture detailTex;
    public Texture2D planetMapTex;
    public Texture2D blueNoiseTex;

    // === 动态状态 ===
    public float accumulatedRotation;
    public Vector3 runningOffset;     // 运行时累积的 offset（不污染序列化的 config.offset）
    public Matrix4x4 prevViewProjMat;
    public float prevCloudAngle = float.NaN; // 方案 C §5:云空间重投影用——上一帧的云转角相位 θ+2π·offset.x(风平移折算为经度旋转)

    // === 时序超采样(方案 C,KSA 完整结构) ===
    public int frameNumber;          // 距上次重建/配置变更的帧计数;0 = 冷启动
    public int[] temporalSequence;   // 当前 upscale 格网的采样序列(缓存,格网变化时重建)
    // TSS 配置签名:重建 RT 的依据(分辨率/时序开关/格网变化都触发重建)
    public int currentUpX = -1;
    public int currentUpY = -1;
    public int currentTemporal = -1;

    /// <summary>
    /// 生成该层的独立噪声纹理并设置到 material 上。
    /// </summary>
    public void GenerateNoiseTextures(Texture2D sharedBlueNoiseOverride = null)
    {
        if (noise == null)
        {
            Mod.LOG($"CloudLayer[{layerIndex}]: noise generator is null, skipping noise generation.");
            return;
        }

        worleyTex = noise.GetWhorleyFBM3D(128, 4 + layerIndex * 2, 4, 0.5f, 2.0f);
        material.SetTexture("CloudShapeTex", worleyTex);

        detailTex = noise.GetWhorleyFBM3D(128, 8 + layerIndex * 2, 4, 0.5f, 2.0f);
        material.SetTexture("CloudDetailTex", detailTex);

        planetMapTex = noise.GetPlanetMap(2048, 16.0f + layerIndex * 4.0f, 6, 0.5f, 2.0f);
        material.SetTexture("PlanetMapTex", planetMapTex);

        if (sharedBlueNoiseOverride != null)
        {
            blueNoiseTex = sharedBlueNoiseOverride;
        }
        else
        {
            blueNoiseTex = Mod.Instance.ResourceLoader.LoadAsset<Texture2D>(Volken.GetNoiseMapPath());
        }
        material.SetTexture("BlueNoiseTex", blueNoiseTex);
    }

    /// <summary>
    /// 设置该层 shader 的静态属性（不会每帧变化的参数）。
    /// </summary>
    public void SetStaticShaderProperties()
    {
        if (config == null || material == null) return;

        material.SetFloat("cloudDensity", config.density);
        material.SetFloat("cloudAbsorption", config.absorption);
        material.SetFloat("ambientLight", config.ambientLight);
        material.SetFloat("cloudCoverage", config.coverage);
        material.SetFloat("cloudScale", 1.0f / Mathf.Max(0.1f, config.shapeScale));
        material.SetFloat("detailScale", 1.0f / Mathf.Max(0.1f, config.detailScale));
        material.SetFloat("detailStrength", config.detailStrength);
        material.SetVector("cloudLayerHeights", config.layerHeights);
        material.SetVector("cloudLayerSpreads", config.layerSpreads);
        material.SetVector("cloudLayerStrengths", config.layerStrengths);
        material.SetFloat("maxCloudHeight", Mathf.Max(0.001f, config.maxCloudHeight));
        material.SetFloat("stepSize", Mathf.Max(0.01f, config.stepSize));
        material.SetFloat("stepSizeFalloff", config.stepSizeFalloff);
        material.SetFloat("numLightSamplePoints", Mathf.Clamp(config.numLightSamplePoints, 1, 50));
        float lightSamples = Mathf.Max(1f, (float)Mathf.Clamp(config.numLightSamplePoints, 1, 50));
        material.SetFloat("lightStepSize", Mathf.Max(0.01f, config.lightMarchDistance / lightSamples));
        material.SetFloat("scatterStrength", config.scatterStrength * 1e-3f);
        material.SetFloat("atmoBlendFactor", config.atmoBlendFactor * 4e-6f);
        material.SetColor("cloudColor", config.cloudColor);
        material.SetFloat("depthThreshold", 0.01f * config.depthThreshold);
        material.SetFloat("blueNoiseStrength", config.blueNoiseStrength);
        material.SetFloat("historyBlend", config.historyBlend);
        material.SetFloat("historyDepthThreshold", config.historyDepthThreshold);
        material.SetVector("phaseParams", config.phaseParameters);
        material.SetFloat("scatterPower", config.scatterPower);
        material.SetFloat("multiScatterBlend", config.multiScatterBlend);
        material.SetFloat("ambientScatterStrength", config.ambientScatterStrength);
        material.SetVector("customWavelengths", config.customWavelengths);
        material.SetFloat("silverLiningIntensity", config.silverLiningIntensity);
        material.SetFloat("forwardScatteringBias", config.forwardScatteringBias);

        // 方案 B: 游戏自带云作为全球分布形状(无 cubemap 时强制关闭 → 完全回退程序化分布)
        bool hasStock = StockCloudMap.Current != null;
        material.SetFloat("useStockCloudMap", (config.useStockCloudMap && hasStock) ? 1f : 0f);
        material.SetFloat("stockMapStrength", Mathf.Clamp01(config.stockMapStrength));
        material.SetFloat("stockMaskInfluence", Mathf.Clamp01(config.stockMaskInfluence));
        material.SetFloat("stockMapLayer", Mathf.Clamp(config.stockMapLayer, 0, 3));
        material.SetVector("stockLayerValid", StockCloudMap.LayerValid);
        material.SetFloat("stockAlignSign", Mathf.Sign(config.stockAlignSign));
        material.SetFloat("stockAlignAngleOffset", config.stockAlignAngleOffset);
    }

    /// <summary>
    /// 根据当前屏幕分辨率创建该层的渲染纹理。
    /// </summary>
    public void CreateRenderTextures(int screenW, int screenH)
    {
        // 方案 C:RT 重建 → 历史失效 → 冷启动全步进
        frameNumber = 0;
        temporalSequence = null;
        prevCloudAngle = float.NaN;   // 云转角相位随重建作废,首帧回退纯世界空间重投影

        // KSA 完整结构:
        //   TSS 开 → cloudRes = 低清(全清/格网),每帧全量 raymarch 低清;上采样在全清做时序累积。
        //   TSS 关 → cloudRes = 全清(现状基线),上采样=运动残影混合。
        //   历史一律全清(时序混合在上采样/全清层面采样历史)。
        float scale = Mathf.Max(0.1f, currentResolutionScale);
        bool tss = config.useTemporalUpscale;
        int upX = Mathf.Max(1, config.upscaleX);
        int upY = Mathf.Max(1, config.upscaleY);
        Vector2Int cloudRes = tss
            ? new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(screenW * scale / upX)), Mathf.Max(1, Mathf.RoundToInt(screenH * scale / upY)))
            : new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(screenW * scale)), Mathf.Max(1, Mathf.RoundToInt(screenH * scale)));

        cloudTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGB32, "CloudTex" + layerIndex, 16);
        cloudDepthTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.RFloat, "CloudDepthTex" + layerIndex);
        cloudMVTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGBHalf, "CloudMVTex" + layerIndex);
        cloudMVDilatedTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGBHalf, "CloudMVDilatedTex" + layerIndex);

        upscaledCloudTex = CreateRT(screenW, screenH, RenderTextureFormat.ARGB32, "UpscaledCloudTex" + layerIndex);
        historyTex = CreateRT(screenW, screenH, RenderTextureFormat.ARGB32, "HistoryTex" + layerIndex);
        historyDepthTex = CreateRT(screenW, screenH, RenderTextureFormat.RFloat, "HistoryDepthTex" + layerIndex);
        historyCloudDepthTex = CreateRT(screenW, screenH, RenderTextureFormat.RFloat, "HistoryCloudDepthTex" + layerIndex);

        currentUpX = upX;
        currentUpY = upY;
        currentTemporal = tss ? 1 : 0;
    }

    /// <summary>
    /// 释放该层的渲染纹理。
    /// </summary>
    public void ReleaseRenderTextures()
    {
        ReleaseRT(ref cloudTex);
        ReleaseRT(ref upscaledCloudTex);
        ReleaseRT(ref historyTex);
        ReleaseRT(ref historyDepthTex);
        ReleaseRT(ref cloudDepthTex);
        ReleaseRT(ref historyCloudDepthTex);
        ReleaseRT(ref cloudMVTex);
        ReleaseRT(ref cloudMVDilatedTex);
    }

    private static RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt, string name, int depthBits = 0)
    {
        var rt = new RenderTexture(Mathf.Max(1, w), Mathf.Max(1, h), depthBits, fmt);
        rt.name = name;
        rt.Create();
        return rt;
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt != null && rt.IsCreated())
        {
            rt.Release();
        }
        rt = null;
    }
}
