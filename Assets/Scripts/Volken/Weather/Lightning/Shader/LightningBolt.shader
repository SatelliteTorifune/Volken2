// Volken 天气系统 —— 闪电材质:**主干 / 分叉**(BIRP 原生,不依赖任何管线特性)。
//
// 【为什么自己写】SP2 用的 Enviro 闪电材质是商业资产包内的,按计划 §3 决策不引入 Enviro3,
// 因此按 <c>Enviro.Lightning</c> 对材质的实际要求重写:只要一个可被 C# 反复
// <c>SetFloat("_Intensity", ...)</c> 的发光强度即可(每段随机 0~2、闪光瞬间 50、淡出递减)。
//
// 【2026-09-28 拆成两个 shader —— 修"闪电偶发紫红色"】
//   紫红色 = Unity 在"材质丢失 / shader 无法使用"时回退的 Error shader。
//   原先主干与落点闪光共用一个双 Pass shader,靠 Pass 名 "Bolt"/"Flash" 区分,
//   而 **一个 Material 只用 Shader 的第一个匹配 Pass**,这两个 Pass 又都没有 LightMode 标签,
//   所以:
//     - 落点闪光球实际上一直在跑 "Bolt" 那个 Pass(_CoreWidth / halo 全部没生效);
//     - C# 里想"按名字启用某个 Pass"的做法不可靠(无 LightMode 时名字不构成可区分的 Pass)。
//   现在拆成各含单一 Pass 的两个 shader,**一个 Material 只有一个 Pass 可选**,不存在歧义。
//   落点闪光见 <c>LightningFlash.shader</c>。
//
// 【Pass 说明】主干/分叉(LineRenderer 用):加色混合、关深度写入、深度测试开
//             —— 被地形/建筑挡住(这是想要的:山后的闪电不该穿山)。
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
            // 显式声明目标等级:LineRenderer 的顶点色(COLOR)与后续可能的立体渲染
            // 都需要 ≥3.0;不写会落到默认 2.5,在部分平台上出现难查的编译/表现问题。
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color  : COLOR;
            };

            float4 _Color;
            float  _Intensity;

            v2f vert(appdata v)
            {
                v2f o;
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
    }

    // 【2026-09-28】原来是 Fallback Off。Fallback Off 的含义是"没有任何后备 shader 可用",
    // 一旦本 shader 因任何原因不可用,渲染出来就是 Unity 的 Error shader(**淡紫/品红**)——
    // 正是玩家看到的现象,而且看不出是哪一步出的问题。
    // Hidden/Internal-Colored 是 Unity 内置、任何构建里都存在的最简着色器:
    // 回退到它至少能画出"一条能看见的加色亮线",并且明显不是我们想要的样子,便于定位。
    Fallback "Hidden/Internal-Colored"
}
