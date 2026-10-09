Shader "Hidden/Volken/HeightFog"
{
    Properties { _MainTex("Source",2D)="white"{} _FogSceneDepth("Scene depth",2D)="white"{} _FogBuild("Fog build",Float)=3 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "FogCommon.cginc"
            sampler2D _MainTex, _FogSceneDepth;
            float _FogDebug;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 source = tex2D(_MainTex,i.uv);
                float eye = tex2D(_FogSceneDepth,i.uv).r;
                float3 ray = VolkenFogRay(i.uv);
                float d = eye/max(0.001,-mul((float3x3)_VolkenFogWorldToCamera,ray).z);
                float4 fog = VolkenFogAtRay(i.uv,d,ray);
                if (_FogDebug>2.5) return float4(fog.rgb,1);
                if (_FogDebug>1.5) return float4(saturate(eye/_VolkenFogRange.y).xxx,1);
                if (_FogDebug>0.5) return float4(fog.aaa,1);
                return float4(source.rgb*fog.a+fog.rgb,source.a);
            }
            ENDCG
        }
        Pass
        {
            Name "HeightLookup"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragLookup
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "FogCommon.cginc"
            float fragLookup(v2f_img i) : SV_Target
            {
                float2 t = floor(i.uv*_VolkenFogLookupSize.xy)/(_VolkenFogLookupSize.xy-1);
                float x = t.x*2-1;
                return VolkenFogOpticalDepthRadial(x*x*x,t.y*t.y*_VolkenFogRange.y);
            }
            ENDCG
        }
    }
    Fallback Off
}
