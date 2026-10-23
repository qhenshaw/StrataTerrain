// The lit passes of Strata/TreeImpostor: the forward pass and, for deferred rendering, the GBuffer pass, which share the
// surface (crossfade, the frame's albedo, normal and foliage, the per-tree tint, probe lighting) and the light through
// the leaves (StrataTranslucency.hlsl). Included after URP's Lighting.hlsl and StrataGI.hlsl; the GBuffer pass defines
// STRATA_GBUFFER first.
#ifndef STRATA_TREE_IMPOSTOR_PASSES_INCLUDED
#define STRATA_TREE_IMPOSTOR_PASSES_INCLUDED

#ifdef STRATA_GBUFFER
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#endif
#include "StrataTranslucency.hlsl"

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    nointerpolation float3 origin : TEXCOORD2;
    nointerpolation float2 sinCos : TEXCOORD3;
    nointerpolation float fade : TEXCOORD4;
    float fogFactor : TEXCOORD5;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    Impostor impostor = PlaceImpostor(input, _WorldSpaceCameraPos, true);
    output.positionWS = impostor.positionWS;
    output.positionCS = TransformWorldToHClip(impostor.positionWS);
    output.uv = impostor.uv;
    output.origin = impostor.origin;
    output.sinCos = impostor.sinCos;
    output.fade = impostor.fade;
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    return output;
}

// The impostor's surface and lighting inputs at a pixel (after its crossfade and cutout clips), and its foliage.
float StrataImpostorInput(Varyings input, out InputData inputData, out SurfaceData surfaceData)
{
    StrataFadeClip(input.positionCS.xy, input.fade);
    half4 albedo = SampleAlbedo(input.uv);
    half4 packed = SAMPLE_TEXTURE2D(_Normals, sampler_Normals, input.uv);
    float3 normalWS = normalize(StrataRotateY(packed.rgb * 2.0 - 1.0, input.sinCos));
    float foliage = packed.a;
    albedo.rgb = StrataTreeTint(albedo.rgb, input.origin, foliage, _HueVariation, _BrightnessVariation) * _Brightness;

    inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
        inputData.shadowCoord = ComputeScreenPos(input.positionCS);
    #else
        inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    #endif
    inputData.fogCoord = input.fogFactor;
    inputData.bakedGI = StrataBakedGI(inputData.positionWS, inputData.normalWS, inputData.viewDirectionWS, input.positionCS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = 1;

    surfaceData = (SurfaceData)0;
    surfaceData.albedo = albedo.rgb;
    surfaceData.smoothness = _Smoothness;
    surfaceData.occlusion = 1.0;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.alpha = 1.0;
    return foliage;
}

half4 FragForward(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    float foliage = StrataImpostorInput(input, inputData, surfaceData);
    half4 colour = UniversalFragmentPBR(inputData, surfaceData);
    colour.rgb += StrataTranslucency(surfaceData.albedo, inputData.normalWS, foliage, inputData.shadowCoord);
    colour.rgb = MixFog(colour.rgb, inputData.fogCoord);
    colour.a = 1.0;
    return colour;
}

#ifdef STRATA_GBUFFER
// As URP's Lit GBuffer pass, with the light through the leaves added to the lighting buffer's starting value, like
// Strata/Tree's, so impostors match the meshes they replace.
GBufferFragOutput FragGBuffer(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    float foliage = StrataImpostorInput(input, inputData, surfaceData);
    #if defined(_DBUFFER)
        ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
    #endif

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular, surfaceData.smoothness, surfaceData.alpha, brdfData);
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
    half3 gi = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                  inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
    gi += StrataTranslucency(surfaceData.albedo, inputData.normalWS, foliage, inputData.shadowCoord);
    return PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness, gi, surfaceData.occlusion);
}
#endif

#endif
