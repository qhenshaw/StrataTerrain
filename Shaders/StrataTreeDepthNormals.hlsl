// The depth-normals passes of Strata/Tree; included inside its passes.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 normalWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    nointerpolation float fade : TEXCOORD2;
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    StrataTreeSpace space = StrataTreeInstance();
    output.positionCS = TransformWorldToHClip(WindyPosition(input, space));
    output.normalWS = StrataTreeNormalToWorld(space, input.normalOS);
    output.uv = input.uv;
    output.fade = space.fade;
    return output;
}

half4 Frag(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
{
    StrataFadeClip(input.positionCS.xy, input.fade);
    AlphaOnly(input.uv);
    float3 normalWS = normalize(input.normalWS) * (frontFace ? 1.0 : -1.0);
    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octahedral = PackNormalOctQuadEncode(normalWS);
        return half4(PackFloat2To888(saturate(octahedral * 0.5 + 0.5)), 0.0);
    #else
        return half4(normalWS, 0.0);
    #endif
}
