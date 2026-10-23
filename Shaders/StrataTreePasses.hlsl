// The lit passes of Strata/Tree: the forward pass and, for deferred rendering, the GBuffer pass, which share the surface
// (crossfade, albedo with the per-tree tint and ground colour, two-sided normals, normal map, smoothness, vertex
// occlusion, probe lighting) and the light through the leaves (StrataTranslucency.hlsl). Included after URP's
// Lighting.hlsl and StrataGI.hlsl; the GBuffer pass defines STRATA_GBUFFER first.
#ifndef STRATA_TREE_PASSES_INCLUDED
#define STRATA_TREE_PASSES_INCLUDED

#ifdef STRATA_GBUFFER
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#endif
#include "StrataTranslucency.hlsl"

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float4 tangentWS : TEXCOORD2;
    float2 uv : TEXCOORD3;
    half4 colour : TEXCOORD4;
    nointerpolation float4 tree : TEXCOORD5;     // origin, foliage
    nointerpolation float fade : TEXCOORD6;
    float fogFactor : TEXCOORD7;
    half4 ground : TEXCOORD8;       // ground colour, and how much of it
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    StrataTreeSpace space = StrataTreeInstance();
    output.positionWS = WindyPosition(input, space);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.normalWS = StrataTreeNormalToWorld(space, input.normalOS);
    output.tangentWS = float4(mul(space.rotation, input.tangentOS.xyz), input.tangentOS.w);
    output.uv = input.uv;
    output.colour = input.color;
    output.tree = float4(space.origin, Foliage(input));
    output.fade = space.fade;
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    if (_GroundTint > 0.0 || _GroundMultiply > 0.0)
        output.ground = half4(StrataGroundColour(space.origin.xz),
                              _GroundTint * saturate(1.0 - input.positionOS.y / max(_GroundTintHeight, 0.01)));
    return output;
}

// The tree's surface and lighting inputs at a pixel (after its crossfade and cutout clips).
void StrataTreeInput(Varyings input, bool frontFace, out InputData inputData, out SurfaceData surfaceData)
{
    StrataFadeClip(input.positionCS.xy, input.fade);
    half4 albedo = BaseAlbedo(input.uv, input.colour);
    float foliage = input.tree.w;
    albedo.rgb = StrataTreeTint(albedo.rgb, input.tree.xyz, foliage, _HueVariation, _BrightnessVariation);
    if (_GroundMultiply > 0.0) albedo.rgb *= input.ground.rgb * _GroundMultiply;
    albedo.rgb = lerp(albedo.rgb, input.ground.rgb, input.ground.a);

    float3 normalWS = normalize(input.normalWS) * (frontFace ? 1.0 : -1.0);
    if (_BumpScale > 0.0 && dot(input.tangentWS.xyz, input.tangentWS.xyz) > 1e-6)
    {
        float3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw), _BumpScale);
        float3 tangent = normalize(input.tangentWS.xyz);
        float3 bitangent = cross(normalWS, tangent) * input.tangentWS.w;
        normalWS = normalize(mul(normalTS, float3x3(tangent, bitangent, normalWS)));
    }

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
    if (_SmoothnessSource > 0.5)
    {
        half mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_BaseMap, input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw).a;
        surfaceData.smoothness = lerp(_SmoothnessRemap.x, _SmoothnessRemap.y, _SmoothnessSource > 1.5 ? 1.0 - mask : mask);
    }
    surfaceData.occlusion = _VertexOcclusion > 0.5 ? input.colour.r : 1.0;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.alpha = 1.0;
}

half4 FragForward(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
{
    // under BRG this also loads the ambient probe
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    StrataTreeInput(input, frontFace, inputData, surfaceData);
    half4 colour = UniversalFragmentPBR(inputData, surfaceData);
    colour.rgb += StrataTranslucency(surfaceData.albedo, inputData.normalWS, input.tree.w, inputData.shadowCoord);
    colour.rgb = MixFog(colour.rgb, inputData.fogCoord);
    colour.a = 1.0;
    return colour;
}

#ifdef STRATA_GBUFFER
// As URP's Lit GBuffer pass, with the light through the leaves added to the lighting buffer's starting value (the
// tree's probe and reflection lighting), so deferred lights only add the ordinary direct lighting on top.
GBufferFragOutput FragGBuffer(Varyings input, bool frontFace : SV_IsFrontFace)
{
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    StrataTreeInput(input, frontFace, inputData, surfaceData);
    #if defined(_DBUFFER)
        ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
    #endif

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular, surfaceData.smoothness, surfaceData.alpha, brdfData);
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
    half3 gi = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                  inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
    gi += StrataTranslucency(surfaceData.albedo, inputData.normalWS, input.tree.w, inputData.shadowCoord);
    return PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness, gi, surfaceData.occlusion);
}
#endif

#endif
