using System;
using Assets.Scripts;
using UnityEngine;
using ModApi.GameLoop.Interfaces;
using ModApi.Flight;
using ModApi.GameLoop;

public class ForceSetting : MonoBehaviour, IFlightFixedUpdate
{
    private bool _startMethodCalled = false; // 必须实现 IGameLoopItem 的要求

   
    public bool StartMethodCalled
    {
        get => _startMethodCalled;
        set => _startMethodCalled = value;
    }
    public int GetInstanceID() => gameObject.GetInstanceID();
    
    public void FlightFixedUpdate(in FlightFrameData frame)
    {
     
        return;
        if (frame.FrameCount % 10 != 0) return; 

        var flightScene = Game.Instance.FlightScene;
        if (flightScene == null) return;

        var flightData = flightScene.CraftNode.CraftScript.FlightData;
        if (flightData == null) return;

        bool shouldBeTransparent = (flightData.AltitudeAboveSeaLevel <= ModSettings.Instance.MinHeight) && ModSettings.Instance.AlterTransparency.Value;

        var waterTransparency = Game.Instance.Settings.Quality.Water.Transparency;

        if (waterTransparency.Value != shouldBeTransparent)
        {
            waterTransparency.Value = shouldBeTransparent;
            Game.Instance.Settings.Quality.Water.CommitChanges();
            Game.Instance.Settings.Quality.ApplySettings();
            Mod.LOG($"[ForceSetting] Water Transparency set to {shouldBeTransparent} at altitude {flightData.AltitudeAboveSeaLevel}m");
        }
    }

    // 可选：Start 中标记 StartMethodCalled（有些游戏循环会检查）
    private void Start()
    {
        StartMethodCalled = true;
    }

    private void Update()
    {
        var flightScene = Game.Instance.FlightScene;
        if (flightScene == null) return;

        var flightData = flightScene.CraftNode.CraftScript.FlightData;
        if (flightData == null) return;

        bool shouldBeTransparent = (flightData.AltitudeAboveSeaLevel <= ModSettings.Instance.MinHeight) && ModSettings.Instance.AlterTransparency.Value;

        var waterTransparency = Game.Instance.Settings.Quality.Water.Transparency;

        if (waterTransparency.Value != shouldBeTransparent)
        {
            waterTransparency.Value = shouldBeTransparent;
            Game.Instance.Settings.Quality.Water.CommitChanges();
            Game.Instance.Settings.Quality.ApplySettings();
            Mod.LOG($"[ForceSetting] Water Transparency set to {shouldBeTransparent} at altitude {flightData.AltitudeAboveSeaLevel}m");
        }
    }
}