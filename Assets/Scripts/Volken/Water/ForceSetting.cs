using Assets.Scripts;
using ModApi.GameLoop;
using UnityEngine;

namespace Volken.Water
{

    public class ForceSetting : MonoBehaviourBase
    {
        private float checkInterval = 2f;
        // 先排程再调 base:base 会 Register 进游戏循环,它若抛异常也不该让计时器没排上
        protected override void OnEnable()
        {
            InvokeRepeating(nameof(CheckWaterTransparency), checkInterval, checkInterval);
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            CancelInvoke(nameof(CheckWaterTransparency));
            base.OnDisable();
        }

        private void CheckWaterTransparency()
        {
            // respect the user's choice: when AlterTransparency is off, leave the game's water settings alone
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
                Mod.Log($"Volken.ForceSetting:Water Transparency set to {targetTransparency} at altitude {flightData.AltitudeAboveSeaLevel:F1}m");
            }
        }
    }
}
