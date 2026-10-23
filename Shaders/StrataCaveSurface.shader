// The surface of Strata's cave meshes (tunnels, caverns, arches and overhangs), drawn by plain MeshRenderers that
// StrataCaves makes from the baked tile data. Each vertex carries the palette layers of its feature's walls and floor,
// how much it is floor, and how much it blends into the ground's own splat layers near the surface, so a cave mouth
// continues the terrain around it. Walls are textured triplanar from the world's palette (StrataSurface.hlsl) with the
// rock strata beds of the cliffs above, and lit with URP's PBR lighting like the terrain.
Shader "Strata/CaveSurface"
{
    Properties
    {
        [Header(Projection)]
        _TriplanarStart ("Triplanar From (normal y)", Range(0, 1)) = 0.8
        _TriplanarEnd ("Triplanar Full (normal y)", Range(0, 1)) = 0.6
        _TriplanarSharpness ("Triplanar Sharpness", Range(1, 16)) = 6
        [Header(Anti Tiling)]
        _FarStart ("Far Tiling From (m)", Float) = 25
        _FarEnd ("Far Tiling Full (m)", Float) = 120
        _MacroScale ("Macro Variation Size (m)", Float) = 90
        _MacroStrength ("Macro Variation Strength", Range(0, 0.5)) = 0.15
        [Header(Debug)]
        [Enum(Off, 0, Layers, 1, Projection, 2, Strata, 3)] _DebugView ("Debug View", Float) = 0
        _GridSpacing ("Grid Spacing (m)", Float) = 32
        _GridStrength ("Grid Strength", Range(0, 1)) = 0
        [HideInInspector] _StrataGrid ("Grid", Vector) = (0, 0, 512, 1)
        [HideInInspector] _StrataHeightParams ("Height", Vector) = (0, 1, 512, 1)
        [HideInInspector] _StrataLod ("Lod", Vector) = (192, 0.8, 1, 0)
        [HideInInspector] _StrataHeights ("Heights", 2DArray) = "" {}
        [HideInInspector] _StrataSplatParams ("Splat", Vector) = (256, 1, 0, 0)
        [HideInInspector] _StrataSplatIndices ("Splat Indices", 2DArray) = "" {}
        [HideInInspector] _StrataSplatWeights ("Splat Weights", 2DArray) = "" {}
        [HideInInspector] _StrataLayerAlbedo ("Layer Albedo", 2DArray) = "" {}
        [HideInInspector] _StrataLayerNormal ("Layer Normal", 2DArray) = "" {}
        [HideInInspector] _StrataLayerMask ("Layer Mask", 2DArray) = "" {}
        [HideInInspector] _StrataRockMap ("Rock Map", 2DArray) = "" {}
        [HideInInspector] _StrataFarHeights ("Far Heights", 2D) = "black" {}
        [HideInInspector] _StrataFarSplatIndices ("Far Splat Indices", 2D) = "black" {}
        [HideInInspector] _StrataFarSplatWeights ("Far Splat Weights", 2D) = "red" {}
        [HideInInspector] _StrataFar ("Far", Vector) = (64, 64, 128, 128)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }

        HLSLINCLUDE
        #pragma target 4.5
        #define STRATA_CUSTOM_ATTRIBUTES
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "StrataCommon.hlsl"

        // the layer bytes (wall, floor) and weights (floor, ground blend) of StrataCaveData's vertices
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 surface : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StrataGI.hlsl"
            #include "StrataSurface.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                nointerpolation float2 layers : TEXCOORD2;
                float2 weights : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.layers = input.surface.xy;
                output.weights = input.surface.zw;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // The wall and floor layers (as much floor as the vertex says), giving way near the surface to the ground's own
            // splat layers; a vertex with no wall layer shows only the ground's.
            StrataLayers CaveLayers(float2 ids, float2 weights, float3 positionWS)
            {
                StrataLayers layers;
                layers.index = -1;
                layers.weight = 0.0;
                int wall = (int)round(ids.x * 255.0);
                int floorLayer = (int)round(ids.y * 255.0);
                float blend = wall == 255 ? 1.0 : weights.y;
                if (floorLayer == 255) floorLayer = wall;
                if (blend < 0.999)
                {
                    AddLayer(layers, wall, (1.0 - weights.x) * (1.0 - blend));
                    AddLayer(layers, floorLayer, weights.x * (1.0 - blend));
                }
                if (blend > 0.001)
                {
                    StrataLayers ground = StrataSplat(positionWS.xz);
                    [unroll] for (int k = 0; k < 4; k++)
                        if (ground.index[k] >= 0) AddLayer(layers, ground.index[k], ground.weight[k] * blend);
                }
                layers.weight /= max(dot(layers.weight, 1.0), 1e-5);
                return layers;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 geometricNormal = normalize(input.normalWS);
                float distance = length(GetCameraPositionWS() - input.positionWS);
                StrataLayers layers = CaveLayers(input.layers, input.weights, input.positionWS);
                // triplanar inside, projected like the terrain where it blends into the ground
                StrataSurfaceOutput surface = StrataSurfaceWith(layers, StrataRockAt(input.positionWS.xz), input.positionWS, geometricNormal,
                                                                distance, 1.0 - input.weights.y);
                if (_DebugView > 0.5) return half4(surface.albedo * (0.6 + 0.4 * saturate(geometricNormal.y)), 1.0);

                float3 cells = input.positionWS / _GridSpacing;
                float3 lines = 1.0 - saturate(abs(frac(cells - 0.5) - 0.5) / max(fwidth(cells), 1e-5));
                surface.albedo *= 1.0 - _GridStrength * max(lines.x, max(lines.y, lines.z)) * 0.6;

                InputData inputData = (InputData)0;
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

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = surface.albedo;
                surfaceData.metallic = surface.metallic;
                surfaceData.smoothness = surface.smoothness;
                surfaceData.occlusion = surface.occlusion;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.alpha = 1.0;

                half4 colour = UniversalFragmentPBR(inputData, surfaceData);
                colour.rgb = MixFog(colour.rgb, inputData.fogCoord);
                colour.a = 1.0;
                return colour;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "StrataCaveDepthNormals.hlsl"
            ENDHLSL
        }

        Pass
        {
            // what deferred asks forward-only objects for
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "StrataCaveDepthNormals.hlsl"
            ENDHLSL
        }
    }
}
