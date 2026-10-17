// The depth-normals passes of Strata/TreeImpostor; included inside its passes.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation float2 sinCos : TEXCOORD1;
    nointerpolation float fade : TEXCOORD2;
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    Impostor impostor = PlaceImpostor(input, _WorldSpaceCameraPos, true);
    output.positionCS = TransformWorldToHClip(impostor.positionWS);
    output.uv = impostor.uv;
    output.sinCos = impostor.sinCos;
    output.fade = impostor.fade;
    return output;
}

half4 Frag(Varyings input) : SV_Target
{
    StrataFadeClip(input.positionCS.xy, input.fade);
    SampleAlbedo(input.uv);
    half4 packed = SAMPLE_TEXTURE2D(_Normals, sampler_Normals, input.uv);
    float3 normalWS = normalize(StrataRotateY(packed.rgb * 2.0 - 1.0, input.sinCos));
    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octahedral = PackNormalOctQuadEncode(normalWS);
        return half4(PackFloat2To888(saturate(octahedral * 0.5 + 0.5)), 0.0);
    #else
        return half4(normalWS, 0.0);
    #endif
}
