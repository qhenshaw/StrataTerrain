using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // The root of a Strata world. Its Settings, Palette and Baked Data blocks hold the world's grid and options, the
    // layers it paints with, and what the last bake produced. It owns the heightfield, trees and grass densities (loaded
    // from the baked data, or flat and bare until the first bake), draws them with a StrataTerrainRenderer, a
    // StrataTreeRenderer and a StrataGrassRenderer (which generates grass on the GPU per camera) in edit and play mode,
    // and its baked caves with StrataCaves, and in play mode streams terrain, tree trunk and cave collision around the
    // registered focus transforms and StrataFocus components. Authoring features (stamps, splines, filters, caves) live
    // under this object in the hierarchy; the editor evaluates them for the live preview and the bake.
    [ExecuteAlways, DisallowMultipleComponent]
    public class StrataWorld : MonoBehaviour
    {
        private static readonly int CameraPositionId = Shader.PropertyToID("_StrataCameraPosition");
        private static readonly int GroundSlotsId = Shader.PropertyToID("_StrataGroundSlots");
        private static readonly int GroundColoursId = Shader.PropertyToID("_StrataGroundColours");
        private static readonly int GroundIndicesId = Shader.PropertyToID("_StrataGroundIndices");
        private static readonly int GroundWeightsId = Shader.PropertyToID("_StrataGroundWeights");
        private static readonly int GroundFarIndicesId = Shader.PropertyToID("_StrataGroundFarIndices");
        private static readonly int GroundFarWeightsId = Shader.PropertyToID("_StrataGroundFarWeights");
        private static readonly int GroundGridId = Shader.PropertyToID("_StrataGroundGrid");
        private static readonly int GroundParamsId = Shader.PropertyToID("_StrataGroundParams");
        private static readonly List<StrataWorld> s_active = new();

        [SerializeField] private Material _material;
        [Tooltip("Material for cave walls; its texturing settings follow the terrain material's.")]
        [SerializeField] private Material _caveMaterial;
        [Tooltip("Strata's StrataGrass.compute; assign it so builds include it.")]
        [SerializeField] private ComputeShader _grassShader;
        [SerializeField] private bool _castShadows = true;
        [Tooltip("Stream collision around the main camera as well as the registered focus transforms.")]
        [SerializeField] private bool _cameraIsFocus;
        [SerializeField] private StrataWorldSettings _settings = new();
        [SerializeField] private StrataPalette _palette = new();
        [Tooltip("Written by Bake: where the baked tiles are and what they refer to.")]
        [SerializeField] private StrataWorldData _data = new();

        private readonly List<Transform> _foci = new();
        private readonly List<Transform> _focusScratch = new();
        private readonly List<Unity.Mathematics.float3> _centres = new();
        private bool _warnedNoFocus;
        private Material _caveInstance;

        public static IReadOnlyList<StrataWorld> Active => s_active;
        public StrataWorldSettings Settings => _settings;
        public StrataPalette Palette => _palette;
        public StrataWorldData Data => _data;
        public StrataHeightfield Heightfield { get; private set; }
        public StrataSplatmap Splatmap { get; private set; }
        public StrataPaletteTextures PaletteTextures { get; private set; }
        public StrataTerrainRenderer Renderer { get; private set; }
        public StrataCollision Collision { get; private set; }
        public StrataTrees Trees { get; private set; }
        public StrataTreeRenderer TreeRenderer { get; private set; }
        public StrataTreeColliders TreeColliders { get; private set; }
        public StrataGrassMap Grass { get; private set; }
        public StrataCaves Caves { get; private set; }
        public StrataCaveColliders CaveColliders { get; private set; }
        // Streams tiles in play mode (null when every tile is resident).
        public StrataStreamer Streamer { get; private set; }
        public StrataGrassRenderer GrassRenderer { get; private set; }
        public IReadOnlyList<StrataGrassPrototype> GrassPrototypes { get; private set; } = Array.Empty<StrataGrassPrototype>();
        public IReadOnlyList<Transform> Foci => _foci;

        // Raised after the heightfield and renderer are (re)created, and after heights change.
        public event Action Rebuilt;
        public event Action HeightsChanged;

        public void RegisterFocus(Transform focus)
        {
            if (focus != null && !_foci.Contains(focus)) _foci.Add(focus);
        }

        public void UnregisterFocus(Transform focus) => _foci.Remove(focus);

        private void OnValidate()
        {
            _settings.Validate();
            _palette.Validate();
        }

        private void OnEnable()
        {
            s_active.Add(this);
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            Build();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            s_active.Remove(this);
            Release();
        }

        // Recreates everything from the settings and data, e.g. after the grid changed or a bake finished.
        public void Rebuild()
        {
            Release();
            Build();
        }

        // Call after changing Heightfield's CPU heights (bounds and GPU copy already updated) so collision follows.
        public void NotifyHeightsChanged()
        {
            Collision?.RebuildAll();
            HeightsChanged?.Invoke();
        }

        private void Reset()
        {
#if UNITY_EDITOR
            _material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(DefaultMaterialPath);
            _caveMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(DefaultCaveMaterialPath);
            _grassShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(GrassShaderPath);
#endif
        }

        public const string DefaultMaterialPath = "Packages/com.qhenshaw.terrain/Shaders/StrataTerrain.mat";
        public const string DefaultCaveMaterialPath = "Packages/com.qhenshaw.terrain/Shaders/StrataCave.mat";
        public const string GrassShaderPath = "Packages/com.qhenshaw.terrain/Runtime/Compute/StrataGrass.compute";

        private void Build()
        {
            if (!_settings.Grid.IsValid) return;
            // without a material asset the shader may be stripped from builds, so this fallback is for the editor
            Shader fallback = _material == null ? Shader.Find("Strata/TerrainSurface") : null;
            if (_material == null && fallback == null)
            {
                Debug.LogError($"{name}: no terrain material assigned and the Strata/TerrainSurface shader isn't available.", this);
                return;
            }
            Material material = _material != null ? _material : new Material(fallback);
            bool baked = _data.Grid.Equals(_settings.Grid) && _data.IsComplete;
            if (_data.Grid.IsValid && !baked)
                Debug.LogWarning($"{name}: the baked data is missing or doesn't match the world settings ({_settings.Grid}); bake again.", this);
            // play mode streams tiles in around the cameras and foci; the editor keeps every tile resident to edit them
            bool stream = baked && Application.isPlaying && _settings.Streaming && _settings.ResidentTiles < _settings.Grid.TileCount;
            // a streamed world needs hole slots only for its holed tiles, at most one per resident tile
            int holeSlots = stream ? Math.Clamp(_data.HoledTiles, 1, _settings.ResidentTiles) : 0;
            Heightfield = new StrataHeightfield(_settings.Grid, stream ? _settings.ResidentTiles : 0, holeSlots);
            Splatmap = new StrataSplatmap(_settings.Grid, Heightfield.SlotCount);
            if (stream)
            {
                using StrataFarFile.Data far = StrataFarFile.Read(_data.FarPath, _settings.Grid, Unity.Collections.Allocator.Temp);
                Heightfield.LoadFar(far);
                Splatmap.LoadFar(far);
            }
            else if (baked)
            {
                Heightfield.Load(_data);
                Splatmap.Load(_data);
            }
            Renderer = new StrataTerrainRenderer(Heightfield, material, _settings.LodDistanceRatio) { CastShadows = _castShadows, Layer = gameObject.layer };
            BuildCaves(material, baked && !stream);
            if (stream) Caves?.LoadFar(_data.FarCavesPath);
            if (_material == null) DestroyImmediate(material);
            RebuildPalette();
            SetStrataDefinitions(baked ? _data.Strata : Array.Empty<StrataBandDefinition>());
            Trees = new StrataTrees(Heightfield);
            if (baked) Trees.Load(_data);
            TreeRenderer = new StrataTreeRenderer(Trees) { CastShadows = _castShadows, Layer = gameObject.layer, DistanceScale = _settings.TreeDistanceScale };
            ComputeShader grassShader = _grassShader;
#if UNITY_EDITOR
            if (grassShader == null) grassShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(GrassShaderPath);
#endif
            if (grassShader != null)
            {
                GrassRenderer = new StrataGrassRenderer(Heightfield, grassShader) { Layer = gameObject.layer };
                SetGrassPrototypes(baked ? _data.GrassPrototypes : Array.Empty<StrataGrassPrototype>());
                if (baked && !stream) Grass.Load(_data);
            }
            if (stream)
                Streamer = new StrataStreamer(_data, Heightfield, Splatmap, Grass, Trees, Caves, _settings.StreamingRadius,
                                              _settings.CollisionRadius + _settings.CollisionChunkSize, _settings.MaxConcurrentLoads);
            if (Application.isPlaying)
            {
                Collision = new StrataCollision(Heightfield, transform, _settings.CollisionChunkSize, _settings.CollisionRadius, _settings.CollisionLayer);
                TreeColliders = new StrataTreeColliders(Trees, Collision, transform, _settings.CollisionLayer);
                if (Caves != null) CaveColliders = new StrataCaveColliders(Caves, Collision, transform, _settings.CollisionLayer);
            }
            Rebuilt?.Invoke();
        }

        // The cave meshes and their material (a copy of the cave material, textured like the terrain), loaded from every
        // tile when the world is resident; a streamed world's tiles load theirs as they come in.
        private void BuildCaves(Material terrain, bool load)
        {
            Material source = _caveMaterial;
#if UNITY_EDITOR
            if (source == null) source = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(DefaultCaveMaterialPath);
#endif
            Shader fallback = source == null ? Shader.Find("Strata/CaveSurface") : null;
            if (source == null && fallback == null) return;
            _caveInstance = source != null ? new Material(source) : new Material(fallback);
            _caveInstance.name = "Strata Caves";
            _caveInstance.hideFlags = HideFlags.HideAndDontSave;
            StrataMaterialBinding.CopyLook(terrain, _caveInstance);
            Caves = new StrataCaves(_settings.Grid, transform, _caveInstance, gameObject.layer, _castShadows && _settings.CaveShadows);
            if (!load) return;
            for (int tile = 0; tile < _settings.Grid.TileCount; tile++)
            {
                Unity.Collections.NativeArray<byte> file = _data.ReadTile(tile, Unity.Collections.Allocator.Temp);
                Caves.SetTile(tile, file, _settings.Grid.SplatSamples);
                file.Dispose();
            }
        }

        // The rock strata definitions the strata map refers to (from the bake, or set live by the editor preview).
        public IReadOnlyList<StrataBandDefinition> StrataDefinitions { get; private set; } = Array.Empty<StrataBandDefinition>();

        public void SetStrataDefinitions(StrataBandDefinition[] definitions)
        {
            StrataDefinitions = definitions;
            Renderer?.SetStrata(definitions);
        }

        // The tree prototypes the trees' records refer to (from the bake, or set live by the editor preview).
        public void SetTreePrototypes(IReadOnlyList<StrataTreePrototype> prototypes)
        {
            if (Trees == null) return;
            TreeRenderer.SetPrototypes(prototypes);
            Trees.SetPrototypes(prototypes);
        }

        // The grass prototypes the density map holds, in order; the map is recreated (bare) for them.
        public void SetGrassPrototypes(IReadOnlyList<StrataGrassPrototype> prototypes)
        {
            if (GrassRenderer == null || Heightfield == null) return;
            GrassPrototypes = prototypes ?? Array.Empty<StrataGrassPrototype>();
            Grass?.Dispose();
            Grass = new StrataGrassMap(Heightfield.Grid, GrassPrototypes.Count, Heightfield.SlotCount);
            GrassRenderer.SetPrototypes(GrassPrototypes, Grass);
        }

        // The globals StrataGround.hlsl reads (for grass tinted by the ground); false if this world has nothing to show.
        private bool SetGroundGlobals()
        {
            if (Heightfield == null || Splatmap == null || PaletteTextures == null) return false;
            StrataGrid grid = Heightfield.Grid;
            Unity.Mathematics.int2 far = StrataHeights.FarSplatSamples(grid) - 1;
            Shader.SetGlobalBuffer(GroundSlotsId, Heightfield.TileSlotBuffer);
            Shader.SetGlobalBuffer(GroundColoursId, PaletteTextures.LayerColours);
            Shader.SetGlobalTexture(GroundIndicesId, Splatmap.Indices);
            Shader.SetGlobalTexture(GroundWeightsId, Splatmap.Weights);
            Shader.SetGlobalTexture(GroundFarIndicesId, Splatmap.FarIndices);
            Shader.SetGlobalTexture(GroundFarWeightsId, Splatmap.FarWeights);
            Shader.SetGlobalVector(GroundGridId, new Vector4(grid.Origin.x, grid.Origin.y, grid.TileSize, grid.TilesX));
            Shader.SetGlobalVector(GroundParamsId, new Vector4(grid.TilesZ, grid.SplatResolution, far.x, far.y));
            return true;
        }

        // Recomputes where one tile's detail patches go, from its strata map (read back from the GPU).
        public void UpdateStrataLeaves(int tile, Unity.Collections.NativeArray<ushort> strataMap)
        {
            if (Splatmap == null) return;
            Heightfield.Readers.Complete();
            Splatmap.UpdateLeaves(tile, strataMap, StrataDefinitions);
        }

        // Re-packs the palette's textures and settings, e.g. after a layer changed.
        public void RebuildPalette()
        {
            if (Renderer == null) return;
            PaletteTextures?.Dispose();
            PaletteTextures = new StrataPaletteTextures(_palette);
            foreach (string problem in PaletteTextures.Problems) Debug.LogWarning($"{name}: {problem}", this);
            Renderer.SetTexturing(Splatmap, PaletteTextures);
        }

        private void Release()
        {
            Streamer?.Dispose();
            Streamer = null;
            CaveColliders?.Dispose();
            TreeColliders?.Dispose();
            Collision?.Dispose();
            Caves?.Dispose();
            if (_caveInstance != null) DestroyImmediate(_caveInstance);
            TreeRenderer?.Dispose();
            Trees?.Dispose();
            GrassRenderer?.Dispose();
            Grass?.Dispose();
            Renderer?.Dispose();
            PaletteTextures?.Dispose();
            Splatmap?.Dispose();
            Heightfield?.Dispose();
            CaveColliders = null;
            TreeColliders = null;
            Collision = null;
            Caves = null;
            _caveInstance = null;
            TreeRenderer = null;
            Trees = null;
            GrassRenderer = null;
            Grass = null;
            GrassPrototypes = Array.Empty<StrataGrassPrototype>();
            Renderer = null;
            PaletteTextures = null;
            Splatmap = null;
            Heightfield = null;
        }

        private void Update()
        {
            if (Streamer != null)
            {
                _centres.Clear();
                foreach (Transform focus in _foci)
                    if (focus != null) _centres.Add(focus.position);
                foreach (Transform focus in StrataFocus.Active) _centres.Add(focus.position);
                foreach (Camera camera in Camera.allCameras) _centres.Add(camera.transform.position);
#if UNITY_EDITOR
                foreach (UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
                    if (view != null && view.camera != null && view.hasFocus) _centres.Add(view.camera.transform.position);
#endif
                Streamer.Update(_centres);
            }
            if (Collision == null) return;
            _focusScratch.Clear();
            _focusScratch.AddRange(_foci);
            _focusScratch.AddRange(StrataFocus.Active);
            if (_cameraIsFocus && Camera.main != null) _focusScratch.Add(Camera.main.transform);
            if (_focusScratch.Count == 0 && !_warnedNoFocus)
            {
                _warnedNoFocus = true;
                Debug.LogWarning($"{name}: no collision focus, so the terrain has no collision. Add a StrataFocus to the player " +
                                 "(or call RegisterFocus), or tick Camera Is Focus.", this);
            }
            Collision.Update(_focusScratch);
        }

        // the culling job chooses levels from this camera, so every pass must morph with the same position; materials
        // are rebound too, as a shader reimport can drop their textures and buffers
        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            Shader.SetGlobalVector(CameraPositionId, camera.transform.position);
            for (int i = 0; i < s_active.Count; i++)
                if (s_active[i].SetGroundGlobals()) break;
            foreach (StrataWorld world in s_active)
            {
                world.Renderer?.Rebind();
                if (world._caveInstance != null && world.Renderer != null)
                    StrataMaterialBinding.Bind(world._caveInstance, world.Heightfield, world.Splatmap, world.PaletteTextures, world.Renderer.Bands, world.Renderer.Lod);
                if (world.Caves != null)
                {
                    world.Caves.LodDistance = world._settings.CaveLodDistance;
                    world.Caves.UndergroundDistance = world._settings.CaveUndergroundDistance;
                    world.Caves.Select(camera.transform.position);
                }
                if (world.TreeRenderer == null) continue;
                world.TreeRenderer.DistanceScale = world._settings.TreeDistanceScale;
                world.TreeRenderer.Refresh();
            }
        }
    }
}
