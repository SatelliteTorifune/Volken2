using System;
using UnityEngine;
using System.Xml.Serialization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Assets.Scripts;
using Application = UnityEngine.Application;

public enum CompositeMode
{
    Additive,  // 加法合成: result.rgb += cloudColor (零视觉干扰, 默认)
    Standard   // 标准合成: result = src * transmittance + cloudColor (物理遮挡)
}

[Serializable]
public class CloudConfig
{
    public const string CONFIG_FOLDER = "/UserData/VolkenConfig/";
    private const string DEFAULT_CONFIG_NAME = "Default";


    #region parameter
    public CompositeMode compositeMode = CompositeMode.Additive;
    public bool enabled;
    public float density;
    public float absorption;
    public float ambientLight;
    public float coverage;
    public float shapeScale;
    public float detailScale;
    public float detailStrength;

    
    
    
    
    [XmlIgnore]
    public Vector4 phaseParameters;
    [XmlElement("phaseParameters")]
    public SerializableVector4 phaseParametersSerializable
    {
        get => new SerializableVector4(phaseParameters);
        set => phaseParameters = value.ToVector4();
    }
    
    [XmlIgnore]
    public Vector3 offset;
    [XmlElement("offset")]
    public SerializableVector3 offsetSerializable
    {
        get => new SerializableVector3(offset);
        set => offset = value.ToVector3();
    }
    
    public float windSpeed;
    public float windDirection;
    public float globalRotationAngular;
    public float scatterStrength;
    public float atmoBlendFactor;
    
    [XmlIgnore]
    public Color cloudColor;
    [XmlElement("cloudColor")]
    public SerializableColor cloudColorSerializable
    {
        get => new SerializableColor(cloudColor);
        set => cloudColor = value.ToColor();
    }
    
    [XmlIgnore]
    public Vector4 layerHeights;
    [XmlElement("layerHeights")]
    public SerializableVector4 layerHeightsSerializable
    {
        get => new SerializableVector4(layerHeights);
        set => layerHeights = value.ToVector4();
    }
    
    [XmlIgnore]
    public Vector4 layerSpreads;
    [XmlElement("layerSpreads")]
    public SerializableVector4 layerSpreadsSerializable
    {
        get => new SerializableVector4(layerSpreads);
        set => layerSpreads = value.ToVector4();
    }
    
    [XmlIgnore]
    public Vector4 layerStrengths;
    [XmlElement("layerStrengths")]
    public SerializableVector4 layerStrengthsSerializable
    {
        get => new SerializableVector4(layerStrengths);
        set => layerStrengths = value.ToVector4();
    }
    
    public float maxCloudHeight;
    public float resolutionScale;
    public float stepSize;
    public float stepSizeFalloff;
    public int numLightSamplePoints;
    public float lightMarchDistance = 12000f;   // 光照步进总距离(米);lightStepSize = lightMarchDistance / numLightSamplePoints
    public float blueNoiseStrength;
    public float depthThreshold;
    public float historyBlend;
    public float historyDepthThreshold = 0.05f;
    public float scatterPower = 1.5f;
    public float multiScatterBlend = 0.3f;
    public float ambientScatterStrength = 0.5f;

    public float nearThreshold = 1e5f;

    // === 时序超采样(方案 C) ===
    // 每帧只步进 1/(upscaleX*upscaleY) 的低清像素(按最优采样序列取格),其余像素由历史累积补齐。
    // 默认关闭 → 走现状路径,行为与之前逐字节一致。
    public bool useTemporalUpscale = false;   // 总开关
    public int upscaleX = 3;                  // 采样格网宽(N=upscaleX*upscaleY)
    public int upscaleY = 3;                  // 采样格网高

    // === 游戏自带云作为全球分布形状(方案 B) ===
    // 全部为新字段;旧 XML 无这些节点时保留默认值 → 行为与之前一致。
    public bool useStockCloudMap = false;   // 总开关:用游戏 Clouds cubemap 替代 PlanetMapTex 做全球分布
    public float stockMapStrength = 1f;     // 0..1 混合强度(eff=0 时与现状逐字节一致)
    public float stockMaskInfluence = 1f;   // 0..1 纬度/行星遮罩(A 通道)影响
    public float stockAlignSign = 1f;       // ±1 对齐旋转方向(镜像/方向反了翻号)
    public float stockAlignAngleOffset = 0f;// 度,一次性对齐微调角
    public int stockMapLayer = 3;           // 用游戏哪一层云作为分布:0=低云(R), 1=中云(G), 2=高云(B), 3=按层对应(默认)

    // === 轨道云(2D 壳着色)+ 过渡带交叉淡入(2026-08-27) ===
    // 高空(轨道)视角用廉价 2D 壳着色替代体积 raymarch;过渡带内与体积云按海拔交叉淡入。
    // 默认关闭(useOrbitClouds=false)→ orbitFade=0 → 行为与之前完全一致,零回归。
    // 旧 XML 无这些节点时保留默认值 → 行为与之前一致。
    public bool useOrbitClouds = false;              // 总开关:开启后按海拔在体积云/2D 轨道云间分派
    public float orbitTransitionStartAltitude = 25000f;  // 过渡带起点(米):低于此 → 纯体积云
    public float orbitTransitionEndAltitude = 100000f;   // 过渡带终点(米):高于此 → 纯 2D 轨道云(跳过体积 raymarch)
    public float orbitSampleAltitude = 0f;           // 2D 采样高度(0=自动:按层强度加权层高)
    public float orbitDensityBoost = 25f;            // 密度→不透明度放大(2D 单样本 vs 体积多步进累积)
    public float orbitBrightness = 0.7f;             // 2D 亮度缩放(与体积云 Additive 合成强度对齐)
    // KSA 2D 云参考改进(2026-08-27):它的 2D 云 = 烘焙颜色贴图 + 法线贴图 Lambertian + 半清。
    // 我们无预烘焙贴图,改用【程序化同源等效】:
    public float orbitReliefStrength = 1.5f;         // 密度梯度法线浮雕强度(KSA normal-map 等效;0=关)
    public float orbitDetailStrength = 0.4f;         // detail 噪声作为云内"纹理"明暗变化强度(0=关)
    public float orbitResolutionScale = 0.5f;        // 2D 轨道云渲染分辨率(相对屏幕;0.5=半清+双线性软化)

    [XmlIgnore]
    public Vector3 customWavelengths = new Vector3(680f, 550f, 450f);
    [XmlElement("customWavelengths")]
    public SerializableVector3 customWavelengthsSerializable
    {
        get => new SerializableVector3(customWavelengths);
        set => customWavelengths = value.ToVector3();
    }
    
    public float silverLiningIntensity = 1.0f;
    public float forwardScatteringBias = 0.85f;

    public float lowAltitudeThreshold = 10000f;
    public float midAltitudeThreshold = 50000f;
    public float highAltitudeThreshold = 150000f;
    public float minDistanceFactor = 0.1f;
    public float maxStepSizeMultiplier = 3f;
    public float minLightSamplesFactor = 0.3f;
    #endregion
    

    public static string GetConfigFolderPath(string planetName)
    {
        string folderPath = Application.persistentDataPath + CONFIG_FOLDER+planetName;
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }
        return folderPath;
    }
    
    public static string GetConfigPath(string planetName, string configName)
    {
        return Path.Combine(GetConfigFolderPath(planetName), configName + ".xml");
    }
    
    public static List<string> GetAllConfigNames(string planetName)
    {
        if (!Directory.Exists(GetConfigFolderPath(planetName)))
        {
            return new List<string>();
        }

        string[] files = Directory.GetFiles(GetConfigFolderPath(planetName), "*.xml");
        List<string> configNames = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        
        return configNames;
    }

    public void SaveToFile(string planetName,string configName)
    {
        try
        {
            string filePath = GetConfigPath(planetName,configName);
            string directory = Path.GetDirectoryName(filePath);
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XmlSerializer serializer = new XmlSerializer(typeof(CloudConfig));
            using (FileStream stream = new FileStream(filePath, FileMode.Create))
            {
                serializer.Serialize(stream, this);
            }
            Mod.LOG($"Cloud config '{configName}' saved to: {filePath}");
        }
        catch (System.Exception e)
        {
            Mod.LOG($"Failed to save cloud config '{configName}': {e.Message}");
        }
    }

    
    public static CloudConfig LoadFromFile(string planetName,string configName)
    {
        string filePath = GetConfigPath(planetName, configName);
        
        if (!File.Exists(filePath))
        {
            Mod.LOG($"Config file '{configName}' not found at {filePath}. Creating default config.");
            CloudConfig defaultConfig = CreateDefault();
            defaultConfig.SaveToFile(planetName,configName);
            return defaultConfig;
        }

        try
        {
            XmlSerializer serializer = new XmlSerializer(typeof(CloudConfig));
            using (FileStream stream = new FileStream(filePath, FileMode.Open))
            {
                CloudConfig config = serializer.Deserialize(stream) as CloudConfig;
                
                // Migration: Old configs only had Vector2 (x,y) for layer params.
                // Vector4 z/w default to 0. If all z/w are 0, it's an old config.
                // Set Layer3/4 strength to 0 so they contribute no density.
                if (config.layerStrengths.z == 0f && config.layerStrengths.w == 0f)
                {
                    Mod.LOG("Volken: Detected legacy config, disabling Layer3/4");
                    // Ensure spreads are safe (avoid division by zero in shader)
                    if (config.layerSpreads.z == 0f) config.layerSpreads.z = 1f;
                    if (config.layerSpreads.w == 0f) config.layerSpreads.w = 1f;
                }
                
                Mod.LOG($"Cloud config '{configName}' loaded from: {filePath}");
                return config;
            }
        }
        catch (System.Exception e)
        {
            Mod.LOG($"Failed to load cloud config '{configName}': {e.Message}. Using default config.");
            return CreateDefault();
        }
    }

    public static CloudConfig CreateDefault()
    {
        return new CloudConfig
        {
            enabled = true,
            density = 0.05f,
            absorption =0.584487557f,
            ambientLight = 0.18f,
            coverage = -0.25f,
            shapeScale = 10182.435f,
            detailScale = 25000.0f,
            detailStrength = 1f,
            phaseParameters = new Vector4(0.75f, -0.75f, 0.5f, 0.5f),
            offset = new Vector3(0.89573895f,0.9473378f,0.95080435f),
            windSpeed = 0.0f,
            windDirection = 0.0f,
            globalRotationAngular=0.1f,
            scatterStrength = 0.21468132f,
            atmoBlendFactor = 0.3628809f,
            cloudColor = Color.white,
            layerHeights = new Vector4(1671.05261f, 4717.10547f, 0f, 0f),
            layerSpreads = new Vector4(670.083f, 5000f, 1f, 1f),
            layerStrengths = new Vector4(0.300f, 2f, 0f, 0f),
            maxCloudHeight = 11238.2275f,
            resolutionScale = 0.5001385f,
            stepSize = 193.29982f,
            stepSizeFalloff = 0.67f,
            numLightSamplePoints = 6,
            lightMarchDistance = 12000f,
            blueNoiseStrength = 0.0f,
            depthThreshold = 1f,
            historyBlend = 0.0f,
            historyDepthThreshold = 0.05f,
            scatterPower = 1.5f,
            multiScatterBlend = 0.3f,
            ambientScatterStrength = 0.62f,
            customWavelengths = new Vector3(680f, 550f, 450f),
            silverLiningIntensity = 3.0f,
            forwardScatteringBias = 0.65f,
            nearThreshold = 100000f,
            useOrbitClouds = false,
            orbitTransitionStartAltitude = 25000f,
            orbitTransitionEndAltitude = 100000f,
            orbitSampleAltitude = 0f,
            orbitDensityBoost = 25f,
            orbitBrightness = 0.7f,
            orbitReliefStrength = 1.5f,
            orbitDetailStrength = 0.4f,
            orbitResolutionScale = 0.5f,
            /*
            lowAltitudeThreshold = 10000f,
            midAltitudeThreshold = 50000f,
            highAltitudeThreshold = 150000,
            minDistanceFactor = 0.1f,
            maxStepSizeMultiplier = 3f,
            minLightSamplesFactor = 0.3f
            */
        };
    }
    public static CloudConfig CreateAnotherDefault()
    {
        return new CloudConfig
        {
            enabled = true,
            density = 0.00595869171f,
            absorption = 0.279352337f,
            ambientLight = 0.0310036615f,
            coverage = 0.272746682f,
            shapeScale = 38702.7734f,
            detailScale = 7866.181f,
            detailStrength = 0.7400386f,
            phaseParameters = new Vector4(0.75f, -0.75f, 0.5f, 0.5f),
            offset = new Vector3(0.015371074f, 0.131764963f, 0.0238080341f),
            windSpeed = 0.001130818f,
            windDirection = 0.0f,
            globalRotationAngular = 0.07383323f,
            scatterStrength = 0.0639246f,
            atmoBlendFactor = 4.441673f,
            cloudColor = new Color(1f, 1f, 1f, 1f),
            layerHeights = new Vector4(17000f, 20000f, 0f, 0f),
            layerSpreads = new Vector4(5000f, 5000f, 1f, 1f),
            layerStrengths = new Vector4(2f, 2f, 0f, 0f),
            maxCloudHeight = 25000f,
            resolutionScale = 0.75203526f,
            stepSize = 1865.18164f,
            stepSizeFalloff = 1.52126586f,
            numLightSamplePoints = 5,
            lightMarchDistance = 20000f,
            blueNoiseStrength = 0.0f,
            depthThreshold = 0.12f,
            historyBlend = 0.0f,
            historyDepthThreshold = 0.05f,
            scatterPower = 1.0f,
            multiScatterBlend = 0.0f,
            ambientScatterStrength = 0.0f,
            customWavelengths = new Vector3(680f, 550f, 450f),
            silverLiningIntensity = 0.0f,
            forwardScatteringBias = 0.0f,
            nearThreshold = 0.0f,
        };
    }

    public CloudConfig Clone()
    {
        return new CloudConfig
        {
            compositeMode = this.compositeMode,
            enabled = this.enabled,
            density = this.density,
            absorption = this.absorption,
            ambientLight = this.ambientLight,
            coverage = this.coverage,
            shapeScale = this.shapeScale,
            detailScale = this.detailScale,
            detailStrength = this.detailStrength,
            phaseParameters = this.phaseParameters,
            offset = this.offset,
            windSpeed = this.windSpeed,
            windDirection = this.windDirection,
            globalRotationAngular = this.globalRotationAngular,
            scatterStrength = this.scatterStrength,
            atmoBlendFactor = this.atmoBlendFactor,
            cloudColor = this.cloudColor,
            layerHeights = this.layerHeights,
            layerSpreads = this.layerSpreads,
            layerStrengths = this.layerStrengths,
            maxCloudHeight = this.maxCloudHeight,
            resolutionScale = this.resolutionScale,
            stepSize = this.stepSize,
            stepSizeFalloff = this.stepSizeFalloff,
            numLightSamplePoints = this.numLightSamplePoints,
            lightMarchDistance = this.lightMarchDistance,
            blueNoiseStrength = this.blueNoiseStrength,
            depthThreshold = this.depthThreshold,
            historyBlend = this.historyBlend,
            historyDepthThreshold = this.historyDepthThreshold,
            scatterPower = this.scatterPower,
            multiScatterBlend = this.multiScatterBlend,
            ambientScatterStrength = this.ambientScatterStrength,
            customWavelengths = this.customWavelengths,
            silverLiningIntensity = this.silverLiningIntensity,
            forwardScatteringBias = this.forwardScatteringBias,
            nearThreshold = this.nearThreshold,
            useTemporalUpscale = this.useTemporalUpscale,
            upscaleX = this.upscaleX,
            upscaleY = this.upscaleY,
            useStockCloudMap = this.useStockCloudMap,
            stockMapStrength = this.stockMapStrength,
            stockMaskInfluence = this.stockMaskInfluence,
            stockAlignSign = this.stockAlignSign,
            stockAlignAngleOffset = this.stockAlignAngleOffset,
            stockMapLayer = this.stockMapLayer,
            useOrbitClouds = this.useOrbitClouds,
            orbitTransitionStartAltitude = this.orbitTransitionStartAltitude,
            orbitTransitionEndAltitude = this.orbitTransitionEndAltitude,
            orbitSampleAltitude = this.orbitSampleAltitude,
            orbitDensityBoost = this.orbitDensityBoost,
            orbitBrightness = this.orbitBrightness,
            orbitReliefStrength = this.orbitReliefStrength,
            orbitDetailStrength = this.orbitDetailStrength,
            orbitResolutionScale = this.orbitResolutionScale,
            /*
            lowAltitudeThreshold = this.lowAltitudeThreshold,
            midAltitudeThreshold = this.midAltitudeThreshold,
            highAltitudeThreshold = this.highAltitudeThreshold,
            minDistanceFactor = this.minDistanceFactor,
            maxStepSizeMultiplier = this.maxStepSizeMultiplier,
            minLightSamplesFactor = this.minLightSamplesFactor,
            */
        };
    }
    
    public void CopyFrom(CloudConfig source)
    {
        this.compositeMode = source.compositeMode;
        this.enabled = source.enabled;
        this.density = source.density;
        this.absorption = source.absorption;
        this.ambientLight = source.ambientLight;
        this.coverage = source.coverage;
        this.shapeScale = source.shapeScale;
        this.detailScale = source.detailScale;
        this.detailStrength = source.detailStrength;
        this.phaseParameters = source.phaseParameters;
        this.offset = source.offset;
        this.windSpeed = source.windSpeed;
        this.windDirection = source.windDirection;
        this.globalRotationAngular= source.globalRotationAngular;
        this.scatterStrength = source.scatterStrength;
        this.atmoBlendFactor = source.atmoBlendFactor;
        this.cloudColor = source.cloudColor;
        this.layerHeights = source.layerHeights;
        this.layerSpreads = source.layerSpreads;
        this.layerStrengths = source.layerStrengths;
        this.maxCloudHeight = source.maxCloudHeight;
        this.resolutionScale = source.resolutionScale;
        this.stepSize = source.stepSize;
        this.stepSizeFalloff = source.stepSizeFalloff;
        this.numLightSamplePoints = source.numLightSamplePoints;
        this.lightMarchDistance = source.lightMarchDistance;
        this.blueNoiseStrength = source.blueNoiseStrength;
        this.depthThreshold = source.depthThreshold;
        this.historyBlend = source.historyBlend;
        this.historyDepthThreshold = source.historyDepthThreshold;
        this.scatterPower = source.scatterPower;
        this.multiScatterBlend = source.multiScatterBlend;
        this.ambientScatterStrength = source.ambientScatterStrength;
        this.customWavelengths = source.customWavelengths;
        this.silverLiningIntensity = source.silverLiningIntensity;
        this.forwardScatteringBias = source.forwardScatteringBias;
        this.nearThreshold= source.nearThreshold;
        this.useTemporalUpscale = source.useTemporalUpscale;
        this.upscaleX = source.upscaleX;
        this.upscaleY = source.upscaleY;
        this.useStockCloudMap = source.useStockCloudMap;
        this.stockMapStrength = source.stockMapStrength;
        this.stockMaskInfluence = source.stockMaskInfluence;
        this.stockAlignSign = source.stockAlignSign;
        this.stockAlignAngleOffset = source.stockAlignAngleOffset;
        this.stockMapLayer = source.stockMapLayer;
        this.useOrbitClouds = source.useOrbitClouds;
        this.orbitTransitionStartAltitude = source.orbitTransitionStartAltitude;
        this.orbitTransitionEndAltitude = source.orbitTransitionEndAltitude;
        this.orbitSampleAltitude = source.orbitSampleAltitude;
        this.orbitDensityBoost = source.orbitDensityBoost;
        this.orbitBrightness = source.orbitBrightness;
        this.orbitReliefStrength = source.orbitReliefStrength;
        this.orbitDetailStrength = source.orbitDetailStrength;
        this.orbitResolutionScale = source.orbitResolutionScale;
        /*
        this.lowAltitudeThreshold= source.lowAltitudeThreshold;
        this.midAltitudeThreshold= source.midAltitudeThreshold;
        this.highAltitudeThreshold= source.highAltitudeThreshold;
        this.minDistanceFactor= source.minDistanceFactor;
        this.maxStepSizeMultiplier= source.maxStepSizeMultiplier;
        this.minLightSamplesFactor= source.minLightSamplesFactor;*/
    }

}
