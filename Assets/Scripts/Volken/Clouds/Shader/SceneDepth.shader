Shader "Hidden/Volken/SceneDepth"
{
    Properties { _MainTex("Far depth",2D)="white"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
        float _DepthFarClip, _HasFarDepth;
        bool HasDepth(float raw)
        {
            #if defined(UNITY_REVERSED_Z)
            return raw>0;
            #else
            return raw<1;
            #endif
        }
        float4 Far(v2f_img i):SV_Target
        {
            float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,i.uv);
            return HasDepth(raw) ? LinearEyeDepth(raw) : _DepthFarClip;
        }
        float4 Near(v2f_img i):SV_Target
        {
            float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,i.uv);
            return HasDepth(raw) ? LinearEyeDepth(raw) : (_HasFarDepth>0.5 ? tex2D(_MainTex,i.uv).r : _DepthFarClip);
        }
        ENDCG
        Pass
        {
            Name "FarDepth"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Far
            ENDCG
        }
        Pass
        {
            Name "NearDepth"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Near
            ENDCG
        }
    }
    Fallback Off
}
