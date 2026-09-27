// Volken 天气系统 —— 闪电材质(BIRP 原生,不依赖任何管线特性)。
//
// 【为什么自己写】SP2 用的 Enviro 闪电材质是商业资产包内的,按计划 §3 决策不引入 Enviro3,
// 因此按 <c>Enviro.Lightning</c> 对材质的实际要求重写:只要一个可被 C# 反复
// <c>SetFloat("_Intensity", ...)</c> 的发光强度即可(每段随机 0~2、闪光瞬间 50、淡出递减)。
//
// 【Pass 说明】
//   Pass "Bolt"   —— 闪电主干/分叉(LineRenderer 用)。加色混合、关深度写入、深度测试开
//                    (被地形/建筑挡住 —— 这是想要的:山后的闪电不该穿山)。
//   Pass "Flash"  —— 落雷点的球状闪光(SP2 的 planeMat)。加色、ZTest Always,
//                    因为它表达的是"整个视野被照亮",不该被任何几何体遮挡。
Shader "Hidden/Volken/LightningBolt"
{
    Properties
    {
        _Color     ("Color", Color) = (0.85, 0.9, 1.0, 1.0)
        _Intensity ("Intensity", Float) = 1.0
        _CoreWidth ("Core Width", Range(0.0, 1.0)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        // ===================== 闪电主干 / 分叉 =====================
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
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color  : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Color;
            float  _Intensity;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                // LineRenderer 的顶点色渐变(两端渐隐)保留下来,乘上材质色
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 核心过曝后偏白(真实闪电芯部是白的,边缘才带蓝紫)
                float3 c = i.color.rgb;
                c = lerp(c, float3(1, 1, 1), saturate(_Intensity / 50.0) * 0.85);
                float a = i.color.a * _Intensity;
                return fixed4(c * _Intensity, a);
            }
            ENDCG
        }

        // ===================== 落雷点球状闪光 =====================
        Pass
        {
            Name "Flash"
            Blend SrcAlpha One
            ZWrite Off
            ZTest Always              // 照亮整个视野,不受几何遮挡
            Cull Off
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Color;
            float  _Intensity;
            float  _CoreWidth;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
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
                return fixed4(c * a, a);
            }
            ENDCG
        }
    }

    Fallback Off
}
