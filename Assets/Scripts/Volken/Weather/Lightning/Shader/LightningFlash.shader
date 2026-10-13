// 落点球状闪光的单 Pass 材质;与主干材质分离。
// 加色混合、ZWrite Off、ZTest Always;_Intensity 控制闪光强度。

Shader "Hidden/Volken/LightningFlash"
{
    Properties
    {
        _Color     ("Color", Color) = (0.8, 0.85, 1.0, 1.0)
        _Intensity ("Intensity", Float) = 0.0
        _CoreWidth ("Core Width", Range(0.0, 1.0)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Flash"
            Blend SrcAlpha One        // 加色
            ZWrite Off
            ZTest Always              // 照亮整个视野,不受几何遮挡
            Cull Off
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "../../Fog/Shader/FogCommon.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float3 fogWorld : TEXCOORD1;
            };

            float4 _Color;
            float  _Intensity;
            float  _CoreWidth;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.fogWorld = mul(unity_ObjectToWorld,v.vertex).xyz;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d = i.uv - 0.5;
                float r = saturate(length(d) * 2.0);
                float core = 1.0 - smoothstep(0.0, max(1e-4, _CoreWidth), r);
                float halo = 1.0 - smoothstep(_CoreWidth, 1.0, r);
                float glow = core + halo * 0.25;

                float3 c = lerp(_Color.rgb, float3(1, 1, 1), core);
                float a = saturate(glow * _Intensity * 0.05);
                return fixed4(c * a * VolkenFogAtWorld(i.fogWorld).a, a);
            }
            ENDCG
        }
    }

    Fallback "Hidden/Internal-Colored"
}
