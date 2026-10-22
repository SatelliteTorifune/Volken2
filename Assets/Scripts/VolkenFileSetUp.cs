using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>把随包默认预设复制到 <c>UserData</c>;**首次启动(目录都还不存在)也必须能跑完**,
    /// 单份复制失败只记日志,不能让 <c>OnModLoaded</c> 抛异常使游戏报告"加载 mod 失败"。</summary>
    public static class VolkenFileSetUp
    {
        /// <summary>随包默认预设的工程内资源目录(改路径需同步资源包)。</summary>
        private const string AssetRoot = "Assets/Resources/DefaultConfigs/";

        public static void CreateDefaultConfig()
        {
            //weather
            FileSetUp("Fog-Heavy", "VolkenWeatherConfig", "Droo");
            FileSetUp("Fog-Heavy", "VolkenWeatherConfig", "Earth");

            FileSetUp("Fog-Light", "VolkenWeatherConfig", "Droo");
            FileSetUp("Fog-Light", "VolkenWeatherConfig", "Earth");

            FileSetUp("Fog-ReallyHeay", "VolkenWeatherConfig", "Droo");
            FileSetUp("Fog-ReallyHeay", "VolkenWeatherConfig", "Earth");

            FileSetUp("LightRain", "VolkenWeatherConfig", "Droo");
            FileSetUp("LightRain", "VolkenWeatherConfig", "Earth");

            FileSetUp("MidRain", "VolkenWeatherConfig", "Droo");
            FileSetUp("MidRain", "VolkenWeatherConfig", "Earth");

            FileSetUp("Stormy", "VolkenWeatherConfig", "Droo");
            FileSetUp("Stormy", "VolkenWeatherConfig", "Earth");

            FileSetUp("Snow(maybe)", "VolkenWeatherConfig", "Droo");
            FileSetUp("Snow(maybe)", "VolkenWeatherConfig", "Earth");

            //cloud
            FileSetUp("Atmospheric", "VolkenConfig", "Droo");
            FileSetUp("Atmospheric", "VolkenConfig", "Earth");

            FileSetUp("NewConfig1", "VolkenConfig", "Droo");
            FileSetUp("NewConfig1", "VolkenConfig", "Earth");

            FileSetUp("NewConfig2", "VolkenConfig", "Droo");
            FileSetUp("NewConfig2", "VolkenConfig", "Earth");

            FileSetUp("Rain", "VolkenConfig", "Droo");
            FileSetUp("Rain", "VolkenConfig", "Earth");

            FileSetUp("Shadow", "VolkenConfig", "Droo");
            FileSetUp("Shadow", "VolkenConfig", "Earth");

            FileSetUp("Cylero", "VolkenConfig", "Cylero");
            FileSetUp("Cylero", "VolkenConfig", "Mars");

            //planetConfig(云与天气共用清单)
            CopyDefaultConfig("PlanetConfigList.xml", Path.Combine(Application.persistentDataPath, "UserData", "VolkenConfig"), "PlanetConfigList.xml");
        }

        private static void FileSetUp(string fileName, string configType, string planet)
        {
            string folder = Path.Combine(Application.persistentDataPath, "UserData", configType, planet);
            CopyDefaultConfig(AssetRoot + configType + "/" + fileName + ".xml", folder, fileName + ".xml");
        }

        private static void CopyDefaultConfig(string assetPath, string targetFolder, string fileName)
        {
            try
            {
                var asset = Mod.Instance.ResourceLoader.LoadAsset<TextAsset>(assetPath);
                if (asset == null)
                {
                    Mod.Log("Default config asset '{0}' not found (rebuild the asset bundle?)", assetPath);
                    return;
                }

                string targetPath = Path.Combine(targetFolder, fileName);
                if (File.Exists(targetPath)) return;

                Directory.CreateDirectory(targetFolder);
                File.WriteAllText(targetPath, asset.text, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Mod.Log("Failed to create default config '{0}': {1}", fileName, e.Message);
            }
        }
    }
}
