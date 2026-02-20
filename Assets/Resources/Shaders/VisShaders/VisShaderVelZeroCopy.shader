Shader "Custom/VisShaderVel"
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

            TEXTURE3D(_VelSimTex);
            SAMPLER(sampler_VelSimTex);
            
            TEXTURE2D(_ColorMapTex);
            SAMPLER(sampler_ColorMapTex);

            CBUFFER_START(UnityPerMaterial)
                float _YSlice;
                float _MinVel;
                float _MaxVel;
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
                
                float4 velVec = SAMPLE_TEXTURE3D(_VelSimTex, sampler_VelSimTex, uvw);
                
                float speed = length(velVec.xyz);
                float normalizedSpeed = saturate((speed - _MinVel) / (_MaxVel - _MinVel));
                
                float4 finalColor = SAMPLE_TEXTURE2D(_ColorMapTex, sampler_ColorMapTex, float2(normalizedSpeed, 0.5));
                
                return float4(finalColor.xyz, 1.0);
            }
            ENDHLSL
        }
    }
}