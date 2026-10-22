using System;
using UnityEngine;

namespace Volken.Clouds
{
    // 保守高斯支持带只省去确定非正密度的查询，不替换密度场或采样格点。
    internal static class CloudEmptySpace
    {
        private static readonly int EnabledId = Shader.PropertyToID("_CloudEmptySpaceEnabled");
        private static readonly int MinId = Shader.PropertyToID("_CloudEmptyBandMin");
        private static readonly int MaxId = Shader.PropertyToID("_CloudEmptyBandMax");

        internal static bool Configure(Material material, CloudLayer layer, bool enabled)
        {
            if (material == null) return false;
            material.SetFloat(EnabledId, 0f);
            if (!enabled || layer == null || !HasBoundedTextures(material, layer)) return false;

            if (!TryBuildBands(material.GetFloat("cloudCoverage"), material.GetFloat("cloudDensity"),
                material.GetFloat("detailStrength"), material.GetVector("cloudLayerHeights"),
                material.GetVector("cloudLayerSpreads"), material.GetVector("cloudLayerStrengths"),
                out var minimum, out var maximum)) return false;

            material.SetVector(MinId, minimum);
            material.SetVector(MaxId, maximum);
            material.SetFloat(EnabledId, 1f);
            return true;
        }

        // 按包络权重分配各带的阈值；全部支持带外的贡献和仍低于真正密度阈值。
        internal static bool TryBuildBands(float coverage, float density, float detailStrength,
            Vector4 heights, Vector4 spreads, Vector4 strengths, out Vector4 minimum, out Vector4 maximum)
        {
            minimum = Vector4.one;
            maximum = -Vector4.one;
            if (!Finite(coverage) || !Finite(density) || density <= 0f || !Finite(detailStrength)) return false;
            double threshold = (1.0 - coverage) * 0.9999 - 0.00001;
            if (threshold <= 0.0) return false;
            double shapeUpper = 1.0 + Math.Max(0.0, -detailStrength);
            double totalWeight = 0.0;
            for (int band = 0; band < 4; band++)
            {
                float height = heights[band], spread = spreads[band], strength = strengths[band];
                if (!Finite(height) || !Finite(spread) || !Finite(strength) || Math.Abs(spread) < 0.0001f)
                    return false;
                double active = band < 2 || strength >= 0.0001f ? 1.0 : 0.0;
                double weight = shapeUpper * active + Math.Abs(strength);
                if (weight > 0.0) totalWeight += weight * 1.0001 + 0.000001;
            }
            if (totalWeight <= threshold) return true;
            double widthFactor = Math.Sqrt(Math.Log(totalWeight / threshold));
            for (int band = 0; band < 4; band++)
            {
                float height = heights[band], spread = spreads[band], strength = strengths[band];
                if (band >= 2 && strength == 0f) continue;

                double halfWidth = Math.Abs(spread) * widthFactor;
                double padding = Math.Max(0.01, (Math.Abs(height) + halfWidth) * 0.000002);
                float lower = (float)(height - halfWidth - padding);
                float upper = (float)(height + halfWidth + padding);
                if (!Finite(lower) || !Finite(upper)) return false;
                minimum[band] = lower;
                maximum[band] = upper;
            }
            return true;
        }

        private static bool HasBoundedTextures(Material material, CloudLayer layer)
        {
            if (layer.worleyTex == null || layer.detailTex == null || layer.planetMapTex == null ||
                !layer.worleyTex.IsCreated() || !layer.detailTex.IsCreated() ||
                layer.worleyTex.format != RenderTextureFormat.R8 || layer.detailTex.format != RenderTextureFormat.R8 ||
                layer.planetMapTex.format != TextureFormat.RGFloat ||
                material.GetTexture("CloudShapeTex") != layer.worleyTex ||
                material.GetTexture("CloudDetailTex") != layer.detailTex ||
                material.GetTexture("PlanetMapTex") != layer.planetMapTex) return false;

            // CloudNoise.GetPlanetMap 使用归一化 ValueFractal，RGFloat 保留其 [-1,1] 值域。
            float useStock = material.GetFloat("useStockCloudMap");
            float stockStrength = material.GetFloat("stockMapStrength");
            if (!UnitInterval(useStock) || !UnitInterval(stockStrength)) return false;
            if (useStock <= 0.5f || stockStrength <= 0f) return true;
            var stock = material.GetTexture("StockCloudCube") as Cubemap;
            if (stock == null || !IsUnsignedNormalized(stock.format) ||
                !UnitInterval(material.GetFloat("stockMaskInfluence")) ||
                !UnitInterval(material.GetFloat("stockDensityScale"))) return false;
            Vector4 valid = material.GetVector("stockLayerValid");
            return UnitInterval(valid.x) && UnitInterval(valid.y) && UnitInterval(valid.z) && UnitInterval(valid.w);
        }

        private static bool IsUnsignedNormalized(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGB24:
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.RGB565:
                case TextureFormat.R16:
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.RGBA4444:
                case TextureFormat.BGRA32:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                case TextureFormat.RG16:
                case TextureFormat.R8:
                    return true;
                default:
                    return false;
            }
        }

        private static bool UnitInterval(float value) => Finite(value) && value >= 0f && value <= 1f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 1e20f;
    }
}
