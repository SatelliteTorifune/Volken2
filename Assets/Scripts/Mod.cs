using Assets.Packages.DevConsole;
using Assets.Scripts.Flight.UI;
using HarmonyLib;

namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Drawing.Printing;
    using System.Linq;
    using System.Text;
    using Assets.Scripts.Flight;
    using ModApi;
    using ModApi.Common;
    using ModApi.Mods;
    using ModApi.Scenes.Events;
    using ModApi.Ui.Inspector;
    using UnityEngine;

    /// <summary>
    /// A singleton object representing this mod that is instantiated and initialize when the mod is loaded.
    /// </summary>
    public class Mod : ModApi.Mods.GameMod
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
        
        protected override void OnModInitialized()
        {
            base.OnModInitialized();
            GameObject VolkenUI=new GameObject("VolkenUI");
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

    }
    [HarmonyPatch(typeof(NavPanelController), "LayoutRebuilt")]
    class LayoutRebuiltPatch2
    {
        static bool Prefix(NavPanelController ___instance)
        {
            try
            {
                Debug.LogFormat("Volken harmonyPatched0");
                ___instance.xmlLayout.GetElementById(VolkenUserInterface.volkenUserInterfaceID).AddOnClickEvent(VolkenUserInterface.Instance.OnToggleVolkenUI, true);
                Debug.LogFormat("Volken harmonyPatched");
            }
            catch (Exception e)
            {
                Debug.LogFormat("Volken:Error while adding click event to{0}", e);
            }

            return true;
        }
    }
}