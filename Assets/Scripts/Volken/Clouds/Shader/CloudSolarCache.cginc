#ifndef VOLKEN_CLOUD_SOLAR_CACHE_INCLUDED
#define VOLKEN_CLOUD_SOLAR_CACHE_INCLUDED

Texture2D<float2> _CloudSolarCacheTex;
SamplerState sampler_CloudSolarCacheTex;
float _CloudSolarCacheEnabled;
float4 _CloudSolarCacheMin;    // xyz: reference-frame origin; w: inverse grid spacing (1/m).
float4 _CloudSolarCacheLayout; // resolution, atlas columns, width, height.

float2 CloudSolarPath(float3 pos)
{
    if (RaySphereIntersect(pos, -lightDir, surfaceRadius).y > 0.0) return float2(-1.0, 0.0);
    float2 shell = RaySphereIntersect(pos, -lightDir, surfaceRadius + maxCloudHeight);
    float distance = shell.y - max(0.0, shell.x);
    return float2(min(numLightSamplePoints, ceil(distance / lightStepSize)), distance);
}

// Preserve the original incremental sample positions, including the two skipped local samples.
float CloudSolarTail(float3 pos, float2 noiseLod, int count)
{
    float3 tailPosition = pos;
    float sum = 0.0;
    [loop] for (int j = 0; j < count; j++)
    {
        tailPosition += lightStepSize * -lightDir;
        if (j >= 2) sum += lightStepSize * max(0.0, SampleDensityCheap(tailPosition, noiseLod));
    }
    return sum;
}

float2 BuildCloudSolarCache(float3 pos)
{
    float2 path = CloudSolarPath(pos);
    return float2(CloudSolarTail(pos, StableNoiseLod(), (int)path.x), path.x);
}

float2 LoadCloudSolarNode(int3 node)
{
    int size = (int)_CloudSolarCacheLayout.x;
    int columns = (int)_CloudSolarCacheLayout.y;
    int2 tile = int2(node.z % columns, node.z / columns);
    float2 uv = (tile * size + node.xy + 0.5) / _CloudSolarCacheLayout.zw;
    return _CloudSolarCacheTex.SampleLevel(sampler_CloudSolarCacheTex, uv, 0);
}

bool TrySampleCachedLightRay(float3 pos, float2 noiseLod, out float2 result, out float blend)
{
    result = 0.0;
    blend = 0.0;
    [branch] if (_CloudSolarCacheEnabled < 0.5) return false;
    float3 grid = (pos - _CloudSolarCacheMin.xyz) * _CloudSolarCacheMin.w;
    float end = _CloudSolarCacheLayout.x - 1.0;
    [branch] if (any(grid < 0.0) || any(grid >= end)) return false;

    float2 path = CloudSolarPath(pos);
    [branch] if (path.x <= 2.0) return false;
    int3 baseNode = (int3)floor(grid);
    float3 f = frac(grid);
    float cached = 0.0, minimum = 1e30, maximum = 0.0;
    bool compatible = true;
    [unroll] for (int z = 0; z < 2; z++)
    [unroll] for (int y = 0; y < 2; y++)
    [unroll] for (int x = 0; x < 2; x++)
    {
        float2 node = LoadCloudSolarNode(baseNode + int3(x, y, z));
        compatible = compatible && node.y > 2.0 && node.y <= numLightSamplePoints
            && node.x >= 0.0 && node.x < 1e25;
        float3 weight = lerp(1.0 - f, f, float3(x, y, z));
        cached += node.x * weight.x * weight.y * weight.z;
        minimum = min(minimum, node.x);
        maximum = max(maximum, node.x);
    }
    [branch] if (!compatible) return false;

    // Compare transmitted light, so fully opaque tails can reuse light despite large density differences.
    // Empty corner samples cannot rule out sub-cell occluders: retain tracing when a corner is clear.
    float3 edge = min(grid, end - grid);
    blend = saturate(min(edge.x, min(edge.y, edge.z)));
    float minimumTau = minimum * max(0.0, cloudAbsorption);
    float maximumTau = maximum * max(0.0, cloudAbsorption);
    blend *= smoothstep(0.01, 0.05, minimumTau);
    blend *= 1.0 - smoothstep(0.02, 0.08, exp(-minimumTau) - exp(-maximumTau));
    [branch] if (blend <= 0.0) return false;
    float3 localPos = pos + lightStepSize * -lightDir;
    float local = lightStepSize * max(0.0, SampleDensityCheap(localPos, noiseLod));
    localPos += lightStepSize * -lightDir;
    local += lightStepSize * max(0.0, SampleDensityCheap(localPos, noiseLod));
    result = float2(local + cached, path.y);
    return true;
}

float2 SampleCachedLightRay(float3 pos, float2 noiseLod)
{
    float2 result;
    float blend;
    TrySampleCachedLightRay(pos, noiseLod, result, blend);
    if (blend >= 1.0) return result;
    float2 direct = SampleLightRay(pos, noiseLod);
    return blend > 0.0 ? lerp(direct, result, blend) : direct;
}
#endif
