// Tree and bush meshes for Strata worlds, drawn by StrataTreeRenderer through a BatchRendererGroup. Each instance is
// one tree: its base, yaw, scale and lean come from the per-instance _StrataTree, and its LOD crossfade from the packed
// visible list. Albedo is the base map times the colour (times the vertex colour for vertex-coloured assets), with a
// per-tree hue and brightness shift on foliage; foliage also lets light through. Wind moves vertices by their weight
// (vertex alpha, or height up the tree). Grass can take the colour of the terrain it stands on (from the world's splats):
// Ground Multiply colours a greyscale texture by it (the scale is one over the texture's mean, so it ends up about as
// bright as the ground), and Ground Tint blends the lower part of a coloured texture towards it. Smoothness is a
// constant or comes from a map's alpha (SpeedTree extra map, HDRP mask map; or roughness, for MHER masks) remapped into
// a range. Lit with URP's PBR lighting (StrataTreePasses.hlsl: forward, or into the GBuffer under deferred, with the
// light through the leaves in the lighting buffer). The two impostor bake passes render the tree unlit into
// StrataTreeImpostor's frames (albedo and coverage; normals in the tree's own space and foliage).
Shader "Strata/Tree"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        [Toggle] _VertexColour ("Multiply By Vertex Colour (sRGB)", Float) = 0
        [Toggle] _VertexOcclusion ("Vertex Colour Is Occlusion (SpeedTree)", Float) = 0
        [Toggle] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0, 2)) = 1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.2
        [Enum(Constant, 0, Map Alpha, 1, Map Alpha Is Roughness, 2)] _SmoothnessSource ("Smoothness From", Float) = 0
        [NoScaleOffset] _MaskMap ("Smoothness Map", 2D) = "white" {}
        _SmoothnessRemap ("Smoothness Range (min, max)", Vector) = (0, 1, 0, 0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Header(Foliage)]
        [Enum(Constant, 0, UV0 X, 1)] _FoliageSource ("Foliage From", Float) = 0
        _Foliage ("Foliage (constant)", Range(0, 1)) = 0
        _Translucency ("Translucency", Range(0, 1)) = 0.35
        _HueVariation ("Hue Variation", Range(0, 0.5)) = 0.2
        _BrightnessVariation ("Brightness Variation", Range(0, 0.5)) = 0.12
        _GroundTint ("Ground Tint", Range(0, 1)) = 0
        _GroundTintHeight ("Ground Tint Height (m)", Float) = 0.5
        _GroundMultiply ("Ground Multiply (0 off, else scale)", Float) = 0
        [Header(Wind)]
        [Enum(Vertex Alpha, 0, Height, 1)] _WindSource ("Sway From", Float) = 0
        _WindHeight ("Sway Height (m)", Float) = 10
        _WindStrength ("Strength (m)", Float) = 0.25
        _WindSpeed ("Speed", Float) = 1.1
        _WindScale ("Gust Scale (1/m)", Float) = 0.035
        _WindDirection ("Direction (XZ)", Vector) = (1, 0, 0.4, 0)
        [HideInInspector] _StrataTree ("Tree", Vector) = (0, 0, 0, 64)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull [_Cull]

        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile _ DOTS_INSTANCING_ON
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _StrataTree;
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _VertexColour;
            float _VertexOcclusion;
            float _AlphaClip;
            float _Cutoff;
            float _BumpScale;
            float _Smoothness;
            float _SmoothnessSource;
            float4 _SmoothnessRemap;
            float _Cull;
            float _FoliageSource;
            float _Foliage;
            float _Translucency;
            float _HueVariation;
            float _BrightnessVariation;
            float _GroundTint;
            float _GroundTintHeight;
            float _GroundMultiply;
            float _WindSource;
            float _WindHeight;
            float _WindStrength;
            float _WindSpeed;
            float _WindScale;
            float4 _WindDirection;
        CBUFFER_END

        #include "StrataTreeCommon.hlsl"
        #include "StrataGround.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MaskMap);
        TEXTURE2D(_BumpMap);
        SAMPLER(sampler_BumpMap);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float4 color : COLOR;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        float Foliage(Attributes input)
        {
            return _FoliageSource > 0.5 ? input.uv.x : _Foliage;
        }

        float3 WindyPosition(Attributes input, StrataTreeSpace space)
        {
            float weight = _WindSource > 0.5 ? saturate(input.positionOS.y / max(_WindHeight, 0.01)) : input.color.a;
            weight *= _WindSource > 0.5 ? weight : 1.0;
            return StrataWind(StrataTreeToWorld(space, input.positionOS.xyz), space.origin, weight,
                              _WindStrength, _WindSpeed, _WindScale, _WindDirection.xz);
        }

        // Albedo and coverage, without the per-tree tint.
        half4 BaseAlbedo(float2 uv, half4 vertexColour)
        {
            half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv * _BaseMap_ST.xy + _BaseMap_ST.zw) * _BaseColor;
            if (_VertexColour > 0.5)
            {
                half3 colour = vertexColour.rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    colour = SRGBToLinear(colour);
                #endif
                albedo.rgb *= colour;
            }
            if (_AlphaClip > 0.5) clip(albedo.a - _Cutoff);
            return albedo;
        }

        void AlphaOnly(float2 uv)
        {
            if (_AlphaClip > 0.5)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv * _BaseMap_ST.xy + _BaseMap_ST.zw).a * _BaseColor.a - _Cutoff);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragForward
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StrataGI.hlsl"
            #include "StrataTreePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            // what deferred rendering draws instead of ForwardLit: the surface into the GBuffer, light through the leaves
            // included
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma exclude_renderers gles3 glcore
            #pragma vertex Vert
            #pragma fragment FragGBuffer
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
            #include "StrataTreePasses.hlsl"
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
                float2 uv : TEXCOORD0;
                nointerpolation float fade : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                StrataTreeSpace space = StrataTreeInstance();
                float3 positionWS = WindyPosition(input, space);
                float3 normalWS = StrataTreeNormalToWorld(space, input.normalOS);
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
                output.uv = input.uv;
                output.fade = space.fade;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                StrataFadeClip(input.positionCS.xy, input.fade);
                AlphaOnly(input.uv);
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
                float2 uv : TEXCOORD0;
                nointerpolation float fade : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                StrataTreeSpace space = StrataTreeInstance();
                output.positionCS = TransformWorldToHClip(WindyPosition(input, space));
                output.uv = input.uv;
                output.fade = space.fade;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                StrataFadeClip(input.positionCS.xy, input.fade);
                AlphaOnly(input.uv);
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
            #include "StrataTreeDepthNormals.hlsl"
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
            #include "StrataTreeDepthNormals.hlsl"
            ENDHLSL
        }

        // The impostor bake: drawn by StrataTreeBuilder with _BakeViewProjection over the mesh in its own space.
        Pass
        {
            Name "ImpostorAlbedo"
            Tags { "LightMode" = "StrataImpostorAlbedo" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex BakeVert
            #pragma fragment Frag

            float4x4 _BakeViewProjection;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 colour : TEXCOORD1;
            };

            Varyings BakeVert(Attributes input)
            {
                Varyings output;
                output.positionCS = mul(_BakeViewProjection, float4(input.positionOS.xyz, 1.0));
                output.uv = input.uv;
                output.colour = input.color;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // the atlas is imported as sRGB; impostors get no self-shadowing, so the occlusion goes into their albedo
                half3 albedo = BaseAlbedo(input.uv, input.colour).rgb;
                if (_VertexOcclusion > 0.5) albedo *= input.colour.r;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    albedo = LinearToSRGB(albedo);
                #endif
                return float4(albedo, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ImpostorNormals"
            Tags { "LightMode" = "StrataImpostorNormals" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex BakeVert
            #pragma fragment Frag

            float4x4 _BakeViewProjection;
            float3 _BakeViewDirection;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalOS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float foliage : TEXCOORD2;
            };

            Varyings BakeVert(Attributes input)
            {
                Varyings output;
                output.positionCS = mul(_BakeViewProjection, float4(input.positionOS.xyz, 1.0));
                output.normalOS = input.normalOS;
                output.uv = input.uv;
                output.foliage = Foliage(input);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                AlphaOnly(input.uv);
                float3 normal = normalize(input.normalOS);
                // two-sided leaves show the side facing the camera
                if (dot(normal, _BakeViewDirection) < 0.0 && _Cull < 0.5) normal = -normal;
                return float4(normal * 0.5 + 0.5, input.foliage);
            }
            ENDHLSL
        }
    }
}
