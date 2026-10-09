// Volken 天气系统 —— 落雷点的球状闪光(SP2 的 planeMat)。
//
// 【为什么与主干分开成一个 shader】见 <c>LightningBolt.shader</c> 顶部说明:
// 一个 Material 只用 Shader 的第一个匹配 Pass,而原先把"线"和"球状光晕"塞在同一个
// 双 Pass shader 里,导致闪光球一直在跑线的那套逻辑(本 shader 的 _CoreWidth / halo 形同虚设)。
// 拆开之后每个 shader 只有一个 Pass,**不存在"选错 Pass"这种可能**。
//
// 【表现】加色、ZWrite Off、**ZTest Always** —— 它表达的是"整个视野被照亮",
// 不该被任何几何体遮挡(原版 Enviro 也是这个语义)。
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
                // 球面 UV 到中心的径向衰减(球体网格的 UV 在极点收敛,直接算径向即可)
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

    // 理由同 LightningBolt.shader:Fallback Off 会让"shader 不可用"直接变成
    // Unity 的 Error shader(淡紫),既难看又掩盖真正的问题。
    Fallback "Hidden/Internal-Colored"
}
