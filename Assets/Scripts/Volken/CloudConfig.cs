using System;
using UnityEngine;
using System.Xml.Serialization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Assets.Scripts;
using Application = UnityEngine.Application;

[Serializable]
public class CloudConfig
{
    public const string CONFIG_FOLDER = "/UserData/VolkenConfig/";
    private const string DEFAULT_CONFIG_NAME = "Default";


    #region parameter
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
    public Vector2 layerHeights;
    [XmlElement("layerHeights")]
    public SerializableVector2 layerHeightsSerializable
    {
        get => new SerializableVector2(layerHeights);
        set => layerHeights = value.ToVector2();
    }
    
    [XmlIgnore]
    public Vector2 layerSpreads;
    [XmlElement("layerSpreads")]
    public SerializableVector2 layerSpreadsSerializable
    {
        get => new SerializableVector2(layerSpreads);
        set => layerSpreads = value.ToVector2();
    }
    
    [XmlIgnore]
    public Vector2 layerStrengths;
    [XmlElement("layerStrengths")]
    public SerializableVector2 layerStrengthsSerializable
    {
        get => new SerializableVector2(layerStrengths);
        set => layerStrengths = value.ToVector2();
    }
    
    public float maxCloudHeight;
    public float resolutionScale;
    public float stepSize;
    public float stepSizeFalloff;
    public int numLightSamplePoints;
    public float blueNoiseStrength;
    public float depthThreshold;
    public float historyBlend;
    public float historyDepthThreshold = 0.05f;
    public float scatterPower = 1.5f;
    public float multiScatterBlend = 0.3f;
    public float ambientScatterStrength = 0.5f;

    public float nearThreshold = 1e5f;
    
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
            layerHeights = new Vector2(1671.05261f, 4717.10547f),
            layerSpreads = new Vector2(670.083f, 5000f),
            layerStrengths = new Vector2(0.300f, 2f),
            maxCloudHeight = 11238.2275f,
            resolutionScale = 0.5001385f,
            stepSize = 193.29982f,
            stepSizeFalloff = 0.67f,
            numLightSamplePoints = 50,
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
            layerHeights = new Vector2(17000f, 20000f),
            layerSpreads = new Vector2(5000f, 5000f),
            layerStrengths = new Vector2(2f, 2f),
            maxCloudHeight = 25000f,
            resolutionScale = 0.75203526f,
            stepSize = 1865.18164f,
            stepSizeFalloff = 1.52126586f,
            numLightSamplePoints = 5,
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
        /*
        this.lowAltitudeThreshold= source.lowAltitudeThreshold;
        this.midAltitudeThreshold= source.midAltitudeThreshold;
        this.highAltitudeThreshold= source.highAltitudeThreshold;
        this.minDistanceFactor= source.minDistanceFactor;
        this.maxStepSizeMultiplier= source.maxStepSizeMultiplier;
        this.minLightSamplesFactor= source.minLightSamplesFactor;*/
    }

}
