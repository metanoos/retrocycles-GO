// Neon trail (spec §13): emissive additive line with a soft core. Uniforms pinned by
// NeonTrailRenderer / ARTrailObject: _BaseColor, _EmissionColor, _Width.
Shader "LightRunners/NeonTrailEnhanced"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0, 1, 1, 1)
        _EmissionColor ("Emission Color", Color) = (0, 2, 2, 1)
        _Width ("Width", Float) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "NeonTrail"
            Blend SrcAlpha One          // additive glow
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                float _Width;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Bright core fading to the edges of the line strip (v across the width).
                float edge = abs(IN.uv.y - 0.5) * 2.0;      // 0 center → 1 edge
                float core = saturate(1.0 - edge);
                float glow = core * core;                    // quadratic falloff

                half3 col = _BaseColor.rgb * IN.color.rgb + _EmissionColor.rgb * glow;
                half alpha = _BaseColor.a * IN.color.a * saturate(glow + 0.15);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
