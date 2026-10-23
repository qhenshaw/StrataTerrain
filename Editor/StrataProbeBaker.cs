using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Strata.Editor
{
    // Bakes Adaptive Probe Volumes for a world. The probe baker only sees GI-static renderers, and Strata draws its
    // terrain and trees procedurally, so for the length of the bake it stands in proxies: the terrain at ProxySpacing
    // with one world albedo texture (each texel the splat-weighted mean colour of its layers), and every tree as a
    // cone crown on a trunk prism coloured like its prototype's foliage and bark, both merged per tile, with the terrain
    // left open over cave holes and the caves' own meshes standing in for the rock around them. The proxies are
    // never saved and are destroyed when the bake ends. The first bake also sets the scene up: a probe volume over the
    // world (or a local one over a region, to measure before a full bake), the scene's baking set at the world's probe spacing, and lighting
    // settings of its own with baked GI on (the shared ones are left alone). Strata's shaders sample the probes
    // (StrataGI.hlsl). Returns one-line reports.
    public static class StrataProbeBaker
    {
        private const float ProxySpacing = 4f;
        private const int SimplificationLevels = 3; // max spacing is 3^3 times the world's min spacing
        private const int ConeSides = 8;
        private const float CrownStart = 0.25f; // of the tree's height
        private const float CrownWidth = 0.85f; // of its reach, as crowns aren't solid
        private const string ProxyRootName = "Strata Probe Proxies";
        private const string VolumeName = "Strata Probe Volume";

        private static GameObject s_proxies;
        private static Stopwatch s_watch;
        private static StrataWorld s_world;

        public static bool IsBaking => s_proxies != null;

        // Bakes the whole world, or a square region (size metres across, around centre) with proxies only near it.
        public static string Bake(StrataWorld world, Vector3? centre = null, float size = 0f)
        {
            if (IsBaking || AdaptiveProbeVolumes.isRunning) return "Strata probes: a bake is already running.";
            if (world.Heightfield == null || world.Splatmap == null || world.Trees == null) return "Strata probes: the world isn't built.";
            if (world.Streamer != null) return "Strata probes: bake from edit mode, where every tile is resident.";
            Scene scene = world.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path)) return "Strata probes: save the scene first.";

            StrataGrid grid = world.Heightfield.Grid;
            bool region = centre.HasValue && size > 0f;
            Rect area = region ? new Rect(centre.Value.x - size * 0.5f, centre.Value.z - size * 0.5f, size, size)
                               : new Rect(grid.Origin.x, grid.Origin.y, grid.Size.x, grid.Size.y);
            string setup = SetUp(world, scene, region, area);
            if (setup.StartsWith("Strata probes:")) return setup;

            s_watch = Stopwatch.StartNew();
            s_world = world;
            // the baker skips objects hidden from saving, so the proxies are only kept out of the scene while it saves
            s_proxies = new GameObject(ProxyRootName) { hideFlags = HideFlags.NotEditable };
            EditorSceneManager.sceneSaving += HideProxiesFromSave;
            EditorSceneManager.sceneSaved += ShowProxiesAfterSave;
            int tiles = 0, trees = 0;
            long triangles = 0;
            try
            {
                // proxies a little past a region, so its edge probes still see the slopes and forest beside them
                Rect proxied = region ? new Rect(area.x - 256f, area.y - 256f, area.width + 512f, area.height + 512f) : area;
                Texture2D albedo = TerrainAlbedo(world);
                Material ground = ProxyMaterial("Strata Probe Ground", Color.white, albedo);
                Material[] treeMaterials = TreeMaterials(world.Trees.Prototypes);
                Material cave = ProxyMaterial("Strata Probe Cave", CaveColour(world));
                for (int tile = 0; tile < grid.TileCount; tile++)
                {
                    float2 min = grid.TileOrigin(tile % grid.TilesX, tile / grid.TilesX);
                    if (!proxied.Overlaps(new Rect(min.x, min.y, grid.TileSize, grid.TileSize))) continue;
                    tiles++;
                    Mesh terrain = TerrainMesh(world, tile);
                    triangles += AddProxy($"Terrain {tile}", terrain, ground);
                    Mesh caves = CaveMesh(world, tile);
                    if (caves != null) triangles += AddProxy($"Caves {tile}", caves, cave);
                    Mesh forest = ForestMesh(world, tile, out int count);
                    trees += count;
                    if (forest != null) triangles += AddProxy($"Trees {tile}", forest, treeMaterials);
                }
                if (!AdaptiveProbeVolumes.BakeAsync())
                {
                    Finish();
                    return "Strata probes: the bake didn't start (see the Console).";
                }
            }
            catch
            {
                Finish();
                throw;
            }
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
            return $"Strata probes: baking {(region ? $"a {size:0} m region" : "the world")} against {tiles} tiles of proxies " +
                   $"({trees} trees, {triangles / 1e6f:0.0}M triangles). {setup}";
        }

        // Removes leftover proxies and the probe volume (the scene's baking set and its data stay; the Lighting window's
        // Clear Baked Data removes those).
        public static string Clear(StrataWorld world)
        {
            Finish();
            int removed = 0;
            foreach (ProbeVolume volume in Object.FindObjectsByType<ProbeVolume>(FindObjectsInactive.Include))
                if (volume.name == VolumeName)
                {
                    Undo.DestroyObjectImmediate(volume.gameObject);
                    removed++;
                }
            return $"Strata probes: removed {removed} probe volume(s); clear the baked probe data from the Lighting window.";
        }

        // The probe volume, baking set and lighting settings, made once. Returns what it changed (or an error).
        private static string SetUp(StrataWorld world, Scene scene, bool region, Rect area)
        {
            List<string> changes = new();
            StrataGrid grid = world.Heightfield.Grid;
            ProbeVolume volume = null;
            foreach (ProbeVolume candidate in Object.FindObjectsByType<ProbeVolume>(FindObjectsInactive.Include))
                if (candidate.name == VolumeName && candidate.gameObject.scene == scene) volume = candidate;
            if (volume == null)
            {
                GameObject go = new(VolumeName);
                SceneManager.MoveGameObjectToScene(go, scene);
                volume = go.AddComponent<ProbeVolume>();
                Undo.RegisterCreatedObjectUndo(go, "Strata Probe Volume");
                changes.Add("added the probe volume");
            }
            float height = grid.HeightRange + 100f; // trees stand above the highest ground
            volume.mode = region ? ProbeVolume.Mode.Local : ProbeVolume.Mode.Global;
            volume.transform.SetPositionAndRotation(new Vector3(area.center.x, grid.MinHeight + height * 0.5f, area.center.y), Quaternion.identity);
            volume.size = new Vector3(area.width, height, area.height);
            EditorUtility.SetDirty(volume);

            string folder = SceneFolder(scene);
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
            // the scene's own baking set (Unity makes one per scene), or a new one beside it
            ProbeVolumeBakingSet set = BakingSetOf(scene);
            if (set == null)
            {
                string setPath = $"{folder}/{scene.name} Baking Set.asset";
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                AssetDatabase.CreateAsset(set, setPath);
                if (!set.TryAddScene(AssetDatabase.AssetPathToGUID(scene.path)))
                {
                    AssetDatabase.DeleteAsset(setPath);
                    return $"Strata probes: couldn't add {scene.name} to a baking set.";
                }
                changes.Add($"made the baking set {setPath}");
            }
            float spacing = world.Settings.ProbeMinSpacing;
            if (!Mathf.Approximately(set.minDistanceBetweenProbes, spacing) || set.simplificationLevels != SimplificationLevels)
            {
                set.minDistanceBetweenProbes = spacing;
                set.simplificationLevels = SimplificationLevels;
                changes.Add($"set {set.name} to {spacing:0.#}-{spacing * Mathf.Pow(3, SimplificationLevels):0.#} m probe spacing");
            }
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssetIfDirty(set);

            LightingSettings lighting = Lightmapping.lightingSettings;
            string ownPath = $"{folder}/{scene.name} Lighting.lighting";
            if (AssetDatabase.GetAssetPath(lighting) != ownPath)
            {
                LightingSettings own = AssetDatabase.LoadAssetAtPath<LightingSettings>(ownPath);
                if (own == null)
                {
                    AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(lighting), ownPath);
                    own = AssetDatabase.LoadAssetAtPath<LightingSettings>(ownPath);
                }
                Lightmapping.lightingSettings = own;
                lighting = own;
                EditorSceneManager.MarkSceneDirty(scene);
                changes.Add($"gave {scene.name} its own lighting settings ({ownPath})");
            }
            if (!lighting.bakedGI || lighting.mixedBakeMode != MixedLightingMode.IndirectOnly)
            {
                lighting.bakedGI = true;
                lighting.mixedBakeMode = MixedLightingMode.IndirectOnly;
                EditorUtility.SetDirty(lighting);
                AssetDatabase.SaveAssetIfDirty(lighting);
                changes.Add("turned baked GI on (Baked Indirect)");
            }

            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                if (light.type == LightType.Directional && light.lightmapBakeType == LightmapBakeType.Realtime)
                    changes.Add($"{light.name} is Realtime, so the probes get skylight only (make it Mixed for its bounce)");
            return changes.Count == 0 ? "" : $"Set up: {string.Join("; ", changes)}. Save the scene to keep it.";
        }

        // Waits out the bake, then removes the proxies and reports.
        private static void Watch()
        {
            if (AdaptiveProbeVolumes.isRunning) return;
            EditorApplication.update -= Watch;
            double seconds = s_watch?.Elapsed.TotalSeconds ?? 0;
            Scene scene = s_world != null ? s_world.gameObject.scene : default;
            Finish();
            ProbeVolumeBakingSet set = scene.IsValid() ? BakingSetOf(scene) : null;
            long bytes = 0;
            string folder = set != null ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(set)) : null;
            if (folder != null && Directory.Exists(folder))
                foreach (string file in Directory.GetFiles(folder, $"{set.name}*.bytes")) bytes += new FileInfo(file).Length;
            UnityEngine.Debug.Log($"Strata probes: bake finished in {seconds:0} s, {bytes / (1024f * 1024f):0.0} MB of probe data " +
                                  $"({(set != null && set.HasBakedData() ? "baked" : "no data")}) for {set?.name}.");
        }

        private static void HideProxiesFromSave(Scene scene, string path) => SetProxySaving(false);
        private static void ShowProxiesAfterSave(Scene scene) => SetProxySaving(true);

        private static void SetProxySaving(bool visible)
        {
            if (s_proxies == null) return;
            foreach (Transform proxy in s_proxies.GetComponentsInChildren<Transform>(true))
                proxy.gameObject.hideFlags = visible ? HideFlags.NotEditable : HideFlags.NotEditable | HideFlags.DontSaveInEditor;
        }

        private static void Finish()
        {
            EditorApplication.update -= Watch;
            EditorSceneManager.sceneSaving -= HideProxiesFromSave;
            EditorSceneManager.sceneSaved -= ShowProxiesAfterSave;
            if (s_proxies == null)
            {
                // a proxy root can outlive a domain reload
                GameObject leftover = GameObject.Find(ProxyRootName);
                if (leftover != null) s_proxies = leftover;
            }
            if (s_proxies != null)
            {
                foreach (MeshFilter filter in s_proxies.GetComponentsInChildren<MeshFilter>(true)) Object.DestroyImmediate(filter.sharedMesh);
                foreach (MeshRenderer renderer in s_proxies.GetComponentsInChildren<MeshRenderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null && (material.hideFlags & HideFlags.DontSave) != 0)
                        {
                            if (material.mainTexture != null && (material.mainTexture.hideFlags & HideFlags.DontSave) != 0) Object.DestroyImmediate(material.mainTexture);
                            Object.DestroyImmediate(material);
                        }
                Object.DestroyImmediate(s_proxies);
            }
            s_proxies = null;
            s_world = null;
            s_watch = null;
        }

        private static ProbeVolumeBakingSet BakingSetOf(Scene scene)
        {
            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            foreach (string guid in AssetDatabase.FindAssets("t:ProbeVolumeBakingSet"))
            {
                ProbeVolumeBakingSet set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(AssetDatabase.GUIDToAssetPath(guid));
                if (set == null) continue;
                foreach (string member in set.sceneGUIDs)
                    if (member == sceneGuid) return set;
            }
            return null;
        }

        private static string SceneFolder(Scene scene) =>
            $"{Path.GetDirectoryName(scene.path).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scene.path)}";

        private static long AddProxy(string name, Mesh mesh, params Material[] materials)
        {
            GameObject go = new(name) { hideFlags = HideFlags.NotEditable };
            go.transform.SetParent(s_proxies.transform, false);
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.receiveGI = ReceiveGI.LightProbes;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            long triangles = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) triangles += mesh.GetSubMesh(i).indexCount / 3;
            return triangles;
        }

        private static Material ProxyMaterial(string name, Color colour, Texture texture = null)
        {
            Material material = new(Shader.Find("Universal Render Pipeline/Lit")) { name = name, hideFlags = HideFlags.DontSave };
            material.SetColor("_BaseColor", colour);
            if (texture != null) material.SetTexture("_BaseMap", texture);
            material.SetFloat("_Smoothness", 0f);
            return material;
        }

        // The whole world's ground colour at ProxySpacing: per texel, the mean albedo of its splat's layers by weight.
        private static Texture2D TerrainAlbedo(StrataWorld world)
        {
            StrataGrid grid = world.Heightfield.Grid;
            IReadOnlyList<StrataLayer> layers = world.Palette.Layers;
            Color[] means = new Color[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                StrataLayer layer = layers[i];
                means[i] = layer == null ? Color.gray : StrataTreeBuilder.MeanColour(layer.Albedo) * layer.Tint.linear;
            }

            int2 size = (int2)math.ceil(grid.Size / ProxySpacing);
            Color32[] pixels = new Color32[size.x * size.y];
            var indices = world.Splatmap.CpuIndices;
            var weights = world.Splatmap.CpuWeights;
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                float2 relative = (new float2(x, y) + 0.5f) * ProxySpacing / grid.TileSize;
                int2 tile = math.clamp((int2)math.floor(relative), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
                int slot = world.Heightfield.SlotOf(grid.TileIndex(tile.x, tile.y));
                Color sum = Color.clear;
                float total = 0f;
                if (slot >= 0)
                {
                    int2 texel = (int2)math.round(math.saturate(relative - tile) * grid.SplatResolution);
                    int index = slot * grid.SplatSamplesPerTile + texel.y * grid.SplatSamples + texel.x;
                    uint layerBits = indices[index], weightBits = weights[index];
                    for (int i = 0; i < 4; i++)
                    {
                        int layer = (int)((layerBits >> (i * 8)) & 0xff);
                        float weight = ((weightBits >> (i * 8)) & 0xff) / 255f;
                        if (weight <= 0f || layer >= means.Length) continue;
                        sum += means[layer] * weight;
                        total += weight;
                    }
                }
                pixels[y * size.x + x] = total > 0f ? (Color32)(sum / total).gamma : (Color32)Color.gray;
            }
            Texture2D texture = new(size.x, size.y, TextureFormat.RGBA32, false, false) { name = "Strata Probe Ground", hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        // One tile's ground at ProxySpacing, in world space, with UVs into the world albedo texture.
        private static Mesh TerrainMesh(StrataWorld world, int tile)
        {
            StrataGrid grid = world.Heightfield.Grid;
            StrataHeights heights = world.Heightfield.View;
            int step = math.max(1, (int)math.round(ProxySpacing / grid.Spacing));
            int quads = grid.Resolution / step;
            int2 first = new int2(tile % grid.TilesX, tile / grid.TilesX) * grid.Resolution;
            int across = quads + 1;
            Vector3[] vertices = new Vector3[across * across];
            Vector2[] uvs = new Vector2[vertices.Length];
            for (int z = 0; z <= quads; z++)
            for (int x = 0; x <= quads; x++)
            {
                int2 sample = first + new int2(x, z) * step;
                float2 position = grid.SamplePosition(sample);
                vertices[z * across + x] = new Vector3(position.x, heights.At(sample), position.y);
                uvs[z * across + x] = (position - grid.Origin) / grid.Size;
            }
            List<int> triangles = new(quads * quads * 6);
            for (int z = 0; z < quads; z++)
            for (int x = 0; x < quads; x++)
            {
                // open where any corner is in a cave's hole, so probes in the cave see the sky through it
                int2 sample = first + new int2(x, z) * step;
                if (heights.Presence(sample) < 0.5f || heights.Presence(sample + new int2(step, 0)) < 0.5f ||
                    heights.Presence(sample + new int2(0, step)) < 0.5f || heights.Presence(sample + step) < 0.5f) continue;
                int i = z * across + x;
                triangles.Add(i); triangles.Add(i + across); triangles.Add(i + 1);
                triangles.Add(i + 1); triangles.Add(i + across); triangles.Add(i + across + 1);
            }
            Mesh mesh = new() { name = $"Strata Probe Terrain {tile}", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // One tile's cave meshes merged in world space, or null if it has none.
        private static Mesh CaveMesh(StrataWorld world, int tile)
        {
            if (world.Caves == null || world.Caves[tile].Count == 0) return null;
            List<CombineInstance> parts = new();
            foreach (StrataCaves.Brick brick in world.Caves[tile])
                parts.Add(new CombineInstance { mesh = brick.Mesh, transform = Matrix4x4.Translate(brick.Origin) });
            Mesh mesh = new() { name = $"Strata Probe Caves {tile}", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        // The mean colour of the wall layers the caves use (grey if none is set).
        private static Color CaveColour(StrataWorld world)
        {
            Color sum = Color.clear;
            int count = 0;
            foreach (StrataCaveFeature cave in world.GetComponentsInChildren<StrataCaveFeature>())
            {
                if (cave.WallLayer == null) continue;
                sum += StrataTreeBuilder.MeanColour(cave.WallLayer.Albedo) * cave.WallLayer.Tint.linear;
                count++;
            }
            return count > 0 ? (sum / count).gamma : Color.gray;
        }

        // One tile's trees as cones on prisms, a crown and a trunk submesh per prototype; null if the tile has none.
        private static Mesh ForestMesh(StrataWorld world, int tile, out int count)
        {
            StrataTrees.Tile trees = world.Trees[tile];
            IReadOnlyList<StrataTreePrototype> prototypes = world.Trees.Prototypes;
            count = trees.Instances.IsCreated ? trees.Instances.Length : 0;
            if (count == 0 || prototypes.Count == 0) return null;

            List<Vector3> vertices = new(count * (ConeSides + 10));
            List<int>[] submeshes = new List<int>[prototypes.Count * 2];
            for (int i = 0; i < submeshes.Length; i++) submeshes[i] = new List<int>();
            for (int i = 0; i < count; i++)
            {
                StrataTreeRecord record = trees.Records[i];
                StrataTreePrototype prototype = record.Prototype < prototypes.Count ? prototypes[record.Prototype] : null;
                if (prototype == null) continue;
                float4 instance = trees.Instances[i];
                Vector3 root = new(instance.x, instance.y, instance.z);
                float height = prototype.Height * record.Scale;
                float reach = prototype.Reach * record.Scale;
                AddCone(vertices, submeshes[record.Prototype * 2], root + Vector3.up * height * CrownStart, height * (1f - CrownStart), reach * CrownWidth);
                AddPrism(vertices, submeshes[record.Prototype * 2 + 1], root, height * CrownStart, math.max(0.15f, reach * 0.08f));
            }

            Mesh mesh = new() { name = $"Strata Probe Trees {tile}", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.subMeshCount = submeshes.Length;
            for (int i = 0; i < submeshes.Length; i++) mesh.SetTriangles(submeshes[i], i, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // A closed cone: base ring at base, apex height above it.
        private static void AddCone(List<Vector3> vertices, List<int> triangles, Vector3 baseCentre, float height, float radius)
        {
            int start = vertices.Count;
            vertices.Add(baseCentre + Vector3.up * height);
            vertices.Add(baseCentre);
            for (int s = 0; s < ConeSides; s++)
            {
                float angle = s * Mathf.PI * 2f / ConeSides;
                vertices.Add(baseCentre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
            }
            for (int s = 0; s < ConeSides; s++)
            {
                int a = start + 2 + s, b = start + 2 + (s + 1) % ConeSides;
                triangles.Add(start); triangles.Add(b); triangles.Add(a);
                triangles.Add(start + 1); triangles.Add(a); triangles.Add(b);
            }
        }

        // An open four-sided prism standing on root.
        private static void AddPrism(List<Vector3> vertices, List<int> triangles, Vector3 root, float height, float radius)
        {
            int start = vertices.Count;
            for (int s = 0; s < 4; s++)
            {
                float angle = s * Mathf.PI * 0.5f;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                vertices.Add(root + offset);
                vertices.Add(root + offset + Vector3.up * height);
            }
            for (int s = 0; s < 4; s++)
            {
                int a = start + s * 2, b = start + (s + 1) % 4 * 2;
                triangles.Add(a); triangles.Add(a + 1); triangles.Add(b + 1);
                triangles.Add(a); triangles.Add(b + 1); triangles.Add(b);
            }
        }

        // Per prototype, a crown material (its foliage's mean colour) and a trunk material (its bark's).
        private static Material[] TreeMaterials(IReadOnlyList<StrataTreePrototype> prototypes)
        {
            Material[] materials = new Material[prototypes.Count * 2];
            for (int p = 0; p < prototypes.Count; p++)
            {
                Material[] sources = prototypes[p] != null && prototypes[p].LodCount > 0 ? prototypes[p].Lods[0].Materials : null;
                Material foliage = null, bark = null;
                if (sources != null)
                    foreach (Material source in sources)
                    {
                        if (source == null) continue;
                        bool leafy = source.HasFloat("_Foliage") && (source.GetFloat("_Foliage") > 0.5f || source.GetFloat("_FoliageSource") > 0.5f);
                        if (leafy) foliage ??= source;
                        else bark ??= source;
                    }
                materials[p * 2] = ProxyMaterial($"Strata Probe Crown {p}", MeanOf(foliage, new Color(0.18f, 0.3f, 0.12f)));
                materials[p * 2 + 1] = ProxyMaterial($"Strata Probe Trunk {p}", MeanOf(bark, new Color(0.3f, 0.22f, 0.15f)));
            }
            return materials;
        }

        private static Color MeanOf(Material material, Color fallback)
        {
            if (material == null || material.GetTexture("_BaseMap") is not Texture2D map) return fallback;
            Color linear = StrataTreeBuilder.MeanColour(map) * (material.HasColor("_BaseColor") ? material.GetColor("_BaseColor").linear : Color.white);
            return linear.gamma;
        }
    }
}
