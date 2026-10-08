Shader "Hidden/Volken/RainCollisionDepth"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Mode ("Standard blend mode", Float) = 0
        _BaseBlendModeDestination ("JNO base destination blend", Float) = 0
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex;
    float4 _MainTex_ST;
    float _Cutoff, _Mode, _BaseBlendModeDestination;
    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f { float4 pos : SV_POSITION; float eye : TEXCOORD0; float2 uv : TEXCOORD1; };
    v2f vert(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.eye = -UnityObjectToViewPos(v.vertex).z;
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        return o;
    }
    float frag(v2f i) : SV_Target
    {
        // Missing properties in replacement rendering can read as zero. Both JNO's
        // opaque destination blend and Standard's opaque mode are zero; transparent
        // JNO parts use destination=10, and Standard Fade/Transparent use modes 2/3.
        if (_BaseBlendModeDestination > 0.5 || _Mode > 1.5) discard;
        if (_Mode > 0.5 && _Mode < 1.5) clip(tex2D(_MainTex, i.uv).a - _Cutoff);
        return i.eye;
    }
    float fragCutout(v2f i) : SV_Target { clip(tex2D(_MainTex, i.uv).a - _Cutoff); return i.eye; }
    ENDCG
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite On ZTest LEqual Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            ENDCG
        }
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" }
        Pass
        {
            ZWrite On ZTest LEqual Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCutout
            #pragma target 3.0
            ENDCG
        }
    }
    Fallback Off
}
