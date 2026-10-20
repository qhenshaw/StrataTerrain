// Baked indirect light for Strata's forward passes (included after URP's Lighting.hlsl): the scene's Adaptive Probe
// Volumes where it has them (StrataProbeBaker bakes them against proxies of the terrain and trees), else the ambient
// probe. Per-vertex SH modes keep the ambient probe, as there is no vertex value to hand over.
#ifndef STRATA_GI_INCLUDED
#define STRATA_GI_INCLUDED

half3 StrataBakedGI(float3 positionWS, half3 normalWS, half3 viewDirWS, float4 positionCS)
{
#if (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)) && !defined(EVALUATE_SH_VERTEX) && !defined(EVALUATE_SH_MIXED)
    return SampleProbeVolumePixel(half3(0, 0, 0), positionWS, normalWS, viewDirWS, positionCS.xy);
#else
    return SampleSH(normalWS);
#endif
}

#endif
