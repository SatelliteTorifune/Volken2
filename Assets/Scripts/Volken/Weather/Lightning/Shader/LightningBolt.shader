// 闪电主干与分叉的单 Pass 材质:_Intensity 控制发光强度。
// 加色混合、关闭深度写入、保留深度测试;落点闪光使用 LightningFlash.shader。
// 生命周期与材质所有权见 docs/weather.md。

Shader "Hidden/Volken/LightningBolt"
{
    Properties
    {
        _Color     ("Color", Color) = (0.85, 0.9, 1.0, 1.0)
        _Intensity ("Intensity", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Bolt"
            Blend SrcAlpha One        // 加色:src * srcAlpha + dst
            ZWrite Off
            ZTest LEqual
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
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color  : COLOR;
                float3 fogWorld : TEXCOORD0;
            };

            float4 _Color;
            float  _Intensity;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.fogWorld = mul(unity_ObjectToWorld,v.vertex).xyz;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 c = i.color.rgb;
                c = lerp(c, float3(1, 1, 1), saturate(_Intensity / 50.0) * 0.85);
                float a = i.color.a * _Intensity;
                return fixed4(c * _Intensity * VolkenFogAtWorld(i.fogWorld).a, a);
            }
            ENDCG
        }
    }

    Fallback "Hidden/Internal-Colored"
}
