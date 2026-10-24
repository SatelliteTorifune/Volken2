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
                if (_shader == null || !_shader.isSupported)
                    Mod.Log("Scene depth shader missing or unsupported; rebuild and reload the mod bundle");
            }
            return _shader != null && _shader.isSupported
                ? new Material(_shader) { hideFlags = HideFlags.HideAndDontSave } : null;
        }
    }
}
