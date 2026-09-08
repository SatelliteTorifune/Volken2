Shader "Jundroo/PlanetRingShader_Fixed"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _localLightDirection ("Local Light Direction", Vector) = (1,0,0,0)
        _planetRadius ("Planet Radius", Float) = 0.5
        _innerRadius ("Inner Radius", Float) = 0.6
        _outerRadius ("Outer Radius", Float) = 0.95
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.05
        _Brightness ("Brightness", Float) = 1.0
    }
    SubShader
    {
        // 改为不透明渲染队列
        Tags { "RenderType"="Opaque" "Queue"="Geometry+50" }  // 在几何体之后，透明物体之前
        LOD 200

        Cull Off
        ZWrite On  // 保持深度写入
        
        // 移除Stencil设置或改为不同的处理方式

        Pass
        {
            // 启用Alpha测试而不是Alpha混合
            AlphaTest Greater [_Cutoff]
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            float4 _localLightDirection;
            float _planetRadius;
            float _innerRadius;
            float _outerRadius;
            float _Cutoff;
            float _Brightness;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 centered = i.uv - 0.5;
                float dist = length(centered) * 2.0;

                // 环形剔除
                if (dist < _planetRadius || dist < _innerRadius || dist > _outerRadius)
                {
                    discard;
                }

                fixed4 col = tex2D(_MainTex, i.uv);
                
                // 使用clip而不是返回alpha值
                clip(col.a - _Cutoff);
                
                // 光照计算
                float3 radialDir = normalize(float3(centered.x, 0, centered.y));
                float lighting = saturate(dot(radialDir, _localLightDirection.xyz)) * _Brightness;
                col.rgb *= lerp(0.5, 1.0, lighting);
                
                // 返回不透明颜色
                return fixed4(col.rgb, 1.0);  // 完全不透明
            }
            ENDCG
        }
    }
}
