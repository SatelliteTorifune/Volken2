using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Assets.Scripts;
using UnityEngine;

namespace Volken.Core
{
    using Volken.Weather;

    /// <summary>一颗行星的配置记录:云层用哪套预设 + 天气用哪套预设(**同一个名字**)。</summary>
    [Serializable]
    public class PlanetConfig
    {
        [XmlAttribute]
        public string PlanetName;

        [XmlAttribute]
        public string CloudConfigName;

        [XmlAttribute]
        public string ExtraCloudConfigName;  // Layer 1 (Extra) 的配置名

        /// <summary>本行星的天气预设名(与 <see cref="CloudConfigName"/> 完全独立)。空 = Default。</summary>
        [XmlAttribute]
        public string WeatherConfigName;

        /// <summary>【仅用于迁移旧内联格式】故意不给初始值 —— XmlSerializer 不为 null 写节点,置 null 后新存档不再冒出 &lt;Weather&gt;。</summary>
        [XmlElement("Weather")]
        public VolkenWeatherConfig LegacyWeather;

        public PlanetConfig(string planetName, string cloudConfigName, string extraCloudConfigName = null)
        {
            PlanetName = planetName;
            CloudConfigName = cloudConfigName;
            ExtraCloudConfigName = extraCloudConfigName;
        }

        public PlanetConfig()
        {
        }

        /// <summary>按层索引取/设配置名;0 = Main,1 = Extra1。</summary>
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
        /// <summary>配置文件夹(相对 persistentDataPath)。云预设与天气共用这一份清单。</summary>
        public const string CONFIG_FOLDER = "/UserData/VolkenConfig/";

        /// <summary>清单文件名(不带扩展名)。持有与读写它的是 <see cref="Volken.Clouds.VolkenClouds"/>。</summary>
        public const string DefaultListName = "PlanetConfigList";

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
            catch (Exception e)
            {
                Mod.Log("Saving failed+" + e);
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
                    if (config == null) return CreateDefault();
                    if (config.configList == null) config.configList = new List<PlanetConfig>();

                    // 迁移旧内联 <Weather> 节点:写成独立预设文件(不覆盖已存在的),然后置 null。
                    foreach (var pc in config.configList)
                    {
                        if (pc?.LegacyWeather == null) continue;

                        string presetName = string.IsNullOrEmpty(pc.WeatherConfigName)
                            ? VolkenWeatherConfig.DefaultConfigName
                            : pc.WeatherConfigName;
                        pc.LegacyWeather.EnsureSections();
                        pc.LegacyWeather.ClampAll();

                        if (!File.Exists(VolkenWeatherConfig.GetConfigPath(pc.PlanetName, presetName)))
                        {
                            pc.LegacyWeather.SaveToFile(pc.PlanetName, presetName);
                            Mod.Log($"Volken: migrated inline weather of '{pc.PlanetName}' -> weather preset '{presetName}'");
                        }
                        pc.WeatherConfigName = presetName;
                        pc.LegacyWeather = null;
                    }
                    return config;
                }
            }
            catch (Exception e)
            {
                Mod.Log($"Failed to load planet config '{configName}': {e.Message}. Using default.");
                return CreateDefault();
            }
        }

        public static PlanetConfigList CreateDefault()
        {
            PlanetConfigList newP = new PlanetConfigList();
            newP.SaveToFile(DefaultListName);
            return newP;
        }

        public string GetConfigName(string planetName, int layerIndex = 0)
        {
            foreach (var planetConfig in configList)
            {
                if (planetConfig != null && planetConfig.PlanetName == planetName)
                {
                    var name = planetConfig.GetConfigName(layerIndex);
                    return string.IsNullOrEmpty(name) ? "Default" : name;
                }
            }

            return "Default";
        }

        /// <summary>取这颗行星的**天气预设名**(独立于云层预设),没登记就用 Default。</summary>
        public string GetWeatherConfigName(string planetName)
        {
            var pc = GetPlanetConfig(planetName);
            var name = pc?.WeatherConfigName;
            return string.IsNullOrEmpty(name) ? VolkenWeatherConfig.DefaultConfigName : name;
        }

        /// <summary>登记这颗行星使用的**天气预设名**并落盘(只动天气这一项,不碰云层预设名)。</summary>
        public void SetWeatherConfig(string planetName, string configName)
        {
            if (string.IsNullOrEmpty(planetName) || string.IsNullOrWhiteSpace(configName)) return;

            var pc = GetPlanetConfig(planetName);
            if (pc == null)
            {
                // 只登记天气名:云层那一栏留空(两者独立,不去替云层选预设)
                pc = new PlanetConfig(planetName, null);
                configList.Add(pc);
            }
            pc.WeatherConfigName = configName;
            this.SaveToFile(DefaultListName);
        }

        public bool ExistsInConfig(string planetName)
        {
            foreach (var pc in configList)
            {
                if (pc != null && pc.PlanetName == planetName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取这颗行星的整条记录(没有则返回 null)。云与天气的预设名都记在这条记录上。</summary>
        public PlanetConfig GetPlanetConfig(string planetName)
        {
            foreach (var pc in configList)
            {
                if (pc != null && pc.PlanetName == planetName) return pc;
            }
            return null;
        }

        public void AddConfig(string planetName, string ConfigName, string extraConfigName = null)
        {
            var existing = GetPlanetConfig(planetName);
            if (existing != null)
            {
                // 已有记录 → 更新预设名即可(本记录只保存"用哪套预设名"的关系;
                // 云与天气的参数本体都在各自的预设文件里,不在这条记录上)
                existing.SetConfigName(0, ConfigName);
                if (extraConfigName != null) existing.SetConfigName(1, extraConfigName);
            }
            else
            {
                configList.Add(new PlanetConfig(planetName, ConfigName, extraConfigName));
            }
            this.SaveToFile(DefaultListName);
        }

        public void SetConfig(string planetName, string ConfigName, int layerIndex = 0)
        {
            foreach (PlanetConfig cfg in configList)
            {
                if (cfg != null && cfg.PlanetName == planetName)
                {
                    cfg.SetConfigName(layerIndex, ConfigName);
                }
            }
            this.SaveToFile(DefaultListName);
        }
    }
}