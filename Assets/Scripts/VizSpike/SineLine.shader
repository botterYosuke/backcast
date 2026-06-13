Shader "VizSpike/SineLine"
{
    // issue #8 viz-spike (Phase 4): procedural line-strip fed by a StructuredBuffer<float>
    // of zero-copy numpy samples. No vertex buffer; vertex positions come from SV_VertexID
    // (X = id/(count-1) mapped to NDC, Y = _Buf[id]) so a single draw call renders the sine.
    Properties { }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "VizSpikeLine"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float> _Buf;
            int _Count;

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                float denom = max((float)(_Count - 1), 1.0);
                float x = (float)vertexID / denom;   // 0..1 across the buffer
                float y = _Buf[vertexID];            // sine sample, -1..1
                // Output directly in clip space (w=1 => NDC): X to [-1,1], Y scaled to fit.
                o.positionCS = float4(x * 2.0 - 1.0, y * 0.9, 0.0, 1.0);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return half4(0.25, 1.0, 0.45, 1.0);
            }
            ENDHLSL
        }
    }
}
