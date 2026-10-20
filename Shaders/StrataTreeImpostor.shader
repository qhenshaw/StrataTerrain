// Distant Strata trees as one quad each, drawn by StrataTreeRenderer through a BatchRendererGroup. StrataTreeBuilder
// renders each prototype from a grid of directions over its upper hemisphere (laid out hemi-octahedrally) into two
// atlases: albedo with coverage, and normals in the tree's own space with foliage. Here each tree shows the frame
// nearest the direction it's seen from (in its own turned space) on a quad facing that way, lit with URP's PBR lighting
// like Strata/Tree. The quad is trimmed to the frame's silhouette (Frame Bounds, one texel per frame: how far in from
// each edge the tree starts), so far fewer transparent pixels are shaded. Shadow passes pick the frame and face the
// quad by the light's direction instead; Shadow Push moves that quad towards the light by a fraction of the tree's
// radius, which StrataTreeRenderer uses when the quad casts a mesh LOD's shadow, so the mesh behind it stays shaded.
Shader "Strata/TreeImpostor"
{
    Properties
    {
        [NoScaleOffset] _Albedo ("Albedo (RGB) Coverage (A)", 2D) = "white" {}
        [NoScaleOffset] _Normals ("Normals (RGB) Foliage (A)", 2D) = "bump" {}
        [NoScaleOffset] _FrameBounds ("Frame Bounds (inset left, bottom, right, top)", 2D) = "black" {}
        _Frames ("Frames Per Side", Float) = 8
        _ImpostorBounds ("Bounds (centre xyz, radius)", Vector) = (0, 5, 0, 6)
        _Cutoff ("Coverage Cutoff", Range(0, 1)) = 0.4
        _Smoothness ("Smoothness", Range(0, 1)) = 0.15
        _Translucency ("Translucency", Range(0, 1)) = 0.35
        _HueVariation ("Hue Variation", Range(0, 0.5)) = 0.2
        _BrightnessVariation ("Brightness Variation", Range(0, 0.5)) = 0.12
        _Brightness ("Brightness", Range(0.2, 1.5)) = 1
        [HideInInspector] _ShadowPush ("Shadow Push", Float) = 0
        [HideInInspector] _StrataTree ("Tree", Vector) = (0, 0, 0, 64)
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile _ DOTS_INSTANCING_ON
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _StrataTree;
            float4 _ImpostorBounds;
            float _Frames;
            float _Cutoff;
            float _Smoothness;
            float _Translucency;
            float _HueVariation;
            float _BrightnessVariation;
            float _Brightness;
            float _ShadowPush;
        CBUFFER_END

        #include "StrataTreeCommon.hlsl"

        TEXTURE2D(_Albedo);
        SAMPLER(sampler_Albedo);
        TEXTURE2D(_Normals);
        SAMPLER(sampler_Normals);
        TEXTURE2D(_FrameBounds);

        struct Attributes
        {
            float4 positionOS : POSITION;       // the quad, -1..1 in x and y
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Impostor
        {
            float3 positionWS;
            float2 uv;
            float3 origin;
            float2 sinCos;
            float fade;
            float radius;
        };

        // toViewer: the direction from the tree to whatever looks at it (camera, or the light for shadows)
        Impostor PlaceImpostor(Attributes input, float3 toViewer, bool towardsPoint)
        {
            StrataTreeSpace space = StrataTreeInstance();
            float3 centreWS = space.origin + StrataRotateY(_ImpostorBounds.xyz * space.scale, space.sinCos);
            if (towardsPoint) toViewer = normalize(toViewer - centreWS);

            // the frame nearest the direction the tree is seen from, in its own space; all four corners agree
            float3 local = StrataUnrotateY(toViewer, space.sinCos);
            float frames = _Frames;
            float2 cell = clamp(floor((StrataHemiOctEncode(local) * 0.5 + 0.5) * frames), 0, frames - 1);
            float3 view = StrataHemiOctDecode((cell + 0.5) / frames * 2.0 - 1.0);

            // the same camera the baker used for that frame
            float3 up = abs(view.y) > 0.999 ? float3(0, 0, 1) : float3(0, 1, 0);
            float3 right = normalize(cross(view, up));
            up = cross(-view, right);
            float radius = _ImpostorBounds.w * space.scale;
            // the corner, moved in to the frame's silhouette
            float4 inset = LOAD_TEXTURE2D_LOD(_FrameBounds, (int2)cell, 0);
            float2 corner = lerp(inset.xy, 1.0 - inset.zw, input.positionOS.xy * 0.5 + 0.5);
            float3 offset = (right * (corner.x * 2.0 - 1.0) + up * (corner.y * 2.0 - 1.0)) * radius;

            Impostor impostor;
            impostor.positionWS = centreWS + StrataRotateY(offset, space.sinCos);
            impostor.uv = (cell + corner) / frames;
            impostor.origin = space.origin;
            impostor.sinCos = space.sinCos;
            impostor.fade = space.fade;
            impostor.radius = radius;
            return impostor;
        }

        half4 SampleAlbedo(float2 uv)
        {
            half4 albedo = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uv);
            clip(albedo.a - _Cutoff);
            return albedo;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StrataGI.hlsl"

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

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                StrataFadeClip(input.positionCS.xy, input.fade);
                half4 albedo = SampleAlbedo(input.uv);
                half4 packed = SAMPLE_TEXTURE2D(_Normals, sampler_Normals, input.uv);
                float3 normalWS = normalize(StrataRotateY(packed.rgb * 2.0 - 1.0, input.sinCos));
                float foliage = packed.a;
                albedo.rgb = StrataTreeTint(albedo.rgb, input.origin, foliage, _HueVariation, _BrightnessVariation) * _Brightness;

                InputData inputData = (InputData)0;
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

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1.0;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.alpha = 1.0;
                half4 colour = UniversalFragmentPBR(inputData, surfaceData);

                Light light = GetMainLight(inputData.shadowCoord);
                half back = saturate(dot(-normalWS, light.direction) * 0.5 + 0.5);
                colour.rgb += albedo.rgb * light.color * (back * back * _Translucency * foliage * light.shadowAttenuation);
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
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    Impostor impostor = PlaceImpostor(input, _LightPosition, true);
                    float3 lightDirectionWS = normalize(_LightPosition - impostor.positionWS);
                #else
                    Impostor impostor = PlaceImpostor(input, _LightDirection, false);
                    float3 lightDirectionWS = _LightDirection;
                #endif
                impostor.positionWS += lightDirectionWS * impostor.radius * _ShadowPush;
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(impostor.positionWS, lightDirectionWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                output.uv = impostor.uv;
                output.fade = impostor.fade;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                StrataFadeClip(input.positionCS.xy, input.fade);
                SampleAlbedo(input.uv);
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
                Impostor impostor = PlaceImpostor(input, _WorldSpaceCameraPos, true);
                output.positionCS = TransformWorldToHClip(impostor.positionWS);
                output.uv = impostor.uv;
                output.fade = impostor.fade;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                StrataFadeClip(input.positionCS.xy, input.fade);
                SampleAlbedo(input.uv);
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
            #include "StrataTreeImpostorDepthNormals.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "StrataTreeImpostorDepthNormals.hlsl"
            ENDHLSL
        }
    }
}
