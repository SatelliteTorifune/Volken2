using UnityEngine;

namespace Volken.Clouds
{
    // Only the volume pass reuses adjacent samples; shadows and reflection keep direct lighting.
    internal static class CloudLightReuse
    {
        internal const string Keyword = "VOLKEN_CLOUD_LIGHT_REUSE";

        internal static bool Apply(Material material, bool enabled)
        {
            if (material == null) return false;
            bool active = enabled && material.GetInt("numLightSamplePoints") > 2 &&
                material.GetFloat("_ReflectionMode") < 0.5f &&
                material.shader.keywordSpace.FindKeyword(Keyword).isValid;
            if (active) material.EnableKeyword(Keyword);
            else material.DisableKeyword(Keyword);
            return active;
        }
    }
}
