using System.IO;
using System.Text;
using UnityEngine;

namespace Assets.Scripts
{
    public static class VolkenFileSetUp
    {
        public static void CreateDefaultConfig()
        {
            WeatherConfigSetup();
            CloudConfigSetup();
            PlanetConfigSetup();
        }

        //天气预设
        private static void WeatherConfigSetup()
        {

        }

        //云的预设
        private static void CloudConfigSetup()
        {


        }

        //默认行星配置
        private static void PlanetConfigSetup()
        {

        }

        // !?屎?!
        private static void FileSetUp(string FileName, string Directory)
        {
            var asset=Mod.Instance.ResourceLoader.LoadAsset<TextAsset>("Assets/Resources/DefaultConfigs/" + FileName + ".xml");
            if (asset != null)
            {
                var targetPath = Path.Combine(Path.Combine(Application.persistentDataPath, "UserData", "VolkenConfig"), FileName + ".xml");
                if (!File.Exists(targetPath))
                {
                    File.WriteAllText(targetPath, asset.text, Encoding.UTF8);
                }
            }
        }
    }
}