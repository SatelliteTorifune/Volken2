using Assets.Scripts;
using ModApi;
using ModApi.Flight.Sim;
using ModApi.Planet;
using UnityEngine;

/// <summary>
/// 方案 B:加载游戏自带云的 Cloud cubemap(PlanetCubemapType.Clouds),
/// 作为 Volken 的"全球分布形状"(哪里出云、云多大、纬度带怎么走)。
///
/// 通道语义:RGBA = (低云密度, 中云密度, 高云密度, 纬度/行星遮罩),全部 clamp01。
///
///  - SOI 进入有大气星球时 LoadFor(),缓存当前星球;切换/进太阳时 Release()。
///  - 加载失败(无 Clouds modifier / renderClouds=false / 该画质档未生成)返回 null
///    → shader 自动回退 Volken 的 PlanetMapTex(行为与之前完全一致)。
///
/// 注意:本方案只借用水平分布,**不**替换 Volken 的垂直层带(layerHeights/spreads)与密度公式。
/// </summary>
public static class StockCloudMap
{
    /// <summary>当前星球已加载的 Cloud cubemap;null = 回退程序化分布。</summary>
    public static Cubemap Current { get; private set; }

    /// <summary>
    /// 加载时检测出的"该星球游戏各云层是否真实存在"。
    /// (R,G,B,A) = (低云, 中云, 高云, 纬度/行星遮罩),1=有数据,0=该层不存在。
    /// 某层为 0 时,shader 会将该 Volken 层回退到老 planetMap 分布(兜底)。
    /// </summary>
    public static Vector4 LayerValid { get; private set; } = Vector4.one;

    /// <summary>
    /// 加载并缓存当前星球的 Cloud cubemap。
    /// 优先尝试画质设置里最大的已生成档位(与游戏 SaveCloudCubemap 的尺寸集合一致)。
    /// </summary>
    public static Cubemap LoadFor(IPlanetNode planet)
    {
        Release();

        if (planet?.PlanetData == null)
        {
            return null;
        }

        try
        {
            IPlanetData data = planet.PlanetData;

            // Sizes are saved as maxSize / 2^i for i in [0, GenerationDownsampleCount).
            var cubemapSettings = Game.Instance.QualitySettings.Terrain.CubemapSettings;
            int maxSize = cubemapSettings.MaxSize;
            int downsample = Mathf.Max(1, cubemapSettings.GenerationDownsampleCount);

            Cubemap cube = null;
            for (int i = 0; i < downsample; i++)
            {
                int size = maxSize / (int)Mathf.Pow(2f, i);
                if (size < 64)
                {
                    continue;
                }

                // create=false: missing size / no clouds returns null quickly.
                cube = PlanetCubemapUtility.LoadCubemap(data, PlanetCubemapType.Clouds, size, false);
                if (cube != null)
                {
                    // 兜底:检测每层是否真有数据(R/G/B=低/中/高,A=遮罩)
                    Vector4 valid = ComputeLayerValidity(cube);
                    bool anyCloud = valid.x > 0.5f || valid.y > 0.5f || valid.z > 0.5f;
                    if (!anyCloud)
                    {
                        Object.Destroy(cube);
                        Mod.LOG($"Volken:StockCloudMap {data.Name} cubemap has no real cloud data — full fallback to procedural.");
                        return null;
                    }

                    Current = cube;
                    LayerValid = valid;
                    Mod.LOG($"Volken:StockCloudMap loaded {data.Name} clouds cubemap @ {size}, layer validity R/G/B/A = {valid.x}/{valid.y}/{valid.z}/{valid.w}");
                    break;
                }
            }

            if (cube == null)
            {
                Mod.LOG($"Volken:StockCloudMap no clouds cubemap for '{data.Name}' — falling back to procedural.");
            }

            return Current;
        }
        catch (System.Exception e)
        {
            Mod.LOG($"Volken:StockCloudMap failed for '{planet?.PlanetData?.Name}': {e.Message}");
        }

        return null;
    }

    /// <summary>
    /// 采样低 mip 检测各通道是否真有数据(区分"该层是真实云层"与"dummy/全 0 层")。
    /// 返回 (R,G,B,A) = (低云, 中云, 高云, 遮罩) 的 0/1 存在性。
    /// </summary>
    private static Vector4 ComputeLayerValidity(Cubemap cube)
    {
        Vector4 valid = Vector4.one;
        try
        {
            if (cube == null) return valid;

            int width = cube.width;
            int mip = Mathf.Max(0, (int)Mathf.Log(width, 2f) - 5); // 每面采样约 32 即可(足够抓稀疏云层)
            mip = Mathf.Min(mip, cube.mipmapCount - 1);

            float maxR = 0f, maxG = 0f, maxB = 0f, maxA = 0f;
            for (int face = 0; face < 6; face++)
            {
                Color[] px = cube.GetPixels((CubemapFace)face, mip);
                for (int i = 0; i < px.Length; i++)
                {
                    Color c = px[i];
                    if (c.r > maxR) maxR = c.r;
                    if (c.g > maxG) maxG = c.g;
                    if (c.b > maxB) maxB = c.b;
                    if (c.a > maxA) maxA = c.a;
                }
            }

            const float eps = 0.02f; // ~5/255,足以区分"有云数据"与"全 0 dummy"
            valid = new Vector4(
                maxR > eps ? 1f : 0f,
                maxG > eps ? 1f : 0f,
                maxB > eps ? 1f : 0f,
                maxA > eps ? 1f : 0f);

            Mod.LOG($"Volken:StockCloudMap validity max R/G/B/A = {maxR:F3}/{maxG:F3}/{maxB:F3}/{maxA:F3} → {valid.x}/{valid.y}/{valid.z}/{valid.w}");
        }
        catch (System.Exception e)
        {
            // 检测失败不致命:保守地全部视为有效(按原行为处理)
            Mod.LOG($"Volken:StockCloudMap validity check failed: {e.Message}");
        }
        return valid;
    }

    /// <summary>释放当前缓存的 cubemap(SOI 切换 / 进入太阳时)。</summary>
    public static void Release()
    {
        if (Current != null)
        {
            Object.Destroy(Current);
        }
        Current = null;
        LayerValid = Vector4.one;
    }
}