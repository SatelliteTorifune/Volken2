#ifndef VOLKEN_FOG_COMMON
#define VOLKEN_FOG_COMMON
float _VolkenFogEnabled, _VolkenFogVolumeEnabled, _VolkenFogLookupEnabled;
float4 _VolkenFogLookupSize;
float4 _VolkenFogOrigin, _VolkenFogUpRadius, _VolkenFogHeight;
float4 _VolkenFogRange, _VolkenFogAmbient, _VolkenFogDirect, _VolkenFogSun, _VolkenFogLight;
float4 _VolkenFogGrid, _VolkenFogNoiseOffset, _VolkenFogShape;
float4x4 _VolkenFogProjection, _VolkenFogInvProjection, _VolkenFogCameraToWorld, _VolkenFogWorldToCamera;
float4x4 _VolkenFogPlanetRotation;

float3 VolkenFogRay(float2 uv)
{
    float4 p = mul(_VolkenFogInvProjection, float4(uv * 2 - 1, 0.5, 1));
    return normalize(mul((float3x3)_VolkenFogCameraToWorld, p.xyz / p.w));
}

float VolkenFogAltitude(float3 delta)
{
    float r = _VolkenFogUpRadius.w + _VolkenFogOrigin.w;
    float change = 2 * r * dot(_VolkenFogUpRadius.xyz, delta) + dot(delta, delta);
    return _VolkenFogOrigin.w + change / max(1, sqrt(max(1, r*r + change)) + r);
}

// Bounds a ray by the fog shell, the planet surface, and the configured distance.
float2 VolkenFogIntervalRadial(float cosine, float distance)
{
    float2 interval = float2(_VolkenFogRange.x, min(distance, _VolkenFogRange.y));
    float r = _VolkenFogUpRadius.w, h = _VolkenFogOrigin.w;
    float b = (r+h) * cosine;
    float top = max(0, _VolkenFogHeight.x + _VolkenFogHeight.y * 12);
    float c = (h-top) * (2*r+h+top);
    float discriminant = b*b-c;
    if (discriminant < 0) return 0;
    float root = sqrt(max(0, discriminant));
    float q = -b - (b >= 0 ? root : -root);
    float a = abs(q) > 1e-5 ? c/q : -b-root;
    interval.x = max(interval.x, min(a,q));
    interval.y = min(interval.y, max(a,q));
    float groundC = h * (2*r+h);
    float groundD = b*b-groundC;
    if (h >= 0 && b < 0 && groundD > 0)
    {
        float ground = groundC / max(1e-5, -b + sqrt(groundD));
        interval.y = min(interval.y, ground);
    }
    return float2(max(0, interval.x), max(0, interval.y));
}

float2 VolkenFogInterval(float3 ray, float distance)
{
    return VolkenFogIntervalRadial(dot(_VolkenFogUpRadius.xyz,ray),distance);
}

float VolkenFogAltitudeRadial(float cosine, float d)
{
    float r = _VolkenFogUpRadius.w + _VolkenFogOrigin.w;
    float change = 2*r*cosine*d + d*d;
    return _VolkenFogOrigin.w + change / max(1,sqrt(max(1,r*r+change))+r);
}

float VolkenFogExpAverage(float q)
{
    return q < 0.001 ? 1-q*0.5+q*q/6-q*q*q/24 : (1-exp(-q))/q;
}

float VolkenFogSegment(float ha, float hb, float length)
{
    float H = _VolkenFogHeight.y;
    ha -= _VolkenFogHeight.x; hb -= _VolkenFogHeight.x;
    if (max(ha,hb) <= 0) return length;
    float constantLength = 0;
    if (min(ha,hb) < 0)
    {
        constantLength = length * (-min(ha,hb)) / max(1e-6, abs(ha-hb));
        ha = max(0,ha); hb = max(0,hb);
    }
    return constantLength + (length-constantLength) * exp(-min(ha,hb)/H)
        * VolkenFogExpAverage(abs(ha-hb)/H);
}

float VolkenFogOpticalDepthRadial(float cosine, float distance)
{
    if (_VolkenFogHeight.z <= 0) return 0;
    float2 span = VolkenFogIntervalRadial(cosine, distance);
    float length = max(0, span.y-span.x);
    if (length <= 0) return 0;
    int steps = clamp((int)ceil(length / sqrt(max(1, 0.08 * _VolkenFogUpRadius.w * _VolkenFogHeight.y))), 1, 32);
    float ds = length / steps;
    float ha = VolkenFogAltitudeRadial(cosine,span.x);
    float tau = 0;
    [loop] for (int i=0; i<steps; ++i)
    {
        float hb = VolkenFogAltitudeRadial(cosine,span.x + (i+1)*ds);
        tau += VolkenFogSegment(ha,hb,ds);
        ha = hb;
    }
    return tau * _VolkenFogHeight.z;
}

float VolkenFogOpticalDepth(float3 ray, float distance)
{
    return VolkenFogOpticalDepthRadial(dot(_VolkenFogUpRadius.xyz,ray),distance);
}

float3 VolkenFogLighting(float3 ray)
{
    float g = _VolkenFogLight.x;
    float phase = (1-g*g) / pow(max(0.05, 1+g*g-2*g*dot(ray,_VolkenFogSun.xyz)), 1.5);
    return _VolkenFogAmbient.rgb + min(4,phase)*_VolkenFogDirect.rgb;
}

#ifndef VOLKEN_FOG_COMPUTE
sampler3D _VolkenFogVolume;
sampler2D _VolkenFogLookup;

float4 VolkenFogAtRay(float2 uv, float distance, float3 ray)
{
    if (_VolkenFogEnabled < 0.5) return float4(0,0,0,1);
    [branch] if (_VolkenFogVolumeEnabled > 0.5)
    {
        float eye = max(0, -mul((float3x3)_VolkenFogWorldToCamera, ray).z) * distance;
        float z = (sqrt(saturate(eye/_VolkenFogRange.y))*_VolkenFogGrid.z+0.5)/(_VolkenFogGrid.z+1);
        return tex3Dlod(_VolkenFogVolume, float4(saturate(uv),z,0));
    }
    float tau;
    [branch] if (_VolkenFogLookupEnabled > 0.5)
    {
        // Cubic angle and squared distance concentrate precision near the horizon/camera.
        float cosine = clamp(dot(_VolkenFogUpRadius.xyz,ray),-1,1);
        float angle = sign(cosine)*pow(abs(cosine),1.0/3.0)*0.5+0.5;
        float z = sqrt(saturate(distance/_VolkenFogRange.y));
        float2 lookupUV = (float2(angle,z)*(_VolkenFogLookupSize.xy-1)+0.5)/_VolkenFogLookupSize.xy;
        tau = tex2Dlod(_VolkenFogLookup,float4(lookupUV,0,0)).r;
        // At sea level the tangent/ground boundary is discontinuous; never interpolate across it.
        if (_VolkenFogOrigin.w == 0 && cosine < 0) tau = 0;
    }
    else tau = VolkenFogOpticalDepth(ray,distance);
    float T = max(1-_VolkenFogRange.z, exp(-tau));
    return float4(VolkenFogLighting(ray)*(1-T), T);
}

float4 VolkenFogAt(float2 uv, float distance)
{
    [branch] if (_VolkenFogEnabled < 0.5) return float4(0,0,0,1);
    return VolkenFogAtRay(uv,distance,VolkenFogRay(uv));
}

float4 VolkenFogAtWorld(float3 world)
{
    if (_VolkenFogEnabled < 0.5) return float4(0,0,0,1);
    // Nested reflection / other cameras must never consume a previous camera's volume.
    float3 actualUp = float3(UNITY_MATRIX_V._m10, UNITY_MATRIX_V._m11, UNITY_MATRIX_V._m12);
    float3 actualRight = float3(UNITY_MATRIX_V._m00, UNITY_MATRIX_V._m01, UNITY_MATRIX_V._m02);
    if (distance(_WorldSpaceCameraPos,_VolkenFogOrigin.xyz)>0.05 ||
        distance(actualUp,_VolkenFogWorldToCamera[1].xyz)>0.001 ||
        distance(actualRight,_VolkenFogWorldToCamera[0].xyz)>0.001)
        return float4(0,0,0,1);
    float3 delta = world-_VolkenFogOrigin.xyz;
    float3 view = mul((float3x3)_VolkenFogWorldToCamera,delta);
    float4 clip = mul(_VolkenFogProjection, float4(view,1));
    if (clip.w <= 0) return float4(0,0,0,1);
    float d = length(delta);
    return VolkenFogAtRay(clip.xy/clip.w*0.5+0.5,d,delta/max(0.0001,d));
}
#endif
#endif
