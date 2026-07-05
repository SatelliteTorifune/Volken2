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

        public GameObject VolkenUI;
        public GameObject forceSettingScriptLoadGameObject;
        public bool hasHarmony { get; private set; } = false;
        
        protected override void OnModInitialized()
        {
            base.OnModInitialized();
            CheckLocalizationFiles("ZH-CN");
            CheckLocalizationFiles("EN-US");
        }
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
            forceSettingScriptLoadGameObject=new GameObject("ForceSettingObject");
            forceSettingScriptLoadGameObject.AddComponent<ForceSetting>();
            GameObject.DontDestroyOnLoad(forceSettingScriptLoadGameObject);
            forceSettingScriptLoadGameObject.SetActive(false);
            Volken.Initialize();
            RegisterCommands();
        }
        private void CheckLocalizationFiles(string targetLanguage)
        {
            var targetPath = Path.Combine(Application.persistentDataPath, "Languages", targetLanguage, "StringsVolken.xml");
            if (File.Exists(targetPath))
            {
                return; 
            }
            
            var localizationFile = Mod.ResourceLoader.LoadAsset<TextAsset>("Assets/Resources/LocalizationFile/"+targetLanguage+"/StringsVolken.xml");
            try
            {
                File.WriteAllBytes(targetPath, localizationFile.bytes);
                Debug.LogFormat($"[Volken] Wrote {targetLanguage} localization file to: {targetPath}");
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat($"[Volken] Failed to write {targetLanguage} localization file to '{targetPath}': {e}");
            }
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
                LOG("force refresh called");
            }

            if (Volken.Instance!=null)
            { 
                Volken.Initialize();
                Volken.Instance?.OnFlightSceneLoaded();
                LOG("Volken is still alive");
            }
        }
        #region LOG
        public static void LOG(object message)
        {
            if (ModSettings.Instance.ShowDevLog)
            {
                Debug.unityLogger.Log(message);
            }
        }
        public static void LOG(string format, params object[] args)
        {
            if (ModSettings.Instance.ShowDevLog)
            {
                Debug.unityLogger.LogFormat(LogType.Log, format, args);
            }
        }
        public static void LOG(UnityEngine.Object context, string format, params object[] args)
        {
            if (ModSettings.Instance.ShowDevLog)
            {
                Debug.unityLogger.LogFormat(LogType.Log, context, format, args);
            }
        }
        #endregion
    }
}