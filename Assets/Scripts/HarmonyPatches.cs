using System;
using System.Reflection;
using Assets.Scripts.Flight.UI;
using Assets.Scripts.Terrain.Rendering;
using HarmonyLib;
using ModApi;
using ModApi.Mods;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Assets.Scripts
{
    #region NavPanelController HarmonyPatch
    [HarmonyPatch(typeof(NavPanelController), "LayoutRebuilt")]
    class LayoutRebuiltPatch
    {
        static bool Prefix(NavPanelController __instance)
        {
            try
            {
                __instance.xmlLayout.GetElementById(VolkenUserInterface.volkenUserInterfaceID)
                    .AddOnClickEvent(VolkenUserInterface.Instance.OnToggleVolkenUI, true);
            }
            catch (Exception e)
            {
                Mod.LOG("Volken:Error while adding click event to{0}", e);
            }

            return true;
        }
    }
    #endregion
}