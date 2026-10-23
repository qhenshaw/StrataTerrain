// Light through leaves for Strata's tree and impostor shaders: the main light seen from behind a surface, scaled by its
// foliage and the shader's Translucency, and shadowed. Forward passes add it to their lit colour; GBuffer passes add it
// to the lighting buffer's starting value, so deferred trees look the same. Included after URP's Lighting.hlsl.
#ifndef STRATA_TRANSLUCENCY_INCLUDED
#define STRATA_TRANSLUCENCY_INCLUDED

half3 StrataTranslucency(half3 albedo, float3 normalWS, float foliage, float4 shadowCoord)
{
    Light light = GetMainLight(shadowCoord);
    half back = saturate(dot(-normalWS, light.direction) * 0.5 + 0.5);
    return albedo * light.color * (back * back * _Translucency * foliage * light.shadowAttenuation);
}

#endif
