// The terrain surface for Strata worlds, drawn by StrataTerrainRenderer through a BatchRendererGroup. Each instance is
// a CDLOD node; the shared flat patch is placed and displaced from the world's height texture in the vertex shader,
// and the geometric normal is taken per pixel from the full-resolution heights, so coarse far patches still shade with
// all their detail. The lit passes (StrataTerrainPasses.hlsl) texture the surface from the splat maps and the
// palette's layers (StrataSurface.hlsl): forward rendering lights it with URP's PBR lighting, deferred rendering writes
// it into the GBuffer like URP's Lit. The _STRATA_HOLES variant, drawn for nodes over a cave hole, clips the hole in
// every pass.
Shader "Strata/TerrainSurface"
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
        [Header(Parallax)]
        _ParallaxStrength ("Parallax Strength", Range(0, 2)) = 1
        [Header(Debug)]
        [Enum(Off, 0, Layers, 1, Projection, 2, Strata, 3, Parallax, 4)] _DebugView ("Debug View", Float) = 0
        _GridSpacing ("Grid Spacing (m)", Float) = 32
        _GridStrength ("Grid Strength", Range(0, 1)) = 0
        [HideInInspector] _StrataNode ("Node", Vector) = (0, 0, 1, 0)
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
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile _ DOTS_INSTANCING_ON
        #pragma multi_compile_local _ _STRATA_HOLES
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "StrataCommon.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragForward
            #pragma multi_compile_fragment _ STRATA_PARALLAX
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
            #include "StrataTerrainPasses.hlsl"
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
            #pragma multi_compile_fragment _ STRATA_PARALLAX
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
            #include "StrataTerrainPasses.hlsl"
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                float3 positionWS = StrataPlace(input.positionOS.xyz);
                output.positionWS = positionWS;
                float3 normalWS = StrataNormal(positionWS.xz);
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
                StrataClipHole(input.positionWS.xz);
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
                StrataClipHole(input.positionWS.xz);
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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "StrataDepthNormals.hlsl"
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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "StrataDepthNormals.hlsl"
            ENDHLSL
        }
    }
}
