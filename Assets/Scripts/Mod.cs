using Assets.Packages.DevConsole;
using Assets.Scripts.Flight.UI;
using HarmonyLib;

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
        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();
            //PlanetRingsZWriteFix.Apply(harmony);
            
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
        
        private void RegisterCommands()
        {
            DevConsoleApi.RegisterCommand<int>("frs",i=>this.frontRenderQueue=i);
            DevConsoleApi.RegisterCommand<int>("brs",i=>this.backRenderQueue=i);
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