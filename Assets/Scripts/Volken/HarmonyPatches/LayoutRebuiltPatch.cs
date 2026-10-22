using System;
using Assets.Scripts.Flight.UI;
using HarmonyLib;

namespace Assets.Scripts
{
    using Volken.Core;
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
                Mod.Log("Volken:Error while adding click event to{0}", e);
            }

            return true;
        }
    }
    #endregion
}