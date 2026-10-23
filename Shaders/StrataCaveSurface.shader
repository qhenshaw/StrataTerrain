// The surface of Strata's cave meshes (tunnels, caverns, arches and overhangs), drawn by plain MeshRenderers that
// StrataCaves makes from the baked tile data. Each vertex carries the palette layers of its feature's walls and floor,
// how much it is floor, and how much it blends into the ground's own splat layers near the surface, so a cave mouth
// continues the terrain around it. Walls are textured triplanar from the world's palette (StrataSurface.hlsl) with the
// rock strata beds of the cliffs above (StrataCavePasses.hlsl), lit with URP's PBR lighting under forward rendering and
// written into the GBuffer under deferred, like the terrain.
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
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragForward
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
            #include "StrataCavePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            // what deferred rendering draws instead of ForwardLit: the surface into the GBuffer
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma exclude_renderers gles3 glcore
            #pragma vertex Vert
            #pragma fragment FragGBuffer
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #define STRATA_GBUFFER
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StrataGI.hlsl"
            #include "StrataSurface.hlsl"
            #include "StrataCavePasses.hlsl"
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
