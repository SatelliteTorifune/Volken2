Shader "Volken/RainSplashes"
{
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "../../Fog/Shader/FogCommon.cginc"
            StructuredBuffer<float4> _SplashPositions;
            StructuredBuffer<float4> _SplashNormals;
            float _Lifetime, _Size, _Fade, _MaxDistance;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float age : TEXCOORD1; float fade : TEXCOORD2; float water : TEXCOORD3; float3 fogWorld : TEXCOORD4; };
            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float4 splash = _SplashPositions[instanceID];
                float4 surface = _SplashNormals[instanceID];
                float age = saturate(splash.w / max(0.01, _Lifetime));
                float3 n = surface.xyz;
                float3 reference = abs(n.y) < 0.95 ? float3(0,1,0) : float3(1,0,0);
                float3 right = normalize(cross(n, reference));
                float3 forward = cross(n, right);
                float distance = length(splash.xyz - _WorldSpaceCameraPos);
                bool alive = surface.w < 0.5 && splash.w >= 0.0 && splash.w < _Lifetime && distance < _MaxDistance;
                float size = _Size * lerp(0.35, 1.0, age) * (alive ? 1.0 : 0.0);
                float3 local = (right * v.vertex.x + forward * v.vertex.y) * size;
                o.pos = UnityWorldToClipPos(splash.xyz + local);
                o.fogWorld = splash.xyz+local;
                o.uv = v.uv * 2.0 - 1.0;
                o.age = age;
                o.fade = (alive ? _Fade : 0.0) * saturate((_MaxDistance - distance) / max(1.0, _MaxDistance * 0.2));
                o.water = surface.w;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float radius = length(i.uv);
                float ring = 1.0 - smoothstep(0.035, 0.12, abs(radius - 0.72));
                float angle = atan2(i.uv.y, i.uv.x);
                float droplets = pow(saturate(cos(angle * 7.0 + i.age * 2.0)), 14.0);
                float crown = droplets * (1.0 - smoothstep(0.10, 0.25, abs(radius - 0.88)));
                float alpha = max(ring * lerp(0.65, 1.0, i.water), crown * (1.0 - i.age)) * (1.0 - i.age);
                clip(alpha - 0.005);
                float4 fog = VolkenFogAtWorld(i.fogWorld);
                return fixed4(float3(0.72,0.82,0.95)*fog.a+fog.rgb, alpha*i.fade*0.65);
            }
            ENDCG
        }
    }
    Fallback Off
}
