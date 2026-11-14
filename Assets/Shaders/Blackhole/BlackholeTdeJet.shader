Shader "Blackhole/TDE Polar Jet"
{
    Properties
    {
        _Intensity ("HDR Intensity", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float spine = exp(-pow((input.uv.x - 0.5) * 5.0, 2.0));
                float endFade = smoothstep(0.0, 0.12, input.uv.y) * (1.0 - smoothstep(0.86, 1.0, input.uv.y));
                float intensity = _Intensity * endFade * (0.25 + 1.75 * spine);
                return half4(float3(0.24, 0.62, 1.0) * intensity, intensity);
            }
            ENDHLSL
        }
    }
}
