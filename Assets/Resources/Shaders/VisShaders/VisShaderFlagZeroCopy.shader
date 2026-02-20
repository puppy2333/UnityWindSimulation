Shader "Custom/VisShaderFlag"
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

            Texture3D<int> _FlagSimTex;

            CBUFFER_START(UnityPerMaterial)
                float _YSlice;
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
                uint nx, ny, nz;
                _FlagSimTex.GetDimensions(nx, ny, nz);

                int3 voxelCoords = int3(
                    (int)(input.uv.x * nx),
                    (int)(_YSlice * ny),
                    (int)(input.uv.y * nz)
                );
                int flagValue = _FlagSimTex.Load(int4(voxelCoords, 0));

                // if (flagMag > 0.9 && flagMag < 1.1)
                // {
                //     flagMag = 1.0;
                // }
                // else
                // {
                //     flagMag = 0.0;
                // }
                
                float colorIntensity = 0.0;
                if (flagValue == 1)
                {
                    colorIntensity = 1.0;
                }

                return float4(colorIntensity, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}