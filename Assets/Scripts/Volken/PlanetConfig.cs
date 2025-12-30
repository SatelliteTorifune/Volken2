using System;
using System.Xml.Serialization;
using System.IO;
using System.Collections.Generic;


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
    
}
[Serializable]
public class PlanetConfigList
{
    public const string CONFIG_FOLDER = "/UserData/VolkenConfig/";
    [XmlArray("Configs")]
    public List<PlanetConfig> configList = new List<PlanetConfig>();
    
    public static string GetConfigFolderPath()
    {
        string folderPath = CONFIG_FOLDER;
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
        }
        catch (System.Exception e)
        {
        }
    }
    public static PlanetConfigList LoadFromFile(string configName)
    {
        string filePath = GetConfigPath(configName);
        
        if (!File.Exists(filePath))
        {
            PlanetConfigList defaultConfig = CreateDefault();
            defaultConfig.SaveToFile(configName);
            return defaultConfig;
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
    
    

    public string GetConfigName(PlanetConfigList planetConfigList,string planetName)
    {
        foreach (var config in planetConfigList.configList)
        {
            if (config.PlanetName == planetName)
            {
              return config.CloudConfigName;  
            }
        }
        return "Not Found";
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