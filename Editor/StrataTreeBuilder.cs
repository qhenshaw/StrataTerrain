using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Fills tree prototypes from their source prefabs. Rebuild reads the prefab's LODGroup (or its renderers as a single
    // LOD): each LOD's mesh is used as it is when one untransformed renderer draws it, otherwise its renderers are
    // combined into a mesh saved beside the prototype; every source material gets a Strata/Tree copy in a Materials
    // folder beside the prototype (shared by prototypes using the same source); LOD distances come from the LODGroup's
    // transitions at a 60 degree view, with the last mesh LOD reaching MeshDistance; the collider comes from a
    // CapsuleCollider on the prefab (none if it has none, as for bushes). BakeImpostor renders LOD0 from Frames x Frames
    // directions over the upper hemisphere into albedo and normal atlases and makes the Strata/TreeImpostor material.
    public static class StrataTreeBuilder
    {
        public const string TreeShader = "Strata/Tree";
        public const string ImpostorShader = "Strata/TreeImpostor";
        public const float MeshDistance = 150f;
        private const int Frames = 8;
        private const int FrameSize = 128;
        private const int AtlasSize = Frames * FrameSize;
        private const int Dilation = 8;
        private static readonly float ViewHeightFactor = 2f * Mathf.Tan(30f * Mathf.Deg2Rad);

        // Creates (or refreshes) the prototype for a prefab in a folder, rebuilds it and bakes its impostor.
        public static StrataTreePrototype CreatePrototype(GameObject prefab, string folder)
        {
            EnsureFolder(folder);
            string path = $"{folder}/{prefab.name}.asset";
            StrataTreePrototype prototype = AssetDatabase.LoadAssetAtPath<StrataTreePrototype>(path);
            if (prototype == null)
            {
                prototype = ScriptableObject.CreateInstance<StrataTreePrototype>();
                prototype.Source = prefab;
                AssetDatabase.CreateAsset(prototype, path);
            }
            prototype.Source = prefab;
            Rebuild(prototype);
            BakeImpostor(prototype);
            return prototype;
        }

        public static string Rebuild(StrataTreePrototype prototype)
        {
            GameObject source = prototype.Source;
            if (source == null) return $"{prototype.name}: no source prefab.";
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(prototype))!.Replace('\\', '/');
            string materialFolder = $"{folder}/Materials";
            EnsureFolder(materialFolder);

            List<Renderer[]> lodRenderers = new();
            List<float> transitions = new();
            float size = 1f;
            if (source.TryGetComponent(out LODGroup group) && group.lodCount > 0)
            {
                size = group.size;
                foreach (LOD lod in group.GetLODs())
                {
                    if (lod.renderers.Length == 0) continue;
                    lodRenderers.Add(lod.renderers);
                    transitions.Add(lod.screenRelativeTransitionHeight);
                }
            }
            else
            {
                lodRenderers.Add(source.GetComponentsInChildren<MeshRenderer>());
                transitions.Add(0f);
                size = MaxSize(source);
            }
            if (lodRenderers.Count > StrataTreePrototype.MaxLods) lodRenderers.RemoveRange(StrataTreePrototype.MaxLods, lodRenderers.Count - StrataTreePrototype.MaxLods);

            StrataTreePrototype.Lod[] lods = new StrataTreePrototype.Lod[lodRenderers.Count];
            float previous = 0f;
            for (int l = 0; l < lods.Length; l++)
            {
                (Mesh mesh, Material[] sources) = MeshOf(lodRenderers[l], source.transform, $"{folder}/{prototype.name} LOD{l}.asset");
                Material[] materials = new Material[sources.Length];
                for (int m = 0; m < sources.Length; m++) materials[m] = Convert(sources[m], materialFolder);
                float distance = l == lods.Length - 1 || transitions[l] <= 0f ? MeshDistance : size / (ViewHeightFactor * transitions[l]);
                distance = Mathf.Max(Mathf.Min(distance, MeshDistance - (lods.Length - 1 - l)), previous + 1f);
                previous = distance;
                lods[l] = new StrataTreePrototype.Lod { Mesh = mesh, Materials = materials, Distance = distance };
            }

            Bounds bounds = lods.Length > 0 && lods[0].Mesh != null ? lods[0].Mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            float reach = Mathf.Max(Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)), Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z)));
            prototype.SetLods(lods, Mathf.Max(bounds.max.y, 0.1f), Mathf.Max(reach, 0.1f));

            CapsuleCollider capsule = source.GetComponentInChildren<CapsuleCollider>();
            if (capsule != null)
            {
                float scale = Mathf.Max(capsule.transform.lossyScale.x, capsule.transform.lossyScale.z);
                prototype.SetCollider(true, capsule.radius * scale, capsule.height * capsule.transform.lossyScale.y + Mathf.Max(0f, capsule.center.y - capsule.height * 0.5f));
            }
            else prototype.SetCollider(false, prototype.ColliderRadius, prototype.ColliderHeight);

            EditorUtility.SetDirty(prototype);
            AssetDatabase.SaveAssetIfDirty(prototype);
            return $"{prototype.name}: {lods.Length} LODs ({string.Join(", ", System.Array.ConvertAll(lods, x => $"{x.Mesh.vertexCount} verts to {x.Distance:0} m"))}), " +
                   $"{(capsule != null ? "trunk capsule" : "no collider")}.";
        }

        // One mesh for a LOD's renderers, and the source material of each of its submeshes.
        private static (Mesh, Material[]) MeshOf(Renderer[] renderers, Transform root, string combinedPath)
        {
            List<MeshFilter> filters = new();
            List<Material> materials = new();
            foreach (Renderer renderer in renderers)
                if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) filters.Add(filter);
            if (filters.Count == 1 && filters[0].transform == root || filters.Count == 1 && root.worldToLocalMatrix * filters[0].transform.localToWorldMatrix == Matrix4x4.identity)
            {
                Mesh mesh = filters[0].sharedMesh;
                Material[] shared = filters[0].GetComponent<Renderer>().sharedMaterials;
                Material[] perSubmesh = new Material[mesh.subMeshCount];
                for (int i = 0; i < perSubmesh.Length; i++) perSubmesh[i] = shared[Mathf.Min(i, shared.Length - 1)];
                return (mesh, perSubmesh);
            }

            // combine, one submesh per distinct material
            Dictionary<Material, List<CombineInstance>> byMaterial = new();
            foreach (MeshFilter filter in filters)
            {
                Material[] shared = filter.GetComponent<Renderer>().sharedMaterials;
                for (int s = 0; s < filter.sharedMesh.subMeshCount; s++)
                {
                    Material material = shared[Mathf.Min(s, shared.Length - 1)];
                    if (!byMaterial.TryGetValue(material, out List<CombineInstance> list)) byMaterial[material] = list = new List<CombineInstance>();
                    list.Add(new CombineInstance { mesh = filter.sharedMesh, subMeshIndex = s, transform = root.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                }
            }
            List<CombineInstance> parts = new();
            foreach (KeyValuePair<Material, List<CombineInstance>> pair in byMaterial)
            {
                Mesh part = new() { indexFormat = IndexFormat.UInt32 };
                part.CombineMeshes(pair.Value.ToArray(), true, true);
                parts.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
                materials.Add(pair.Key);
            }
            Mesh combined = AssetDatabase.LoadAssetAtPath<Mesh>(combinedPath);
            bool created = combined == null;
            if (created) combined = new Mesh();
            combined.Clear();
            combined.indexFormat = IndexFormat.UInt32;
            combined.CombineMeshes(parts.ToArray(), false, false);
            if (combined.vertexCount > 200000) Debug.LogWarning($"Strata: the combined LOD mesh {combinedPath} has {combined.vertexCount} vertices; it is saved as text.");
            if (created) AssetDatabase.CreateAsset(combined, combinedPath);
            else EditorUtility.SetDirty(combined);
            foreach (CombineInstance part in parts) Object.DestroyImmediate(part.mesh);
            return (combined, materials.ToArray());
        }

        // A Strata/Tree material for a source material: the source itself if it already is one, otherwise a copy kept
        // in the materials folder (refreshed from the source each time).
        private static Material Convert(Material source, string folder)
        {
            if (source == null) return null;
            Shader shader = Shader.Find(TreeShader);
            if (source.shader == shader) return source;
            string path = $"{folder}/{source.name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created) material = new Material(shader) { name = source.name };
            material.shader = shader;

            if (source.HasProperty("_WindStrength") && source.HasProperty("_HueVariation") && !source.HasProperty("_BaseMap"))
            {
                // the game's vertex-coloured low-poly vegetation: colour and sway in the vertices, foliage in uv x
                material.SetFloat("_VertexColour", 1f);
                material.SetFloat("_FoliageSource", 1f);
                material.SetFloat("_WindSource", 0f);
                material.SetFloat("_Smoothness", 0.05f);
                foreach (string name in new[] { "_WindStrength", "_WindSpeed", "_WindScale", "_HueVariation", "_BrightnessVariation", "_Translucency" })
                    if (source.HasProperty(name)) material.SetFloat(name, source.GetFloat(name));
                if (source.HasProperty("_WindDirection")) material.SetVector("_WindDirection", source.GetVector("_WindDirection"));
            }
            else
            {
                Texture albedo = Texture(source, "_BaseMap", "_MainTex", "_BaseColorMap");
                material.SetTexture("_BaseMap", albedo);
                material.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
                Texture normal = Texture(source, "_BumpMap", "_NormalMap");
                material.SetTexture("_BumpMap", normal);
                bool clip = source.IsKeywordEnabled("_ALPHATEST_ON") || source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f ||
                            source.renderQueue >= (int)RenderQueue.AlphaTest && source.renderQueue < (int)RenderQueue.Transparent;
                material.SetFloat("_AlphaClip", clip ? 1f : 0f);
                material.SetFloat("_Cutoff", source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.5f);
                material.SetFloat("_Smoothness", source.HasProperty("_Smoothness") ? source.GetFloat("_Smoothness") * 0.5f : 0.2f);
                material.SetFloat("_Cull", clip ? (float)CullMode.Off : (float)CullMode.Back);
                material.SetFloat("_FoliageSource", 0f);
                material.SetFloat("_Foliage", clip ? 1f : 0f);
                material.SetFloat("_WindSource", 1f);
                material.SetFloat("_WindStrength", clip ? 0.25f : 0.08f);
            }
            if (created) AssetDatabase.CreateAsset(material, path);
            else EditorUtility.SetDirty(material);
            return material;
        }

        private static Texture Texture(Material material, params string[] names)
        {
            foreach (string name in names)
                if (material.HasProperty(name) && material.GetTexture(name) != null) return material.GetTexture(name);
            return null;
        }

        private static float MaxSize(GameObject source)
        {
            Bounds bounds = new(source.transform.position, Vector3.zero);
            foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
            return Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        }

        // Renders LOD0 into the impostor atlases (PNGs beside the prototype) and makes or updates its impostor material.
        public static string BakeImpostor(StrataTreePrototype prototype)
        {
            if (!prototype.IsValid) return $"{prototype.name}: rebuild it from its source before baking the impostor.";
            StrataTreePrototype.Lod lod = prototype.Lods[0];
            Mesh mesh = lod.Mesh;
            Vector3 centre = mesh.bounds.center;
            float radius = mesh.bounds.extents.magnitude;
            string prototypePath = AssetDatabase.GetAssetPath(prototype);
            string folder = Path.GetDirectoryName(prototypePath)!.Replace('\\', '/');

            RenderTexture target = new(AtlasSize, AtlasSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D albedo, normals;
            try
            {
                albedo = Render(mesh, lod.Materials, centre, radius, "ImpostorAlbedo", target);
                normals = Render(mesh, lod.Materials, centre, radius, "ImpostorNormals", target);
            }
            finally
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
            Dilate(albedo, normals);
            Texture2D albedoAsset = Save(albedo, $"{folder}/{prototype.name} Impostor Albedo.png", true);
            Texture2D normalsAsset = Save(normals, $"{folder}/{prototype.name} Impostor Normals.png", false);
            Object.DestroyImmediate(albedo);
            Object.DestroyImmediate(normals);

            string materialPath = $"{folder}/Materials/{prototype.name} Impostor.mat";
            EnsureFolder($"{folder}/Materials");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find(ImpostorShader)) { name = $"{prototype.name} Impostor" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.SetTexture("_Albedo", albedoAsset);
            material.SetTexture("_Normals", normalsAsset);
            material.SetFloat("_Frames", Frames);
            Vector4 bounds = new(centre.x, centre.y, centre.z, radius);
            material.SetVector("_ImpostorBounds", bounds);
            // lighting and tint like the mesh's foliage
            Material foliage = System.Array.Find(lod.Materials, m => m != null && (m.GetFloat("_FoliageSource") > 0.5f || m.GetFloat("_Foliage") > 0.5f)) ?? lod.Materials[0];
            foreach (string name in new[] { "_Translucency", "_HueVariation", "_BrightnessVariation", "_Smoothness" })
                material.SetFloat(name, foliage.GetFloat(name));
            EditorUtility.SetDirty(material);
            prototype.SetImpostor(material, bounds);
            EditorUtility.SetDirty(prototype);
            AssetDatabase.SaveAssets();
            return $"{prototype.name}: impostor of {Frames}x{Frames} frames of {FrameSize} px.";
        }

        // Every frame through an orthographic camera fitted to the bounding sphere, looking in from a direction over the
        // hemisphere; the impostor shader rebuilds the same camera to place its quad.
        private static Texture2D Render(Mesh mesh, Material[] materials, Vector3 centre, float radius, string passName, RenderTexture target)
        {
            CommandBuffer cmd = new() { name = "Bake Strata Impostor" };
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(true, true, Color.clear);
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-radius, radius, -radius, radius, 0.01f, radius * 2f + 2f), true);
            for (int y = 0; y < Frames; y++)
            for (int x = 0; x < Frames; x++)
            {
                Vector3 view = Decode(new Vector2((x + 0.5f) / Frames, (y + 0.5f) / Frames) * 2f - Vector2.one);
                Vector3 up = Mathf.Abs(view.y) > 0.999f ? Vector3.forward : Vector3.up;
                Vector3 eye = centre + view * (radius + 1f);
                Matrix4x4 worldToCamera = Matrix4x4.TRS(eye, Quaternion.LookRotation(-view, up), new Vector3(1f, 1f, -1f)).inverse;
                cmd.SetViewport(new Rect(x * FrameSize, y * FrameSize, FrameSize, FrameSize));
                cmd.SetGlobalMatrix("_BakeViewProjection", projection * worldToCamera);
                cmd.SetGlobalVector("_BakeViewDirection", view);
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    Material material = materials[Mathf.Min(s, materials.Length - 1)];
                    int pass = material.FindPass(passName);
                    if (pass >= 0) cmd.DrawMesh(mesh, Matrix4x4.identity, material, s, pass);
                }
            }
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            Texture2D texture = new(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, AtlasSize, AtlasSize), 0, 0, false);
            RenderTexture.active = previous;
            return texture;
        }

        // Matches StrataHemiOctDecode in StrataTreeCommon.hlsl.
        private static Vector3 Decode(Vector2 encoded)
        {
            float x = (encoded.x + encoded.y) * 0.5f;
            float z = (encoded.x - encoded.y) * 0.5f;
            return new Vector3(x, 1f - Mathf.Abs(x) - Mathf.Abs(z), z).normalized;
        }

        // Spreads colour into the empty pixels round each frame's tree (keeping them transparent), so mipmaps don't
        // darken the edges towards the clear colour.
        private static void Dilate(Texture2D albedo, Texture2D normals)
        {
            Color32[] colours = albedo.GetPixels32(0);
            Color32[] packed = normals.GetPixels32(0);
            bool[] filled = new bool[colours.Length];
            for (int i = 0; i < colours.Length; i++) filled[i] = colours[i].a > 0;
            List<int> grown = new();
            (int, int)[] steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            for (int pass = 0; pass < Dilation; pass++)
            {
                grown.Clear();
                for (int y = 0; y < AtlasSize; y++)
                for (int x = 0; x < AtlasSize; x++)
                {
                    int i = y * AtlasSize + x;
                    if (filled[i]) continue;
                    int r = 0, g = 0, b = 0, nr = 0, ng = 0, nb = 0, na = 0, count = 0;
                    foreach ((int dx, int dy) in steps)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= AtlasSize || ny >= AtlasSize) continue;
                        if (nx / FrameSize != x / FrameSize || ny / FrameSize != y / FrameSize) continue;
                        int n = ny * AtlasSize + nx;
                        if (!filled[n]) continue;
                        r += colours[n].r; g += colours[n].g; b += colours[n].b;
                        nr += packed[n].r; ng += packed[n].g; nb += packed[n].b; na += packed[n].a;
                        count++;
                    }
                    if (count == 0) continue;
                    colours[i] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 0);
                    packed[i] = new Color32((byte)(nr / count), (byte)(ng / count), (byte)(nb / count), (byte)(na / count));
                    grown.Add(i);
                }
                foreach (int i in grown) filled[i] = true;
            }
            albedo.SetPixels32(colours, 0);
            normals.SetPixels32(packed, 0);
        }

        // Written as a PNG and imported: colour as sRGB with its coverage kept up through the mipmaps, normals as data.
        private static Texture2D Save(Texture2D texture, string path, bool colour)
        {
            File.WriteAllBytes(path, texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = colour;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.mipMapsPreserveCoverage = colour;
            importer.alphaTestReferenceValue = 0.4f;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)!.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
