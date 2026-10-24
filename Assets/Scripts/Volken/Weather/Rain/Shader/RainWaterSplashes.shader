Shader "Volken/RainWaterSplashes"
{
    Properties
    {
        [HideInInspector] _ZTest("Depth test", Float) = 4
        [HideInInspector] _WaterSplashVersion("Water splash version", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent+30" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off ZTest [_ZTest]
        Pass
        {
            Name "WaterImpact"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "../../Fog/Shader/FogCommon.cginc"
            StructuredBuffer<float4> _SplashPositions;
            StructuredBuffer<float4> _SplashNormals;
            sampler2D _SplashSceneDepth;
            float _Lifetime, _Size, _Fade, _MaxDistance, _SplashSceneDepthReady;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float age : TEXCOORD1;
                float fade : TEXCOORD2;
                float spray : TEXCOORD3;
                float3 world : TEXCOORD4;
                float4 screen : TEXCOORD5;
                float eyeDepth : TEXCOORD6;
            };
            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float4 splash = _SplashPositions[instanceID];
                float4 surface = _SplashNormals[instanceID];
                float age = saturate(splash.w / max(0.01, _Lifetime));
                float3 n = surface.xyz;
                n = dot(n,n) > 0.5 ? normalize(n) : float3(0,1,0);
                float3 reference = abs(n.y) < 0.95 ? float3(0,1,0) : float3(1,0,0);
                float3 right = normalize(cross(n, reference));
                float3 forward = cross(n, right);
                float distance = length(splash.xyz - _WorldSpaceCameraPos);
                bool alive = surface.w > 0.5 && splash.w >= 0.0 && splash.w < _Lifetime && distance < _MaxDistance;
                float size = _Size * (alive ? 1.0 : 0.0);
                float3 local;
                // Mesh z selects a surface ripple (0) or a vertical, camera-facing spray (1).
                if (v.vertex.z > 0.5)
                {
                    float3 sprayRight = cross(n, _WorldSpaceCameraPos - splash.xyz);
                    sprayRight = dot(sprayRight,sprayRight) > 1e-6 ? normalize(sprayRight) : right;
                    float height = size * (0.12 + 1.5 * sin(age * 3.14159265));
                    float width = size * lerp(0.3, 1.0, age);
                    local = sprayRight * v.vertex.x * width + n * ((v.vertex.y + 1.0) * 0.5 * height);
                }
                else
                {
                    float radius = size * lerp(0.4, 2.2, age);
                    local = (right * v.vertex.x + forward * v.vertex.y) * radius;
                }
                o.world = splash.xyz + local;
                o.pos = UnityWorldToClipPos(o.world);
                o.screen = ComputeScreenPos(o.pos);
                o.eyeDepth = -mul(UNITY_MATRIX_V, float4(o.world,1)).z;
                o.uv = v.uv * 2.0 - 1.0;
                o.age = age;
                o.spray = v.vertex.z;
                o.fade = (alive ? _Fade : 0.0) * saturate((_MaxDistance - distance) / max(1.0, _MaxDistance * 0.2));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                clip(i.fade - 0.001);
                float alpha;
                float3 color;
                if (i.spray > 0.5)
                {
                    float y = i.uv.y * 0.5 + 0.5;
                    float width = lerp(0.1, 0.82, y);
                    float aa = max(0.025, fwidth(i.uv.x));
                    float crown = 1.0 - smoothstep(aa, aa * 2.0, abs(abs(i.uv.x) - width));
                    crown *= smoothstep(0.0,0.1,y) * (1.0-smoothstep(0.65,0.95,y));
                    float2 bead = float2((abs(i.uv.x)-0.65)/0.13, (y-0.82)/0.12);
                    float droplets = 1.0 - smoothstep(0.5,1.0,length(bead));
                    alpha = max(crown,droplets) * (1.0-smoothstep(0.25,0.7,i.age));
                    color = float3(0.86,0.94,1.0);
                }
                else
                {
                    float radius = length(i.uv);
                    float aa = max(0.018, fwidth(radius));
                    float outer = 1.0-smoothstep(aa,aa*2.0,abs(radius-0.72));
                    float inner = (1.0-smoothstep(aa,aa*2.0,abs(radius-0.42))) * (1.0-i.age);
                    float shadow = (1.0-smoothstep(aa,aa*2.0,abs(radius-0.79))) * 0.45;
                    float highlight = max(outer,inner*0.65);
                    alpha = max(highlight,shadow) * (1.0-i.age);
                    color = lerp(float3(0.12,0.22,0.3),float3(0.78,0.9,1.0),saturate(highlight*2.0));
                }
                alpha *= i.fade * 0.85;
                [branch] if (_SplashSceneDepthReady > 0.5)
                {
                    float depth = tex2Dproj(_SplashSceneDepth, UNITY_PROJ_COORD(i.screen)).r;
                    alpha *= saturate((depth-i.eyeDepth)/0.04);
                }
                clip(alpha - 0.005);
                float4 fog = VolkenFogAtWorld(i.world);
                return fixed4(color*fog.a+fog.rgb, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
