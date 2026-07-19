// Fullscreen crash flash (spec §13 / §7.6): flash + chromatic aberration + vignette.
// Uniforms pinned by CrashSequence: _FlashIntensity, _Distortion, _VignetteIntensity,
// _FlashColor. Applied to a fullscreen UI RawImage/Image material.
Shader "LightRunners/ScreenCrashFlash"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _FlashIntensity ("Flash Intensity", Range(0, 1)) = 0
        _Distortion ("Chromatic Distortion", Range(0, 0.1)) = 0.02
        _VignetteIntensity ("Vignette Intensity", Range(0, 2)) = 0.8
        _FlashColor ("Flash Color", Color) = (1, 0, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "CrashFlash"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _FlashIntensity;
                float _Distortion;
                float _VignetteIntensity;
                half4 _FlashColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 center = IN.uv - 0.5;
                float dist = length(center);

                // Chromatic aberration: offset R and B samples radially.
                float2 offset = center * _Distortion * _FlashIntensity;
                half r = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv + offset).r;
                half g = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).g;
                half b = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv - offset).b;
                half3 tex = half3(r, g, b);

                // Vignette darkening toward the corners, scaled with the flash.
                float vignette = saturate(dist * _VignetteIntensity * (0.5 + _FlashIntensity));

                half3 col = lerp(tex, _FlashColor.rgb, _FlashIntensity * 0.7);
                col *= (1.0 - vignette * 0.8);

                half alpha = saturate(_FlashIntensity * (0.6 + 0.4 * vignette));
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
