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

        /// <summary>
        /// 这颗行星用哪套**天气预设**。与 <see cref="CloudConfigName"/> **完全独立** ——
        /// 云与天气各选各的名字、各自新建保存,互不干扰(就像两个云层各自的预设)。
        /// 文件:<c>UserData/VolkenWeatherConfig/{行星}/{WeatherConfigName}.xml</c>。
        /// 空 = 用 <see cref="VolkenWeatherConfig.DefaultConfigName"/>。
        /// </summary>
        [XmlAttribute]
        public string WeatherConfigName;

        /// <summary>
        /// 【旧格式,仅供迁移】天气参数曾经内联在这条记录里。
        /// 现在天气按预设名存成独立文件(<c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>,
        /// 预设名与云层同一个),见 <see cref="VolkenWeatherConfig.SaveToFile"/>。
        ///
        /// 这个字段**故意不给初始值**:XmlSerializer 不会为 null 元素写节点,
        /// 所以迁移完把它置 null 之后,新存档里不会再冒出 <c>&lt;Weather&gt;</c> 节点。
        /// </summary>
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
        /// <summary>配置文件夹(相对 persistentDataPath)。云预设与天气共用这一份清单。</summary>
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

                    // 迁移:旧格式把天气内联在记录里(<Weather> 节点)。新格式按**天气预设名**存独立文件,
                    // 所以把旧内联天气写到"{行星}/{天气预设}.xml" —— 只在目标文件还不存在时写
                    // (不覆盖更新的内容),然后丢掉内联节点(XmlSerializer 不写 null,不会再冒出来)。
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
            newP.SaveToFile(Volken.Core.VolkenMod.CloudConfigListName);
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
            this.SaveToFile(Volken.Core.VolkenMod.CloudConfigListName);
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
            this.SaveToFile(Volken.Core.VolkenMod.CloudConfigListName);
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
            this.SaveToFile(Volken.Core.VolkenMod.CloudConfigListName);
        }
    }
}