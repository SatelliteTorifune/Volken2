using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Assets.Scripts;
using UnityEngine;
using Volken.Clouds;

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

        /// <summary>清单文件名(不带扩展名)。持有与读写它的是 <see cref="VolumetricClouds"/>。</summary>
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

        /// <summary>先写临时文件再原子替换;失败返回 false 并保留旧文件。**绝不先截断原文件**。</summary>
        public bool SaveToFile(string configName)
        {
            if (!ConfigFileNames.IsValid(configName))
            {
                Mod.Log("Planet config save rejected: invalid name '{0}'", configName);
                return false;
            }

            string temporaryPath = null;
            try
            {
                string filePath = GetConfigPath(configName);
                string directory = Path.GetDirectoryName(filePath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                XmlSerializer serializer = new XmlSerializer(typeof(PlanetConfigList));
                temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, this);
                    stream.Flush(true);
                }
                if (File.Exists(filePath)) File.Replace(temporaryPath, filePath, null);
                else File.Move(temporaryPath, filePath);
                Mod.Log($"Planet config '{configName}' saved to: {filePath}");
                return true;
            }
            catch (Exception e)
            {
                Mod.Log($"Saving planet config '{configName}' failed: {e}");
                return false;
            }
            finally
            {
                if (temporaryPath != null && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception e) { Mod.Log($"Planet config temporary file cleanup failed: {e.Message}"); }
                }
            }
        }

        public static PlanetConfigList LoadFromFile(string configName)
        {
            string filePath;
            try
            {
                filePath = GetConfigPath(configName);
            }
            catch (Exception e)
            {
                Mod.Log($"Failed to resolve planet config path '{configName}': {e.Message}");
                return new PlanetConfigList();
            }

            // 缺失 = 首次安装:创建默认清单并落盘(这是唯一会写盘的读取路径)
            if (!File.Exists(filePath))
            {
                return CreateDefault();
            }

            PlanetConfigList config;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(PlanetConfigList));
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    config = serializer.Deserialize(stream) as PlanetConfigList;
                }
            }
            catch (Exception e)
            {
                // 坏文件只隔离、不覆盖:清空玩家的行星绑定比丢一份读不了的清单更糟
                Mod.Log($"Failed to load planet config '{configName}': {e.Message}. Quarantining the file; using empty in-memory list.");
                ConfigFileNames.TryQuarantine(filePath);
                return new PlanetConfigList();
            }

            if (config == null)
            {
                Mod.Log($"Planet config '{configName}' deserialized to null. Quarantining the file.");
                ConfigFileNames.TryQuarantine(filePath);
                return new PlanetConfigList();
            }
            if (config.configList == null) config.configList = new List<PlanetConfig>();

            // 迁移旧内联 <Weather> 节点:失败只记日志,不能把整份清单判成损坏
            MigrateLegacyWeather(config, configName);
            return config;
        }

        private static void MigrateLegacyWeather(PlanetConfigList config, string configName)
        {
            foreach (var pc in config.configList)
            {
                if (pc?.LegacyWeather == null) continue;

                try
                {
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
                catch (Exception e)
                {
                    Mod.Log($"Volken: failed to migrate inline weather of '{configName}': {e.Message}");
                }
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