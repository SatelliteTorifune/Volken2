using System;
using System.Xml.Serialization;
using System.IO;
using System.Collections.Generic;
using Assets.Scripts;
using Application = UnityEngine.Application;

[Serializable]
public class PlanetConfig
{
    [XmlAttribute]
    public string PlanetName;
    [XmlAttribute]
    public string CloudConfigName;
    [XmlAttribute]
    public string ExtraCloudConfigName;  // Layer 1 (Extra) 的配置名

    public PlanetConfig(string planetName, string cloudConfigName, string extraCloudConfigName = null)
    {
        PlanetName = planetName;
        CloudConfigName = cloudConfigName;
        ExtraCloudConfigName = extraCloudConfigName;
    }
    public PlanetConfig()
    {
        
    }

    /// <summary>
    /// 根据层索引获取或设置配置名。layerIndex 0=Main, 1=Extra1, ...
    /// </summary>
    public string GetConfigName(int layerIndex)
    {
        return layerIndex == 0 ? CloudConfigName : ExtraCloudConfigName;
    }

    public void SetConfigName(int layerIndex, string configName)
    {
        if (layerIndex == 0) CloudConfigName = configName;
        else ExtraCloudConfigName = configName;
    }
}
[Serializable]
public class PlanetConfigList
{
    
    public const string CONFIG_FOLDER = "/UserData/VolkenConfig/";
    [XmlArray("Configs")]
    public List<PlanetConfig> configList = new List<PlanetConfig>();
    
    public static string GetConfigFolderPath()
    {
        string folderPath = Application.persistentDataPath + CONFIG_FOLDER;
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }
        return folderPath;
    }
    public static string GetConfigPath(string configName)
    {
        return Path.Combine(GetConfigFolderPath(), configName + ".xml");
    }
    public void SaveToFile(string configName)
    {
        try
        {
            string filePath = GetConfigPath(configName);
            string directory = Path.GetDirectoryName(filePath);
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XmlSerializer serializer = new XmlSerializer(typeof(PlanetConfigList));
            using (FileStream stream = new FileStream(filePath, FileMode.Create))
            {
                serializer.Serialize(stream, this);
            }
            Mod.Log($"Planet config '{configName}' saved to: {filePath}");
        }
        catch (System.Exception e)
        {
            Mod.Log("Saving failed+"+e);
        }
    }
    public static PlanetConfigList LoadFromFile(string configName)
    {
        string filePath = GetConfigPath(configName);
        
        if (!File.Exists(filePath))
        {
            return CreateDefault();
        }

        try
        {
            XmlSerializer serializer = new XmlSerializer(typeof(PlanetConfigList));
            using (FileStream stream = new FileStream(filePath, FileMode.Open))
            {
                PlanetConfigList config = serializer.Deserialize(stream) as PlanetConfigList;
                return config;
            }
        }
        catch (System.Exception e)
        {
            return CreateDefault();
        }
    }
    public static PlanetConfigList CreateDefault()
    {
        PlanetConfigList newP = new PlanetConfigList();
        newP.SaveToFile(Volken.CloudConfigListName);
        return newP ;
    }
    public string GetConfigName(string planetName, int layerIndex = 0)
    {
        foreach (var planetConfig in configList)
        {
            if (planetConfig.PlanetName == planetName)
            {
                var name = planetConfig.GetConfigName(layerIndex);
                return string.IsNullOrEmpty(name) ? "Default" : name;
            }
        }

        return "Default";
    }


    public bool ExistsInConfig(string planetName)
    {
        foreach (var pc in configList)
        {
            if (pc.PlanetName == planetName)
            {
                return true;
            }
        }

        return false;
    }

    public void AddConfig(string planetName, string ConfigName, string extraConfigName = null)
    {
        PlanetConfig cfg = new PlanetConfig(planetName, ConfigName, extraConfigName);
        configList.Add(cfg);
        this.SaveToFile(Volken.CloudConfigListName);
    }

    public void SetConfig(string planetName, string ConfigName, int layerIndex = 0)
    {
        foreach (PlanetConfig cfg in configList)
        {
            if (cfg.PlanetName == planetName)
            {
                cfg.SetConfigName(layerIndex, ConfigName);
            }
        }
        this.SaveToFile(Volken.CloudConfigListName);
    }
}