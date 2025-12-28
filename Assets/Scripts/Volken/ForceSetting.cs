using Assets.Scripts;
using UnityEngine;

public class ForceSetting : MonoBehaviour
{
    private float checkInterval = 1f;

    private void Start()
    {
        InvokeRepeating(nameof(CheckWaterTransparency), checkInterval, checkInterval);
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(CheckWaterTransparency));  
    }

    private void CheckWaterTransparency()
    {
        var flightScene = Game.Instance.FlightScene;
        if (flightScene == null) return;

        var flightData = flightScene.CraftNode.CraftScript.FlightData;
        if (flightData == null) return;

        bool shouldBeTransparent = flightData.AltitudeAboveSeaLevel <= ModSettings.Instance.MinHeight && ModSettings.Instance.AlterTransparency.Value;

        var waterTransparency = Game.Instance.Settings.Quality.Water.Transparency;

        if (waterTransparency.Value != shouldBeTransparent)
        {
            waterTransparency.Value = shouldBeTransparent;
            Game.Instance.Settings.Quality.Water.CommitChanges();
            Game.Instance.Settings.Quality.ApplySettings();
            Mod.LOG($"[ForceSetting] Water Transparency set to {shouldBeTransparent} at altitude {flightData.AltitudeAboveSeaLevel:F0}m");
        }
    }
}