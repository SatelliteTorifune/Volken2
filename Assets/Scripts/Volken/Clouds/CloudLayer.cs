using System;
using Assets.Scripts;
using UnityEngine;
/// <summary>
/// 封装单个云层的【全局】状态:配置 + 材质 + 噪声纹理 + 风/自转累积量。
/// 每个 CloudLayer 实例完全独立,零参数共享。
///
/// 凡是"跟某台相机走"的渲染目标 / 时序状态 / 云空间重投影状态,已移到 <see cref="CloudLayerView"/>
/// (每个 CloudRenderer 实例一份)—— 这是多相机(主视角 + PIP 等额外摄像机)正确渲染体积云的前提。
/// </summary>

namespace Volken.Clouds
{
    

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

        // === 噪声纹理 (完全独立，不同种子) ===
        public CloudNoise noise;
        public RenderTexture worleyTex;
        public RenderTexture detailTex;
        public Texture2D planetMapTex;
        public Texture2D blueNoiseTex;

        // === 全局动态状态(所有相机共享;每帧只推进一次,见 CloudRenderer.AdvanceGlobalCloudState) ===
        public float accumulatedRotation;   // 云场自转累积角(全球共享)
        public Vector3 runningOffset;       // 运行时累积的风偏移(全球共享;不污染序列化的 config.offset)
        private bool _staticPropsLogged;    // 静态参数诊断日志只打一次

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
                blueNoiseTex = Mod.Instance.ResourceLoader.LoadAsset<Texture2D>(Volken.Core.VolkenMod.GetNoiseMapPath());
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
            mat.SetFloat("stockDensityScale", Mathf.Clamp01(config.stockDensityScale));
            mat.SetVector("stockLayerValid", StockCloudMap.LayerValid);
            mat.SetFloat("stockAlignSign", Mathf.Sign(config.stockAlignSign));
            mat.SetFloat("stockAlignAngleOffset", config.stockAlignAngleOffset);

            // 轨道云(2D 壳着色)静态参数
            mat.SetFloat("orbitSampleAltitude", Mathf.Max(0f, config.orbitSampleAltitude));
            mat.SetFloat("orbitDensityBoost", Mathf.Max(0.01f, config.orbitDensityBoost));
            mat.SetFloat("orbitBrightness", Mathf.Max(0f, config.orbitBrightness));
            mat.SetFloat("orbitReliefStrength", Mathf.Max(0f, config.orbitReliefStrength));
            mat.SetFloat("orbitDetailStrength", Mathf.Max(0f, config.orbitDetailStrength));
            // 调试分屏/覆盖足迹(_OrbitDebugMode):仅在 debug 模式(ModSettings.DevMode)下启用。
            // orbitDebugMode 配置兼容保留(旧存档可能为 1),非 debug 模式一律置 0 → 不渲染。
            bool orbitDebug = false;
            try { orbitDebug = ModSettings.Instance != null && ModSettings.Instance.DevMode; } catch { }
            mat.SetFloat("_OrbitDebugMode", (orbitDebug && config.orbitDebugMode > 0.5f) ? 1f : 0f);

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
                    " stockDensityScale=" + Mathf.Clamp01(config.stockDensityScale) +
                    " orbit(alt=" + config.orbitSampleAltitude +
                    " boost=" + config.orbitDensityBoost +
                    " bright=" + config.orbitBrightness +
                    " relief=" + config.orbitReliefStrength +
                    " detail=" + config.orbitDetailStrength +
                    " res=" + config.orbitResolutionScale + ")");
            }
        }
    }

}
