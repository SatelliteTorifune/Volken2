using System;
using System.Xml.Serialization;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Assets.Scripts;

[Serializable]
public class PlanetConfig
{
    [XmlAttribute]
    public string PlanetName;
    [XmlAttribute]
    public string CloudConfigName;

    public PlanetConfig(string planetName,string cloudConfigName)
    {
        PlanetName = planetName;
        CloudConfigName = cloudConfigName;
    }
    public PlanetConfig()
    {
        
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
            Mod.LOG($"Not Cloud config '{configName}' saved to: {filePath}");
        }
        catch (System.Exception e)
        {
            Mod.LOG("SAving failed+"+e);
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
        var cfg = new PlanetConfig("Droo","Default");
        newP.configList.Add(cfg);
        newP.SaveToFile(Volken.CloudConfigListName);
        return newP ;
    }
    public string GetConfigName(string planetName)
    {
        Mod.LOG($"Looking for config for planet: {planetName}");
        Mod.LOG($"Available configs: {configList.Count}");
    
        foreach (var planetConfig in configList)
        {
            Mod.LOG($"Found planet config - Name: {planetConfig.PlanetName}, Config: {planetConfig.CloudConfigName}");
            if (planetConfig.PlanetName == planetName)
            {
                Mod.LOG($"Match found! Returning config: {planetConfig.CloudConfigName}");
                return planetConfig.CloudConfigName;
            }
        }
    
        Mod.LOG($"No match found for {planetName}, returning Default");
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

    public void AddConfig(string planetName, string ConfigName)
    {
        PlanetConfig cfg=new PlanetConfig(planetName,ConfigName);
        configList.Add(cfg);
        this.SaveToFile(Volken.CloudConfigListName);
    }

    public void SetConfig(string planetName, string ConfigName)
    {
        foreach (PlanetConfig cfg in configList)
        {
            if (cfg.PlanetName == planetName)
            {
                cfg.CloudConfigName = ConfigName;
            }
        }
        this.SaveToFile(Volken.CloudConfigListName);
    }
}