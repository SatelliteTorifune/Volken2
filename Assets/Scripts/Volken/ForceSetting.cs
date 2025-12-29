using Assets.Scripts;
using UnityEngine;

public class ForceSetting : MonoBehaviour
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
        var flightScene = Game.Instance.FlightScene;
        if (flightScene == null) return;
        var flightData = flightScene.CraftNode.CraftScript.FlightData;
        if (flightData == null) return;
        
        bool targetTransparency = flightData.AltitudeAboveSeaLevel <= ModSettings.Instance.MinHeight && ModSettings.Instance.AlterTransparency.Value;

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