#ifndef VOLKEN_CLOUD_EMPTY_SPACE_INCLUDED
#define VOLKEN_CLOUD_EMPTY_SPACE_INCLUDED

float _CloudEmptySpaceEnabled;
float4 _CloudEmptyBandMin, _CloudEmptyBandMax;

float CloudEmptyMaxComponent(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

// 高度到支持带的距离是空区的保守空间半径；min > max 表示该带处处低于阈值。
float CloudEmptyDistance(float height)
{
    float4 distance = max(_CloudEmptyBandMin - height, height - _CloudEmptyBandMax);
    distance.x = _CloudEmptyBandMin.x <= _CloudEmptyBandMax.x ? distance.x : 1e20;
    distance.y = _CloudEmptyBandMin.y <= _CloudEmptyBandMax.y ? distance.y : 1e20;
    distance.z = _CloudEmptyBandMin.z <= _CloudEmptyBandMax.z ? distance.z : 1e20;
    distance.w = _CloudEmptyBandMin.w <= _CloudEmptyBandMax.w ? distance.w : 1e20;
    return min(min(distance.x, distance.y), min(distance.z, distance.w));
}

// 仅重演旧路径的空样本状态，保留步进相位、回退规则和 350 次预算。
bool CloudTrySkipEmptyInterval(float3 camPos, float3 viewDir, float maxRayDist,
    inout float rayDist, inout float localStepSize, inout float stepSizeMultiplier,
    inout int emptySamples, inout int iter)
{
    [branch] if (_CloudEmptySpaceEnabled < 0.5) return false;
    if (!(stepSize > 0.0 && stepSize < 1e20 && abs(stepSizeFalloff) < 1e20) ||
        !(localStepSize > 0.0 && localStepSize < 1e20) ||
        !(stepSizeMultiplier == 1.0 || stepSizeMultiplier == 2.0) ||
        !all(abs(camPos) < 1e18) || !all(abs(sphereCenter) < 1e18) ||
        !(abs(rayDist) < 1e18 && abs(maxRayDist) < 1e18 && abs(surfaceRadius) < 1e18))
        return false;

    float directionLength = length(viewDir);
    if (!(directionLength > 1e-6 && directionLength < 1e6)) return false;
    float3 firstPosition = camPos + rayDist * viewDir;
    if (!all(abs(firstPosition - sphereCenter) < 1e18)) return false;
    float height = length(firstPosition - sphereCenter) - surfaceRadius;
    float coordinateScale = CloudEmptyMaxComponent(abs(camPos)) + CloudEmptyMaxComponent(abs(sphereCenter))
        + max(abs(rayDist), abs(maxRayDist)) * directionLength + abs(surfaceRadius);
    float roundingGuard = max(0.5, coordinateScale * 2e-6);
    float emptyDistance = CloudEmptyDistance(height) - roundingGuard;
    if (!(emptyDistance > 0.0)) return false;

    float firstRayDist = rayDist;
    bool skipped = false;
    [loop] for (int skippedSample = 0; skippedSample < 32; skippedSample++)
    {
        if (iter >= 350 || rayDist >= maxRayDist ||
            (rayDist - firstRayDist) * directionLength >= emptyDistance) break;
        if (stepSizeMultiplier == 1.0)
        {
            emptySamples++;
            stepSizeMultiplier = emptySamples > 3 ? 2.0 : 1.0;
        }
        localStepSize = stepSize * clamp(stepSizeFalloff * 1e-5 * rayDist, 1.0, 2.0);
        rayDist += localStepSize * stepSizeMultiplier;
        iter++;
        skipped = true;
    }
    return skipped;
}

#endif
