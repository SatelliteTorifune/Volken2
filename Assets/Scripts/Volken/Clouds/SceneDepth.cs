using UnityEngine;
using Assets.Scripts;

namespace Volken.Clouds
{
    internal static class SceneDepth
    {
        private static Shader _shader;
        private static float _retry;
        internal static Material CreateMaterial()
        {
            if (_shader == null && Time.realtimeSinceStartup >= _retry)
            {
                _retry = Time.realtimeSinceStartup + 5f;
                _shader = Mod.LoadVolkenAsset<Shader>("Assets/Scripts/Volken/Clouds/Shader/SceneDepth.shader", false);
                Mod.Diag("Fog depth asset: {0}", _shader != null && _shader.isSupported ? "ready" : "missing/unsupported; rebuild bundle");
            }
            return _shader != null && _shader.isSupported
                ? new Material(_shader) { hideFlags = HideFlags.HideAndDontSave } : null;
        }
    }
}
