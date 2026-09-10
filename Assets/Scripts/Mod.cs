using System.IO;
using Assets.Packages.DevConsole;
using Assets.Scripts.Flight.UI;
using HarmonyLib;
using ModApi.Scenes.Events;

namespace Assets.Scripts
{
    using System;
    using UnityEngine;

    /// <summary>
    /// A singleton object representing this mod that is instantiated and initialize when the mod is loaded.
    /// </summary>
    public partial class Mod : ModApi.Mods.GameMod
    {
        /// <summary>
        /// Prevents a default instance of the <see cref="Mod"/> class from being created.
        /// </summary>
        private Mod() : base()
        {
        }

        public int frontRenderQueue = 3000;
        public int backRenderQueue = 3000;
        
        //f 2000 2500 2501
        //b 2500-2501
        
        /// <summary>
        /// Gets the singleton instance of the mod object.
        /// </summary>
        /// <value>The singleton instance of the mod object.</value>
        public static Mod Instance { get; } = GetModInstance<Mod>();

        private GameObject VolkenUI;
        public GameObject ForceSettingScriptLoadGameObject;
        
        /// <summary>
        /// Gets the mod version as reported by the mod manifest (ModInfo.Version), e.g. 0.6.
        /// </summary>
        public Version ModVersion { get; private set; }
        
        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();
            //PlanetRingsZWriteFix.Apply(harmony);
            //PlanetRingsShaderPatch.Apply(harmony);
            VolkenUI=new GameObject("VolkenUI");
            VolkenUI.AddComponent<VolkenUserInterface>();
            GameObject.DontDestroyOnLoad(VolkenUI);
            VolkenUI.SetActive(true);
            ForceSettingScriptLoadGameObject=new GameObject("ForceSettingObject");
            ForceSettingScriptLoadGameObject.AddComponent<ForceSetting>();
            GameObject.DontDestroyOnLoad(ForceSettingScriptLoadGameObject);
            ForceSettingScriptLoadGameObject.SetActive(false);
            Volken.Initialize();
            VolkenProfiler.ProfilerController.Create();
            RegisterCommands();

            Game.Instance.Settings.Game.Flight.GroundClouds.Value = true;

            // 本地版本 = ModInfo.Version(System.Version,如 0.6)
            this.ModVersion = this.ModInfo.Version;

            // 更新检查(本地模式:LatestVersionUrl 未配置时仅打日志,不弹窗)
            new ModUpdater().CheckForUpdate();
        }
        
        private void RegisterCommands()
        {
            DevConsoleApi.RegisterCommand<int>("frs",i=>this.frontRenderQueue=i);
            DevConsoleApi.RegisterCommand<int>("brs",i=>this.backRenderQueue=i);
            DevConsoleApi.RegisterCommand("VolkenForceRefresh",ForceRefresh);
        }
        
        private void ForceRefresh()
        {
            if (!Game.InFlightScene)
            {
                return;
            }
            if (Volken.Instance==null)
            {
                Volken.Initialize();
                Volken.Instance?.OnFlightSceneLoaded();
                Log("force refresh called");
            }

            if (Volken.Instance!=null)
            { 
                Volken.Initialize();
                Volken.Instance?.OnFlightSceneLoaded();
                Log("Volken is still alive");
            }
        }
        #region LOG
        public static void Log(string format, params object[] args)
        {
            try
            {
                if (ModSettings.Instance == null || !ModSettings.Instance.DevMode) return;
                Debug.unityLogger.LogFormat(LogType.Log, "[Volken]"+format, args);
            }
            catch
            {
                Debug.Log("什么叫做他妈的Log报错了??????");
            }
        }
        #endregion
    }
}