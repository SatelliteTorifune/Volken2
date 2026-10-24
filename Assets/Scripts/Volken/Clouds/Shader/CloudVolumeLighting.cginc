#ifndef VOLKEN_CLOUD_VOLUME_LIGHTING
#define VOLKEN_CLOUD_VOLUME_LIGHTING
float4 _LightningPositionRange, _LightningColorIntensity;
int _LightningSamples;
Texture2D<float> _LightningShadowTex;
SamplerState sampler_LightningShadowTex;

//Textures
sampler2D _CameraDepthTexture;

Texture2D<float> DepthTex;
SamplerState samplerDepthTex;

Texture3D<float> CloudShapeTex;
SamplerState samplerCloudShapeTex;

Texture3D<float> CloudDetailTex;
SamplerState samplerCloudDetailTex;

Texture2D<float2> PlanetMapTex;
SamplerState samplerPlanetMapTex;

// Game stock cloud cubemap as global distribution shape (plan B)
TextureCube<float4> StockCloudCube;
SamplerState samplerStockCloudCube;
float useStockCloudMap;        // 0/1 master switch (forced 0 when no cubemap loaded)
float stockMapStrength;        // 0..1 blend strength
float stockMaskInfluence;      // 0..1 latitude/planet mask (A channel) influence
float stockMapLayer;           // 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
float4 stockLayerValid;        // load-time check: (R=low, G=mid, B=high, A=mask) layer presence; 0 = fall back that band to planetMap
float stockAlignSign;          // +-1 rotation direction
float stockAlignAngleOffset;   // degrees, one-time alignment
float stockDensityScale;       // 方案 A:区域内密度缩放 0..1(1=恒等/现状;保留足迹边缘)
float4x4 planetToBody;         // reference-frame -> planet-body rotation

Texture2D<float4> BlueNoiseTex;
SamplerState samplerBlueNoiseTex;

Texture2D<float4> HistoryTex;
SamplerState samplerHistoryTex;
Texture2D<float> HistoryDepthTex;
SamplerState samplerHistoryDepthTex;

Texture2D<float> HistoryCloudDepthTex;
SamplerState samplerHistoryCloudDepthTex;

Texture2D<float> CloudDepthTex;
SamplerState samplerCloudDepthTex;


//Cloud Shape
float cloudDensity;
float _CloudDensityBounds;
float cloudAbsorption;
float ambientLight;
float4 _CloudAmbientColor;
float4 _CloudSolarColor;
float _CloudSunOcclusion;
float _SunlightOcclusionStrength, _AmbientOcclusionStrength;
float cloudCoverage;
float cloudScale;
float detailScale;
float detailStrength;
float3 cloudOffset;
float4 cloudColor;
float scatterStrength;
float historyDepthThreshold;

//scatter
float scatterPower;
float multiScatterBlend;
float ambientScatterStrength;
float3 customWavelengths;
float silverLiningIntensity;
float forwardScatteringBias;
//Cloud Layers (x=Layer1, y=Layer2, z=Layer3, w=Layer4)
float4 cloudLayerHeights;
float4 cloudLayerSpreads;
float4 cloudLayerStrengths;

//Container
float surfaceRadius;
float maxCloudHeight;
float3 sphereCenter;

// Quality
float stepSize;
float stepSizeFalloff;
int numLightSamplePoints;
float lightStepSize;    // 光照步进独立步长(= lightMarchDistance / numLightSamplePoints)

//Misc
float3 lightDir;
float4 phaseParams;
float2 blueNoiseScale;
float2 blueNoiseOffset;
float blueNoiseStrength;
float atmoBlendFactor;
float maxDepth;
float historyBlend;
matrix reprojMat;
matrix _HistoryCloudViewMat;
float currentRotation;
        
// magic functions for better lighting
float HenyeyGreenstein(float a, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * 3.1415926 * pow(abs(1.0 + g2 - 2.0 * g * a), 1.5));
}
float HenyeyGreensteinMultiple(float cosAngle, float g1, float g2, float blend)
{
    float hg1 = HenyeyGreenstein(cosAngle, g1);
    float hg2 = HenyeyGreenstein(cosAngle, g2);
    float hg3 = HenyeyGreenstein(cosAngle, 0.0);
    
    float firstBlend = lerp(hg1, hg2, blend);
    return lerp(firstBlend, hg3, multiScatterBlend * 0.5);
}
float CloudPhase(float a, float blend)
{
    float hgBlend = HenyeyGreensteinMultiple(a, forwardScatteringBias, -phaseParams.y, blend);
    return phaseParams.z + hgBlend * phaseParams.w * silverLiningIntensity;
}

float Phase(float a) {
    float blend = .5;
    float hgBlend = HenyeyGreenstein(a, phaseParams.x) * (1 - blend) + HenyeyGreenstein(a, -phaseParams.y) * blend;
    return phaseParams.z + hgBlend * phaseParams.w;
}

// basic transmittance function
float Beer(float d, float amb) {
    return amb + exp(-d * cloudAbsorption) * (1.0 - amb);
}

// more advanced transmittance function for lighting stuff
float BeersPowder(float d, float amb)
{
    return amb + 2.0 * exp(-d * cloudAbsorption) * (1.0 - exp(-2.0 * d * cloudAbsorption)) * (1.0 - amb);
}

// 对本段的 Beer 衰减作解析积分;密度×步长的显式累加会放大厚样本亮点。
float SegmentScatteringWeight(float densityStep, float amb)
{
    float opticalDepth = max(0.0, densityStep * cloudAbsorption);
    float attenuationIntegral = opticalDepth > 1e-3
        ? (1.0 - exp(-opticalDepth)) / opticalDepth
        : 1.0 - 0.5 * opticalDepth + opticalDepth * opticalDepth / 6.0;
    return lerp(attenuationIntegral, 1.0, amb);
}

// returns the distances of the intersections from the given point
float2 RaySphereIntersect(float3 pos, float3 dir, float radius) {
    float3 offset = pos - sphereCenter;

    float a = dot(dir, dir);
    float b = 2 * dot(offset, dir);
    float c = dot(offset, offset) - radius * radius;
    float d = b * b - 4 * a * c;

    // no intersection
    if (d < 0.0) {
        return -1.0;
    }

    float sqrtD = sqrt(d);
    return float2((-b - sqrtD) / (2 * a), (-b + sqrtD) / (2 * a));
}

// 方案 B: sample the game stock Clouds cubemap as the global distribution shape.
// dir is in the reference frame (already rotated by currentRotation);
// we apply E/W wind as a Y rotation (approximation of the planetMap UV shift),
// then rotate into the planet-body space where the cubemap was baked.
float4 SampleStockDistribution(float3 dir, float windAngle)
{
    float yAngle = windAngle + stockAlignSign * (stockAlignAngleOffset * 0.0174532925);
    float ca = cos(yAngle);
    float sa = sin(yAngle);
    float3 sd = float3(dir.x * ca - dir.z * sa, dir.y, dir.x * sa + dir.z * ca);
    sd = mul(planetToBody, float4(sd, 0.0)).xyz;
    return StockCloudCube.SampleLevel(samplerStockCloudCube, sd, 0);
}

float2 StableNoiseLod()
{
    // 原预设的世界场;FOV、相机旋转和 TSS 不改变密度与光照采样。
    return float2(0.0, 0.0);
}

float SampleDensity(float3 worldPos, float detailFalloff, float2 noiseLod)
{
    float3 offset = worldPos - sphereCenter;
    float r = length(offset);

    float cosAngle = cos(currentRotation);
    float sinAngle = sin(currentRotation);
    float3 rotatedOffset = float3(
        offset.x * cosAngle - offset.z * sinAngle,
        offset.y,
        offset.x * sinAngle + offset.z * cosAngle
    );

    float3 dir = normalize(rotatedOffset);
    float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);

    //yes the wind and the angular are somehow conflic...but lmao i dgaf
    //no joking,but that's the best effect for now if you don't want to see the pile of shit in northern pole
    // Strong east/west wind (full strength)
    spherical.x += cloudOffset.x;

    //TODO the velocity of the cloud should be with direction
    // Safe north/south wind: attenuated by latitude (zero at poles, max at equator)
    float latFactor = sin(spherical.y * 3.14159265); 
    spherical.y += cloudOffset.z * 0.25 * latFactor; 

    float2 planetMap = PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);

    // 方案 B: game stock Clouds cubemap as the global distribution shape.
    // stockEff == 0 keeps the exact previous behavior (pure fallback).
    // Uniform branch: when the feature is off (or no cubemap is bound) we skip
    // the cubemap fetch entirely so there is zero added cost vs the original.
    float4 stock = 0.0;
    if (useStockCloudMap > 0.5)
    {
        stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
    }
    float stockEff = useStockCloudMap * stockMapStrength;
    // 兜底(方案 B):load 时检测该星球游戏各云层是否真实存在(R/G/B=低/中/高,A=遮罩)。
    // 某层不存在(valid=0)→ 该层回退到老 Volken 的 planetMap;遮罩不存在 → mask 置中性。
    float selValid = lerp(lerp(stockLayerValid.x, stockLayerValid.y, step(0.5, stockMapLayer)), stockLayerValid.z, step(1.5, stockMapLayer));
    float4 valid = lerp(selValid.xxxx, float4(stockLayerValid.x, stockLayerValid.y, stockLayerValid.z, stockLayerValid.x), step(2.5, stockMapLayer));
    float stockMaskValid = stockLayerValid.w;
    float stockMask = lerp(1.0, stock.a, stockMaskInfluence * stockEff * stockMaskValid);
    // 方案 B layer source: 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
    float selChannel = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
    float4 stockBandRaw = lerp(selChannel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer));
    float4 stockBand = stockBandRaw * stockMask;

    // 方案 A:区域内密度缩放(默认 1 → 恒等 → 现状逐字节一致;0 仍 0 → 足迹边缘保留)
    float4 stockCov = stockBand * stockDensityScale;

    // Layer1/3/4 use density (planetMap.r), Layer2 uses height (planetMap.g)
    // Stock replaces the data source: R=low, G=mid, B=high, A=mask.
    // valid==0 的层保持 planetMap(老 Volken 行为)
    float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockCov, stockEff * valid);
    float4 layers;
    layers.x = cloudLayerStrengths.x * mapVal.x;
    layers.y = cloudLayerStrengths.y * mapVal.y;
    layers.z = cloudLayerStrengths.z * mapVal.z;
    layers.w = cloudLayerStrengths.w * mapVal.w;

    float4 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
    float4 falloff = exp(-falloffExponent * falloffExponent);
    
    // Gate: only active layers (strength > 0) contribute shape * falloff
    // This preserves EXACT original behavior for Layer1&2 when Layer3/4 are disabled
    float4 active = step(0.0001, cloudLayerStrengths);
    // 方案 B: gate the 3D shape by the stock distribution per band (valid==0 → dist=1, 不回退门)
    float4 dist = lerp(float4(1.0, 1.0, 1.0, 1.0), stockBand, stockEff * valid);
    // Maximum uneroded shape; keep a margin at the density boundary and fall back for unknown textures.
    float shapeUpper = 1.0 + max(0.0, -detailStrength);
    float upper = shapeUpper * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                + layers.x * falloff.x + layers.y * falloff.y
                + layers.z * falloff.z + layers.w * falloff.w + cloudCoverage - 1.0;
    float margin = 1e-4 * (1.0 + abs(cloudCoverage) + dot(abs(layers), falloff) + shapeUpper * dot(dist, falloff));
    [branch] if (_CloudDensityBounds > 0.5 && upper < -margin) return 0.0;

    // Domain warping: small sinusoidal perturbation to break up visible tiling
    float3 warpOffset = float3(
        sin(rotatedOffset.y * 0.05 + rotatedOffset.z * 0.03),
        cos(rotatedOffset.x * 0.04 - rotatedOffset.z * 0.05),
        sin(rotatedOffset.x * 0.03 + rotatedOffset.y * 0.04)
    ) * 0.15;
    float3 warpedUv = rotatedOffset + warpOffset;

    // Keep shape relatively cheap; target the smallest-scale repetition in detail.
    float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, warpedUv * cloudScale, noiseLod.x);
    float detail = CloudDetailTex.SampleLevel(samplerCloudDetailTex, warpedUv * detailScale, noiseLod.y);
    shape -= (1.0 - shape) * (1.0 - shape) * detailStrength * detailFalloff * detail;

    float totalDensity = shape * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                       + layers.x * falloff.x + layers.y * falloff.y
                       + layers.z * falloff.z + layers.w * falloff.w;
    
    return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
}

float SampleDensityCheap(float3 worldPos, float2 noiseLod)
{
    float3 offset = worldPos - sphereCenter;
    float r = length(offset);

    float cosAngle = cos(currentRotation);
    float sinAngle = sin(currentRotation);
    float3 rotatedOffset = float3(
        offset.x * cosAngle - offset.z * sinAngle,
        offset.y,
        offset.x * sinAngle + offset.z * cosAngle
    );

    float3 dir = normalize(rotatedOffset);
    float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);

    //yes the wind and the angular are somehow conflic...but lmao i dgaf
    //no joking,but that's the best effect for now if you don't want to see the pile of shit in northern pole
    // Strong east/west wind (full strength)
    spherical.x += cloudOffset.x;
    
    float latFactor = sin(spherical.y * 3.14159265);  
    spherical.y += cloudOffset.z * 0.25 * latFactor;  

    float2 planetMap = PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);

    // 方案 B: game stock Clouds cubemap as the global distribution shape.
    // stockEff == 0 keeps the exact previous behavior (pure fallback).
    // Uniform branch: when the feature is off (or no cubemap is bound) we skip
    // the cubemap fetch entirely so there is zero added cost vs the original.
    float4 stock = 0.0;
    if (useStockCloudMap > 0.5)
    {
        stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
    }
    float stockEff = useStockCloudMap * stockMapStrength;
    // 兜底(方案 B):load 时检测该星球游戏各云层是否真实存在(R/G/B=低/中/高,A=遮罩)。
    // 某层不存在(valid=0)→ 该层回退到老 Volken 的 planetMap;遮罩不存在 → mask 置中性。
    float selValid = lerp(lerp(stockLayerValid.x, stockLayerValid.y, step(0.5, stockMapLayer)), stockLayerValid.z, step(1.5, stockMapLayer));
    float4 valid = lerp(selValid.xxxx, float4(stockLayerValid.x, stockLayerValid.y, stockLayerValid.z, stockLayerValid.x), step(2.5, stockMapLayer));
    float stockMaskValid = stockLayerValid.w;
    float stockMask = lerp(1.0, stock.a, stockMaskInfluence * stockEff * stockMaskValid);
    // 方案 B layer source: 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
    float selChannel = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
    float4 stockBandRaw = lerp(selChannel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer));
    float4 stockBand = stockBandRaw * stockMask;

    // 方案 A:区域内密度缩放(默认 1 → 恒等 → 现状逐字节一致;0 仍 0 → 足迹边缘保留)
    float4 stockCov = stockBand * stockDensityScale;

    // Layer1/3/4 use density (planetMap.r), Layer2 uses height (planetMap.g)
    // Stock replaces the data source: R=low, G=mid, B=high, A=mask.
    // valid==0 的层保持 planetMap(老 Volken 行为)
    float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockCov, stockEff * valid);
    float4 layers;
    layers.x = cloudLayerStrengths.x * mapVal.x;
    layers.y = cloudLayerStrengths.y * mapVal.y;
    layers.z = cloudLayerStrengths.z * mapVal.z;
    layers.w = cloudLayerStrengths.w * mapVal.w;

    float4 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
    float4 falloff = exp(-falloffExponent * falloffExponent);
    
    // 方案 B: gate the 3D shape by the stock distribution per band
    float4 active = step(0.0001, cloudLayerStrengths);
    float4 dist = lerp(float4(1.0, 1.0, 1.0, 1.0), stockBand, stockEff);
    // Maximum uneroded shape; keep a margin at the density boundary and fall back for unknown textures.
    float shapeUpper = 1.0 + max(0.0, -detailStrength);
    float upper = shapeUpper * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                + layers.x * falloff.x + layers.y * falloff.y
                + layers.z * falloff.z + layers.w * falloff.w + cloudCoverage - 1.0;
    float margin = 1e-4 * (1.0 + abs(cloudCoverage) + dot(abs(layers), falloff) + shapeUpper * dot(dist, falloff));
    [branch] if (_CloudDensityBounds > 0.5 && upper < -margin) return 0.0;

    // Domain warping: small sinusoidal perturbation to break up visible tiling
    float3 warpOffset = float3(
        sin(rotatedOffset.y * 0.05 + rotatedOffset.z * 0.03),
        cos(rotatedOffset.x * 0.04 - rotatedOffset.z * 0.05),
        sin(rotatedOffset.x * 0.03 + rotatedOffset.y * 0.04)
    ) * 0.15;
    float3 warpedUv = rotatedOffset + warpOffset;

    float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, warpedUv * cloudScale, noiseLod.x);
    float detail = CloudDetailTex.SampleLevel(samplerCloudDetailTex, warpedUv * detailScale, noiseLod.y);
    shape -= (1.0 - shape) * (1.0 - shape) * detailStrength * detail;

    float totalDensity = shape * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                       + layers.x * falloff.x + layers.y * falloff.y
                       + layers.z * falloff.z + layers.w * falloff.w;
    
    return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
}

// approximate the light that reaches the given point
float2 SampleLightRay(float3 pos, float2 noiseLod) {
    float3 rayPos = pos;
    float3 rayDir = -lightDir;

    float2 surfIntersect = RaySphereIntersect(rayPos, rayDir, surfaceRadius);
    if (surfIntersect.y > 0.0) {
        return 0.0;
    }

    float2 intersect = RaySphereIntersect(rayPos, rayDir, surfaceRadius + maxCloudHeight);
    float step = lightStepSize;
    int lightSamples = min(numLightSamplePoints, ceil((intersect.y - max(0.0, intersect.x)) / step));

    float d = 0.0;

    [loop]
    for (int i = 0; i < lightSamples; i++) {
        rayPos += step * rayDir;
        d += step * max(0.0, SampleDensityCheap(rayPos, noiseLod));
        // Once exp(-tau) < 1.3e-14, further samples have no visible direct contribution.
        if (numLightSamplePoints > 2 && cloudAbsorption > 0.0 && d * cloudAbsorption >= 32.0) break;
    }

    return float2(d, intersect.y - max(0.0, intersect.x));
}

// 完整球壳光程的固定预算查询;不使用视线 LOD、环境亮度下限或 12km 光程截断。
float CloudSunTransmittance(float3 pos, float3 direction)
{
    float2 shell = RaySphereIntersect(pos, direction, surfaceRadius + maxCloudHeight);
    float start = max(0.0, shell.x), end = shell.y;
    float2 ground = RaySphereIntersect(pos, direction, surfaceRadius);
    if (ground.x > 0.0) end = min(end, ground.x);
    if (end <= start || cloudAbsorption <= 0.0 || cloudDensity <= 0.0) return 1.0;
    float segment = (end - start) / 96.0;
    float tau = 0.0;
    [loop] for (int j = 0; j < 96; j++)
    {
        float3 samplePos = pos + direction * (start + (j + 0.5) * segment);
        tau += max(0.0, SampleDensity(samplePos, 1.0, float2(0, 0))) * segment * cloudAbsorption;
        if (tau >= 20.0) break;
    }
    return exp(-min(tau, 20.0));
}

// 33³ 格点上的有限光程消光;与体积密度共用固定 mip 0。
float TraceLightningOpticalDepth(float3 pos)
{
    float3 delta = _LightningPositionRange.xyz - pos;
    float distance = length(delta);
    if (_LightningPositionRange.w <= 0.0 || _LightningSamples < 2) return 20.0;
    if (distance >= _LightningPositionRange.w) return 20.0;
    if (distance <= 0.001) return 0.0;
    float3 dir = delta / distance;
    float2 ground = RaySphereIntersect(pos, dir, surfaceRadius);
    if (ground.y > 0.001 && max(ground.x, 0.0) < distance) return 20.0;
    float2 shell = RaySphereIntersect(pos, dir, surfaceRadius + maxCloudHeight);
    float segment = min(distance, max(0.0, shell.y)) / min(_LightningSamples, 16);
    float tau = 0.0;
    [loop] for (int j = 0; j < min(_LightningSamples, 16); j++)
    {
        float3 samplePos = pos + dir * ((j + 0.5) * segment);
        tau += max(0.0, SampleDensityCheap(samplePos, float2(0.0, 0.0))) * segment * max(0.0, cloudAbsorption);
        if (tau >= 20.0) return 20.0;
    }
    return tau;
}

float LightningShadowSlice(float2 grid, float slice)
{
    float2 tile = float2(fmod(slice, 9.0), floor(slice / 9.0));
    float2 uv = (tile * 33.0 + grid + 0.5) / float2(297.0, 132.0);
    return _LightningShadowTex.SampleLevel(sampler_LightningShadowTex, uv, 0);
}

float3 SampleLightningRadiance(float3 pos, float2 noiseLod)
{
    float range = _LightningPositionRange.w;
    [branch] if (range <= 0.0 || _LightningSamples < 2) return 0.0;
    float3 delta = _LightningPositionRange.xyz - pos;
    float distSq = dot(delta, delta);
    [branch] if (distSq >= range * range) return 0.0;
    float normalizedDistSq = distSq / (range * range);
    float edge = 1.0 - normalizedDistSq;
    float gain = _LightningColorIntensity.w * edge * edge / (1.0 + 25.0 * normalizedDistSq);
    if (gain <= 0.001) return 0.0;
    float distance = sqrt(distSq);
    if (distance > 0.001)
    {
        float2 ground = RaySphereIntersect(pos, delta / distance, surfaceRadius);
        if (ground.y > 0.001 && max(ground.x, 0.0) < distance) return 0.0;
    }
    float3 grid = saturate((pos - _LightningPositionRange.xyz) / (2.0 * range) + 0.5) * 32.0;
    float slice = floor(grid.z);
    float tau = lerp(LightningShadowSlice(grid.xy, slice),
        LightningShadowSlice(grid.xy, min(32.0, slice + 1.0)), frac(grid.z));
    return gain * _LightningColorIntensity.rgb * exp(-tau);
}
#endif
