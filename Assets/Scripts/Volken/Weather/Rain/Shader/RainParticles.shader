// BIRP 实例化雨丝;_RotationMatrix 在参考帧空间构轴,列 1 承载长轴与拉伸。
// 逐粒长宽变化与滚转由 _StreakVariation / _StreakRoll 控制,滚转默认关闭。
// 软粒子读取 RFloat 线性眼空间深度(米);深度未就绪时 _InvFade=0。
// 网格 UV 为 0..1,uv.y=0 为亮头、1 为尾部;渲染位置来自 _Positions。
// 坐标和重定位约束见 docs/archive/sp2-rain-particledomain-port-2026-09-28.md §10。

Shader "Volken/RainParticles"
{
    Properties
    {
        _MainTex ("Main Texture (RGB, A=alpha)", 2D) = "white" {}
        _Emission ("Emission Boost", Range(0, 3)) = 0
        _MainColor ("Main Color", Color) = (0.72, 0.82, 1, 1)
        _InvFade ("Soft Particles Factor", Range(0.01, 3)) = 1
        _Falloff ("Tail Fade", Range(0, 1)) = 0.9
        _DistanceFade ("Distance Fade (depth cue)", Range(0, 1)) = 0.35
        _StreakVariation ("Per-streak Size Variation", Range(0, 1)) = 0.5
        _StreakRoll ("Per-streak Roll", Range(0, 1)) = 0
        _MinStreakWidthPx ("Min On-screen Streak Width (px)", Range(0, 4)) = 0
        _ShaderVer ("Shader Build (部署探针)", Float) = 9
        _EdgeFade ("Domain Edge Fade", Range(0, 0.5)) = 0.2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0   // StructuredBuffer 需要 SM5

            #include "UnityCG.cginc"
            #include "../../Fog/Shader/FogCommon.cginc"

            StructuredBuffer<float4> _Positions;   // 与 compute 里 _Positions 同一 buffer(xyz=帧空间位置)
            StructuredBuffer<float4> _RandomData;  // 同一 buffer 的读视图:yzw = 逐粒长/宽倍率与滚转
            StructuredBuffer<float4> _RainCollisionData;
            uint _RainCollisionEnabled;

            sampler2D _MainTex;
            fixed4 _MainColor;
            float _Emission;
            float _InvFade;
            float _Falloff;
            float _FadeAmount;         // 环境淡出因子
            float4x4 _RotationMatrix;  // 世界系构轴(C# 每帧按 AlignStreaks 算;列1 已含拉伸)
            float3 _DomainPos;         // 球域中心(=相机位置;SP2 material 同名)
            float _DomainRadius;       // 球域半径
            float _EdgeFade;           // 边界淡出宽度比例(0 = 关)
            float _DistanceFade;
            float _StreakVariation;
            float _StreakRoll;
            float _MinStreakWidthPx;
            float3 _SeaCenter;         // 海平面球心(帧空间,与 _Positions 同空间)
            float _SeaRadius;          // 海平面半径(米;0 = 行星无水 → 不做水下剔除)
            float3 _SeaUp;             // 海平面法线(径向向上)= −down;截断雨丝用

            sampler2D _LinearSceneDepth;   // CloudRenderer.combinedDepthTex(RFloat, LinearEyeDepth 米)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screenPos : TEXCOORD1;   // 深度采样用(ComputeScreenPos)
                float eyeDepth : TEXCOORD2;     // 粒子自身线性眼空间深度(正米)
                float edgeFade : TEXCOORD3;     // 域边界淡出系数(顶点算好插值;粒子级量,插值基本恒定)
                float3 fogWorld : TEXCOORD4;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float3 local = mul((float3x3)_RotationMatrix, v.vertex.xyz);
                float4 raw = _RandomData[instanceID];
                bool rndBad = !(raw.x == raw.x && raw.y == raw.y && raw.z == raw.z && raw.w == raw.w);   // NaN != NaN
                float4 rnd = rndBad ? float4(0.5, 0.5, 0.5, 0.5) : clamp(raw, 0.0, 1.0);
                float lenScale = 1.0 + (rnd.y * 2.0 - 1.0) * _StreakVariation * 0.5;
                float widScale = 1.0 + (rnd.z * 2.0 - 1.0) * _StreakVariation * 0.35;
                if (_MinStreakWidthPx > 0.0 && _ScreenParams.y >= 8.0)
                {
                    float m11 = abs(UNITY_MATRIX_P._m11);
                    float halfW = max(abs(v.vertex.x), abs(v.vertex.z));
                    float camDist = clamp(length(_Positions[instanceID].xyz - _WorldSpaceCameraPos), 0.2, 500.0);
                    if (m11 > 1e-3 && halfW > 1e-5)
                    {
                        float pxPerMeter = _ScreenParams.y * 0.5 * m11 / camDist;
                        float wantWorldW = _MinStreakWidthPx / pxPerMeter;
                        widScale = min(widScale * max(1.0, wantWorldW / (2.0 * halfW * widScale)), widScale * 8.0 + 4.0);
                    }
                }
                float2 xz = float2(local.x * widScale, local.z * widScale);
                float roll = rnd.w * 6.2831853 * _StreakRoll;
                float rollCos = cos(roll);
                float rollSin = sin(roll);
                local = float3(xz.x * rollCos - xz.y * rollSin, local.y * lenScale, xz.x * rollSin + xz.y * rollCos);

                if (_SeaRadius > 0.0 && _RainCollisionEnabled == 0u)
                {
                    float3 partPos = _Positions[instanceID].xyz;
                    float seaAlt = length(partPos - _SeaCenter) - _SeaRadius;
                    if (seaAlt < 0.0)
                    {
                        local = 0.0;
                    }
                    else
                    {
                        float3 axis = float3(_RotationMatrix._m01, _RotationMatrix._m11, _RotationMatrix._m21);
                        float axisUp = dot(axis, _SeaUp);
                        if (abs(axisUp) > 1e-3 && seaAlt + local.y * axisUp < 0.0)
                        {
                            local.y = -seaAlt / axisUp;
                        }
                    }
                }
                if (_RainCollisionEnabled != 0u)
                {
                    float4 surface = _RainCollisionData[instanceID];
                    if (surface.w < 0.0) local = 0.0;
                    else
                    {
                        float clearance = surface.w + dot(local, surface.xyz);
                        if (clearance < 0.0) local -= surface.xyz * clearance;
                    }
                }
                float4 worldPos = float4(_Positions[instanceID].xyz + local, 1.0);

                o.pos = UnityWorldToClipPos(worldPos);
                o.fogWorld = worldPos.xyz;
                o.uv = v.uv;      // 全 0..1,不平铺(uv.y 0=头(亮)→1=尾(淡))
                o.screenPos = ComputeScreenPos(o.pos);
                o.eyeDepth = -mul(UNITY_MATRIX_V, worldPos).z;   // 正米,与 combinedDepthTex 同约定

                float radial = length(_Positions[instanceID].xyz - _DomainPos);
                float safeR = max(1e-3, _DomainRadius);
                float edge = (_EdgeFade > 0.0001)
                    ? saturate((1.0 - radial / safeR) / max(1e-3, _EdgeFade))
                    : 1.0;
                o.edgeFade = edge * (1.0 - _DistanceFade * saturate(radial / safeR));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _MainColor;
                col.rgb *= 1.0 + _Emission;
                col.a *= _FadeAmount;
                col.a *= i.edgeFade;   // 域边界淡出(隐藏球域边界/回收突现)
                col.a *= 1.0 - saturate(i.uv.y * _Falloff);
                if (_InvFade > 0.0)
                {
                    float sceneDepth = tex2D(_LinearSceneDepth, i.screenPos.xy / i.screenPos.w).r;  // 米
                    col.a *= saturate((sceneDepth - i.eyeDepth) * _InvFade);
                }
                float4 fog = VolkenFogAtWorld(i.fogWorld);
                col.rgb = col.rgb*fog.a+fog.rgb;
                return col;
            }
            ENDCG
        }
    }
}
