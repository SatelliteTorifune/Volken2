using Assets.Scripts;
using ModApi.GameLoop;
using UnityEngine;

public class ForceSetting : MonoBehaviourBase
{
    private float checkInterval = 2f;
    private void OnEnable()
    {
        InvokeRepeating(nameof(CheckWaterTransparency), checkInterval, checkInterval);
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(CheckWaterTransparency));  
    }
    
    private void CheckWaterTransparency()
    {
        // respect the user's choice: when AlterTransparency is off, leave the game's
        // water settings completely alone (previously this forced transparency to false)
        if (!ModSettings.Instance.AlterTransparency.Value) return;

        var flightScene = Game.Instance.FlightScene;
        if (flightScene == null) return;
        var flightData = flightScene.CraftNode.CraftScript.FlightData;
        if (flightData == null) return;
        
        bool targetTransparency = flightData.AltitudeAboveSeaLevel <= ModSettings.Instance.MinHeight;

        var actualWaterTransparency = Game.Instance.Settings.Quality.Water.Transparency;

        if (actualWaterTransparency.Value != targetTransparency)
        {
            actualWaterTransparency.Value = targetTransparency;
            Game.Instance.Settings.Quality.Water.CommitChanges();
            Game.Instance.Settings.Quality.ApplySettings();
            Mod.LOG($"Volken.ForceSetting:Water Transparency set to {targetTransparency} at altitude {flightData.AltitudeAboveSeaLevel:F1}m");
        }
    }
}