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
        
        /// <summary>
        /// Gets the singleton instance of the mod object.
        /// </summary>
        /// <value>The singleton instance of the mod object.</value>
        public static Mod Instance { get; } = GetModInstance<Mod>();

        public GameObject VolkenUI;
        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();
            VolkenUI=new GameObject("VolkenUI");
            VolkenUI.AddComponent<VolkenUserInterface>();
            GameObject.DontDestroyOnLoad(VolkenUI);
            VolkenUI.SetActive(true);
            Volken.Initialize();
            RegisterCommands();
            
        }
        private void RegisterCommands()
        {
            //I don't really know if i need to use console here so I'll just leave a function here so far
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