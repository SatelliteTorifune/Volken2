using System.IO;
using Assets.Packages.DevConsole;
using Assets.Scripts.Flight.UI;
using HarmonyLib;
using ModApi.Scenes.Events;
using UnityEditor;

namespace Assets.Scripts
{
    using Volken.Core;
    using Volken.Clouds;
    using Volken.Weather;
    using Volken.Water;
    using System;
    using UnityEngine;

    /// <summary>本 mod 的 GameMod 单例。</summary>
    public class Mod : ModApi.Mods.GameMod
    {

        private Mod() : base()
        {
        }

        public int frontRenderQueue = 3000;
        public int backRenderQueue = 3000;

        public static Mod Instance { get; } = GetModInstance<Mod>();

        private GameObject VolkenUI;
        public GameObject ForceSettingScriptLoadGameObject;

        /// <summary>mod 版本(取自 ModInfo.Version)。</summary>
        public Version ModVersion { get; private set; }

        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();

            if (ModSettings.Instance.RestoreDefaultConfig)
            {
                VolkenFileSetUp.CreateDefaultConfig();
            }
            SetUpGo<VolkenUserInterface>(ref VolkenUI,"VolkenUI");
            SetUpGo<ForceSetting>(ref ForceSettingScriptLoadGameObject,"ForceSettingObject");

            // 初始化顺序是硬要求:Clouds 负责解析行星环境并广播 PlanetChanged,
            // 必须先于 VolkenWeather(后者订阅它)。
            VolumetricClouds.Initialize();
            VolkenWeather.Initialize();
            // 注:性能剖析器(VolkenProfiler)不再由正式代码启动 —— 见 VolkenTests/TestsBootstrap.cs 自发注册
            RegisterCommands();

            Game.Instance.Settings.Game.Flight.GroundClouds.Value = true;

            // 本地版本 = ModInfo.Version(System.Version,如 0.6)
            this.ModVersion = this.ModInfo.Version;

            // 更新检查(本地模式:LatestVersionUrl 未配置时仅打日志,不弹窗)
            new ModUpdater().CheckForUpdate();

        }

        private void SetUpGo<T>(ref GameObject GO,string name) where T : MonoBehaviour
        {

            GO = new GameObject(name);
            GO.AddComponent<T>();
            GameObject.DontDestroyOnLoad(GO);
            GO.SetActive(true);
        }
        private void RegisterCommands()
        {
            DevConsoleApi.RegisterCommand<int>("frs",i=>this.frontRenderQueue=i);
            DevConsoleApi.RegisterCommand<int>("brs",i=>this.backRenderQueue=i);
            DevConsoleApi.RegisterCommand("VolkenForceRefresh",ForceRefresh);
            DevConsoleApi.RegisterCommand<int>("VolkenCloudPerf", CloudPerformance.Capture);
            DevConsoleApi.RegisterCommand<int>("VolkenCloudFastPath", CloudPerformance.SetFastPaths);
            DevConsoleApi.RegisterCommand<int>("VolkenCloudBench", CloudPerformanceSweep.Run);

        }


        /// <summary>开发命令 <c>VolkenForceRefresh</c>:强制重跑一次行星解析。</summary>
        private void ForceRefresh()
        {
            if (!Game.InFlightScene) return;

            VolumetricClouds.Initialize();
            VolkenWeather.Initialize();

            VolumetricClouds.Instance?.OnFlightSceneLoaded();
            Log("VolkenForceRefresh: 已让 Clouds 重新解析行星环境");;
        }
        #region LOG
        public static void Log(string format, params object[] args)
        {
            bool enabled;
            try { enabled = ModSettings.Instance?.DevMode?.Value == true; }
            catch { return; }
            if (!enabled) return;
            try
            {
                if (args == null || args.Length == 0) Debug.unityLogger.Log(LogType.Log, "[Volken]" + format);
                else Debug.unityLogger.LogFormat(LogType.Log, "[Volken]" + format, args);
            }
            catch
            {
                Debug.LogWarning("[Volken] Log formatting failed.");
            }
        }
        #endregion

        #region ASSET LOADING

        /// <summary>
        /// 从 bundle 载入资源;**用 <c>LoadAsset&lt;T&gt;</c> 而不是 <c>Load&lt;T&gt;</c>**(后者读不到 mod bundle)。
        /// 失败记日志并返回 null。
        /// </summary>
        /// <param name="path">工程内路径(移动资产必须同步改这里)。</param>
        /// <param name="required">true = 缺失时打日志(默认)。</param>
        public static T LoadVolkenAsset<T>(string path, bool required = true) where T : UnityEngine.Object
        {

            try
            {
                var asset = Instance.ResourceLoader.LoadAsset<T>(path);
                if (asset != null)
                {
                    return asset;
                }
            }
            catch
            {
            }

            if (required) Log("Required asset '{0}' not found (rebuild the asset bundle?)", path);
            return null;
        }

        #endregion
    }
}
