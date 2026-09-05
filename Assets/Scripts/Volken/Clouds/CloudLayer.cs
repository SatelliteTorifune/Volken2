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
    public RenderTexture orbitCloudTex;       // 轨道云(2D 壳着色)输出,Composite 按 _OrbitFade 混合
    public float currentResolutionScale;
    public float currentOrbitRes = -1f;       // 轨道云当前分辨率缩放(签名,变化触发 RT 重建)

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

    // === 轨道云(2D 壳着色 + 过渡带交叉淡入) ===
    public float orbitFade;            // 本帧海拔淡入因子 0..1(CloudRenderer 每帧写入)
    public bool orbitOnlyLastFrame;    // 上一帧是否纯 2D(进入纯 2D 时清时序历史,防切回残影)
    private bool _staticPropsLogged;   // 静态参数诊断日志只打一次

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
            Mod.Log($"CloudLayer[{layerIndex}]: noise generator is null, skipping noise generation.");
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
    public void SetStaticShaderProperties(Material target = null)
    {
        Material mat = target != null ? target : material;
        if (config == null || mat == null) return;

        mat.SetFloat("cloudDensity", config.density);
        mat.SetFloat("cloudAbsorption", config.absorption);
        mat.SetFloat("ambientLight", config.ambientLight);
        mat.SetFloat("cloudCoverage", config.coverage);
        mat.SetFloat("cloudScale", 1.0f / Mathf.Max(0.1f, config.shapeScale));
        mat.SetFloat("detailScale", 1.0f / Mathf.Max(0.1f, config.detailScale));
        mat.SetFloat("detailStrength", config.detailStrength);
        mat.SetVector("cloudLayerHeights", config.layerHeights);
        mat.SetVector("cloudLayerSpreads", config.layerSpreads);
        mat.SetVector("cloudLayerStrengths", config.layerStrengths);
        mat.SetFloat("maxCloudHeight", Mathf.Max(0.001f, config.maxCloudHeight));
        mat.SetFloat("stepSize", Mathf.Max(0.01f, config.stepSize));
        mat.SetFloat("stepSizeFalloff", config.stepSizeFalloff);
        mat.SetFloat("numLightSamplePoints", Mathf.Clamp(config.numLightSamplePoints, 1, 50));
        float lightSamples = Mathf.Max(1f, (float)Mathf.Clamp(config.numLightSamplePoints, 1, 50));
        mat.SetFloat("lightStepSize", Mathf.Max(0.01f, config.lightMarchDistance / lightSamples));
        mat.SetFloat("scatterStrength", config.scatterStrength * 1e-3f);
        mat.SetFloat("atmoBlendFactor", config.atmoBlendFactor * 4e-6f);
        mat.SetColor("cloudColor", config.cloudColor);
        mat.SetFloat("depthThreshold", 0.01f * config.depthThreshold);
        mat.SetFloat("blueNoiseStrength", config.blueNoiseStrength);
        mat.SetFloat("historyBlend", config.historyBlend);
        mat.SetFloat("historyDepthThreshold", config.historyDepthThreshold);
        mat.SetVector("phaseParams", config.phaseParameters);
        mat.SetFloat("scatterPower", config.scatterPower);
        mat.SetFloat("multiScatterBlend", config.multiScatterBlend);
        mat.SetFloat("ambientScatterStrength", config.ambientScatterStrength);
        mat.SetVector("customWavelengths", config.customWavelengths);
        mat.SetFloat("silverLiningIntensity", config.silverLiningIntensity);
        mat.SetFloat("forwardScatteringBias", config.forwardScatteringBias);

        // 方案 B: 游戏自带云作为全球分布形状(无 cubemap 时强制关闭 → 完全回退程序化分布)
        bool hasStock = StockCloudMap.Current != null;
        mat.SetFloat("useStockCloudMap", (config.useStockCloudMap && hasStock) ? 1f : 0f);
        mat.SetFloat("stockMapStrength", Mathf.Clamp01(config.stockMapStrength));
        mat.SetFloat("stockMaskInfluence", Mathf.Clamp01(config.stockMaskInfluence));
        mat.SetFloat("stockMapLayer", Mathf.Clamp(config.stockMapLayer, 0, 3));
        mat.SetVector("stockLayerValid", StockCloudMap.LayerValid);
        mat.SetFloat("stockAlignSign", Mathf.Sign(config.stockAlignSign));
        mat.SetFloat("stockAlignAngleOffset", config.stockAlignAngleOffset);

        // 轨道云(2D 壳着色)静态参数
        mat.SetFloat("orbitSampleAltitude", Mathf.Max(0f, config.orbitSampleAltitude));
        mat.SetFloat("orbitDensityBoost", Mathf.Max(0.01f, config.orbitDensityBoost));
        mat.SetFloat("orbitBrightness", Mathf.Max(0f, config.orbitBrightness));
        mat.SetFloat("orbitReliefStrength", Mathf.Max(0f, config.orbitReliefStrength));
        mat.SetFloat("orbitDetailStrength", Mathf.Max(0f, config.orbitDetailStrength));
        mat.SetFloat("_OrbitDebugMode", config.orbitDebugMode > 0.5f ? 1f : 0f);

        // 诊断日志:游戏自带云层检测 + 轨道云静态参数(定位 2D/体积范围差异)
        if (!_staticPropsLogged)
        {
            _staticPropsLogged = true;
            bool hasStockNow = StockCloudMap.Current != null;
            Mod.Log($"CloudLayer[{layerIndex}] stockValid=" + StockCloudMap.LayerValid +
                " useStock=" + ((config.useStockCloudMap && hasStockNow) ? 1 : 0) +
                " stockLayer=" + Mathf.Clamp(config.stockMapLayer, 0, 3) +
                " stockStrength=" + Mathf.Clamp01(config.stockMapStrength) +
                " stockMaskInf=" + Mathf.Clamp01(config.stockMaskInfluence) +
                " orbit(alt=" + config.orbitSampleAltitude +
                " boost=" + config.orbitDensityBoost +
                " bright=" + config.orbitBrightness +
                " relief=" + config.orbitReliefStrength +
                " detail=" + config.orbitDetailStrength +
                " res=" + config.orbitResolutionScale + ")");
        }
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

        // 轨道云(2D 壳着色):按 orbitResolutionScale 降分辨率渲染 + Composite 双线性软化
        // (KSA 2D 云同样不追求全清;半清还天然软化程序化噪声的颗粒感)
        float orbitRes = Mathf.Clamp(config.orbitResolutionScale, 0.1f, 1f);
        orbitCloudTex = CreateRT(
            Mathf.Max(1, Mathf.RoundToInt(screenW * orbitRes)),
            Mathf.Max(1, Mathf.RoundToInt(screenH * orbitRes)),
            RenderTextureFormat.ARGB32, "OrbitCloudTex" + layerIndex);
        currentOrbitRes = orbitRes;

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
        ReleaseRT(ref orbitCloudTex);
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
