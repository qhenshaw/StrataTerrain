// The lit passes of Strata/TerrainSurface: the forward pass and, for deferred rendering, the GBuffer pass, which share
// the surface (holes clipped, splat layers, rock strata, grid lines, probe lighting). Included after URP's Lighting.hlsl,
// StrataGI.hlsl and StrataSurface.hlsl; the GBuffer pass defines STRATA_GBUFFER first.
#ifndef STRATA_TERRAIN_PASSES_INCLUDED
#define STRATA_TERRAIN_PASSES_INCLUDED

#ifdef STRATA_GBUFFER
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#endif

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float fogFactor : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings output = (Varyings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    output.positionWS = StrataPlace(input.positionOS.xyz);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    return output;
}

// The terrain's surface and lighting inputs at a pixel; false for the debug views, whose colour (unlit, so it reads
// exactly) is in debugColour.
bool StrataTerrainInput(Varyings input, out InputData inputData, out SurfaceData surfaceData, out half3 debugColour)
{
    StrataClipHole(input.positionWS.xz);
    float3 geometricNormal = StrataNormal(input.positionWS.xz);
    float distance = length(GetCameraPositionWS() - input.positionWS);
    StrataSurfaceOutput surface = StrataSurface(input.positionWS, geometricNormal, distance);
    debugColour = surface.albedo * (0.6 + 0.4 * saturate(geometricNormal.y));

    // contour and grid lines for checking the shape, anti-aliased by screen-space width
    float3 cells = input.positionWS / _GridSpacing;
    float3 lines = 1.0 - saturate(abs(frac(cells - 0.5) - 0.5) / max(fwidth(cells), 1e-5));
    surface.albedo *= 1.0 - _GridStrength * max(lines.x, max(lines.y, lines.z)) * 0.6;

    inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = surface.normalWS;
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
    surfaceData.albedo = surface.albedo;
    surfaceData.metallic = surface.metallic;
    surfaceData.smoothness = surface.smoothness;
    surfaceData.occlusion = surface.occlusion;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.alpha = 1.0;
    return _DebugView < 0.5;
}

half4 FragForward(Varyings input) : SV_Target
{
    // under BRG this also loads the ambient probe (unity_SHAr etc. read per-instance or global DOTS values)
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    half3 debugColour;
    if (!StrataTerrainInput(input, inputData, surfaceData, debugColour)) return half4(debugColour, 1.0);
    half4 colour = UniversalFragmentPBR(inputData, surfaceData);
    colour.rgb = MixFog(colour.rgb, inputData.fogCoord);
    colour.a = 1.0;
    return colour;
}

#ifdef STRATA_GBUFFER
// As URP's Lit GBuffer pass: the surface into the GBuffer, with its indirect lighting (probes and reflections) as the
// lighting buffer's starting value; the deferred pass adds the lights. Debug views go in as emission over black.
GBufferFragOutput FragGBuffer(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    InputData inputData;
    SurfaceData surfaceData;
    half3 debugColour;
    bool lit = StrataTerrainInput(input, inputData, surfaceData, debugColour);
    if (!lit)
    {
        surfaceData.albedo = 0;
        surfaceData.metallic = 0;
        surfaceData.smoothness = 0;
    }
    #if defined(_DBUFFER)
        ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
    #endif

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular, surfaceData.smoothness, surfaceData.alpha, brdfData);
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
    half3 gi = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                  inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
    if (!lit) gi = debugColour;
    return PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness, gi, surfaceData.occlusion);
}
#endif

#endif
