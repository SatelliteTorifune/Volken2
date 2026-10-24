Shader "Hidden/Volken/RainLens"
{
    Properties
    {
        _MainTex ("Scene", 2D) = "white" {}
        _DropMask ("Drop mask", 2D) = "black" {}
        _ShaderVer ("Shader Build", Float) = 2
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "Mask"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 data : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 data : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = float4(v.vertex.xy, 0, 1);
                #if UNITY_UV_STARTS_AT_TOP
                    o.pos.y = -o.pos.y;
                #endif
                o.uv = v.uv;
                o.data = v.data;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float2 q = i.uv * 2 - 1;
                float r2 = dot(q,q);
                clip(1 - r2);
                float height = sqrt(saturate(1 - r2));
                float2 along = i.data.xy;
                float2 across = float2(along.y, -along.x);
                float2 normal = (across*q.x + along*q.y) / max(0.35,height);
                normal = clamp(normal,-1,1) * lerp(0.75,0.25,i.data.w);
                float alpha = (1-smoothstep(0.80,1,r2)) * i.data.z;
                alpha *= lerp(1, smoothstep(0,0.7,i.uv.y), i.data.w);
                return float4(normal * 0.5 + 0.5, height, alpha);
            }
            ENDCG
        }
        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _DropMask;
            float4 _MainTex_TexelSize;
            float _RefractionPx, _BlurPx;
            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float ySign = 1;
                #if UNITY_UV_STARTS_AT_TOP
                    if (_MainTex_TexelSize.y < 0) { uv.y = 1-uv.y; ySign = -1; }
                #endif
                float4 original = tex2D(_MainTex,uv);
                float4 mask = tex2D(_DropMask,i.uv);
                if (mask.a < 0.001) return original;
                float2 texel = abs(_MainTex_TexelSize.xy);
                float coverage = saturate(mask.a);
                // 遮罩 RGB 已按覆盖度混合,恢复局部几何,淡出只由最终 coverage 施加。
                float2 normal = clamp((mask.rg * 2 - 1) / max(coverage,0.06),-1,1);
                float height = saturate(mask.b / max(coverage,0.06));
                float2 offset = normal * _RefractionPx * texel;
                offset.y *= ySign;
                float2 edge = texel * 0.5;
                float2 refractedUV = clamp(uv + offset,edge,1-edge);
                float2 blur = texel * _BlurPx * height;
                float3 refracted = tex2D(_MainTex,refractedUV).rgb;
                if (_BlurPx > 0.001)
                {
                    refracted = refracted * 0.4
                        + tex2D(_MainTex,clamp(refractedUV+float2(blur.x,0),edge,1-edge)).rgb * 0.15
                        + tex2D(_MainTex,clamp(refractedUV-float2(blur.x,0),edge,1-edge)).rgb * 0.15
                        + tex2D(_MainTex,clamp(refractedUV+float2(0,blur.y),edge,1-edge)).rgb * 0.15
                        + tex2D(_MainTex,clamp(refractedUV-float2(0,blur.y),edge,1-edge)).rgb * 0.15;
                }
                float rim = 1-smoothstep(0.25,0.75,height);
                float2 direction = normal / max(0.001,length(normal));
                float litSide = saturate(dot(direction,float2(-0.55,0.83)));
                float darkSide = saturate(dot(direction,float2(0.55,-0.83)));
                float sceneLight = dot(max(original.rgb,refracted),float3(0.2126,0.7152,0.0722));
                refracted *= 1 - rim * (0.18 + darkSide * 0.36);
                // 高光只借用局部场景亮度,黑背景仍为黑。
                refracted += sceneLight * rim * litSide * 0.65;
                return float4(lerp(original.rgb,refracted,coverage),original.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
