// The depth-normals passes of Strata/TerrainSurface; included after StrataCommon.hlsl.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    output.positionWS = StrataPlace(input.positionOS.xyz);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    return output;
}

half4 Frag(Varyings input) : SV_Target
{
    float3 normalWS = StrataBandNormal(StrataNormal(input.positionWS.xz), input.positionWS,
                                       length(_StrataCameraPosition - input.positionWS), StrataRockAt(input.positionWS.xz));
    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octahedral = PackNormalOctQuadEncode(normalWS);
        return half4(PackFloat2To888(saturate(octahedral * 0.5 + 0.5)), 0.0);
    #else
        return half4(normalWS, 0.0);
    #endif
}
