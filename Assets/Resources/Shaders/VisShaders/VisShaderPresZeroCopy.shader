Shader "Custom/VisShaderPres"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="HDRenderPipeline" }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

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

            TEXTURE3D(_PresSimTex);
            SAMPLER(sampler_PresSimTex);
            
            TEXTURE2D(_ColorMapTex);
            SAMPLER(sampler_ColorMapTex);

            CBUFFER_START(UnityPerMaterial)
                float _YSlice;
                float _MinPres;
                float _MaxPres;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                
                output.uv = input.uv;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float3 uvw = float3(input.uv.x, _YSlice, input.uv.y);
                
                float pres = SAMPLE_TEXTURE3D(_PresSimTex, sampler_PresSimTex, uvw);
                
                float normalizedPres = saturate((pres - _MinPres) / (_MaxPres - _MinPres));
                
                float4 finalColor = SAMPLE_TEXTURE2D(_ColorMapTex, sampler_ColorMapTex, float2(normalizedPres, 0.5));
                
                return finalColor;
            }
            ENDHLSL
        }
    }
}