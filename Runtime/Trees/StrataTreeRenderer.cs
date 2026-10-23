using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // Draws a world's trees through a BatchRendererGroup. Every tree is one instance in a single batch whose only
    // per-instance property is a float4 (base position, and yaw, scale and lean packed into w); the tree shaders build
    // the transform from it, so a tree costs 16 bytes of GPU memory. Each tile owns a segment of the instance buffer,
    // rewritten when its trees change. Per view, a parallel Burst job walks the 64 m cells, frustum-culls them, and gives
    // each run of a prototype its LOD: whole runs at once (as a range) when the cell lies within one LOD, otherwise tree
    // by tree, with trees in a crossfade band drawn by both LODs and their fade packed into the visible list; a second
    // job sorts the results into one draw command per prototype, LOD, submesh and set of shadow splits. Beyond the last mesh
    // LOD trees draw as impostor quads, which cast shadows into every cascade that sees them (cascades split by view depth
    // while LODs go by distance, so from above, impostors stand over ground in the nearer cascades too). The last
    // mesh LODs can cast the impostor's shadow instead (Impostor Shadow LODs): far meshes are costly in the shadow map.
    // That stand-in card is drawn with a copy of the impostor material pushed towards the light by part of the tree's
    // radius (Impostor Shadow Push): through the tree's centre it would leave the lit half of the mesh unshaded, brighter
    // than its neighbours. Refresh rebuilds the draws when a prototype is edited.
    public sealed unsafe class StrataTreeRenderer : IDisposable
    {
        private const int HeaderBytes = 64;
        private const int MatrixBytes = 48;
        private const int TreeDataOffset = HeaderBytes + MatrixBytes * 2;
        private const int MaxSplits = 6;
        // slots: the mesh LODs, the impostor (at LodCount), and the impostor standing in for mesh LOD shadows
        private const int SlotCount = StrataTreePrototype.MaxLods + 2;
        private const int StandInSlot = StrataTreePrototype.MaxLods + 1;
        private const float ReferenceHalfFov = 0.5235988f;

        private static readonly int ObjectToWorldId = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectId = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int TreeId = Shader.PropertyToID("_StrataTree");
        private static readonly int ShadowPushId = Shader.PropertyToID("_ShadowPush");

        private struct PrototypeCull
        {
            public float4 LodEnd;
            public int LodCount;
            public float ImpostorEnd;
            public float Crossfade;
            public bool HasImpostor;
            public bool CastShadows;
            public bool ImpostorShadows;
            // the first slot whose shadow is drawn by the impostor
            public int ShadowImpostorFrom;
            public float MaxEnd;
            public uint RenderingLayers;
        }

        // the draws for one prototype slot (a mesh LOD or the impostor): its submeshes in Submeshes
        private struct SlotDraws
        {
            public int First;
            public int Count;
        }

        private struct SubmeshDraw
        {
            public BatchMeshID Mesh;
            public BatchMaterialID Material;
            public ushort Submesh;
        }

        private readonly StrataTrees _trees;
        private readonly BatchRendererGroup _brg;
        private readonly Mesh _quad;
        private readonly BatchMeshID _quadId;
        private readonly List<BatchMeshID> _meshIds = new();
        private readonly List<BatchMaterialID> _materialIds = new();
        private readonly List<Material> _standInMaterials = new();
        private IReadOnlyList<StrataTreePrototype> _prototypeList = Array.Empty<StrataTreePrototype>();
        private int[] _prototypeVersions = Array.Empty<int>();
        private readonly int[] _segmentOffset;
        private readonly int[] _segmentCapacity;
        private GraphicsBuffer _instanceData;
        private BatchID _batch;
        private int _capacity;
        private NativeArray<float4> _instances;
        private NativeList<StrataTreeCell> _cells;
        private NativeList<StrataTreeRun> _runs;
        private NativeArray<PrototypeCull> _prototypes;
        private NativeArray<SlotDraws> _slots;
        private NativeArray<SubmeshDraw> _submeshes;
        private bool _cellsDirty = true;

        public bool CastShadows { get; set; } = true;
        // Off draws nothing (for profiling what the trees cost).
        public bool Enabled { get; set; } = true;
        public int Layer { get; set; }
        // Multiplies every prototype's LOD and impostor distances.
        public float DistanceScale { get; set; } = 1f;
        public double LastCullMilliseconds { get; private set; }
        public double LastCameraCullMilliseconds { get; private set; }
        public double LastShadowCullMilliseconds { get; private set; }
        public int LastShadowTrees { get; private set; }
        public int LastVisibleTrees { get; private set; }
        public long GpuBytes => _instanceData != null ? (long)_instanceData.count * 4 : 0;

        public StrataTreeRenderer(StrataTrees trees)
        {
            _trees = trees;
            StrataGrid grid = trees.Grid;
            _segmentOffset = new int[grid.TileCount];
            _segmentCapacity = new int[grid.TileCount];
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            _quad = BuildQuad();
            _quadId = _brg.RegisterMesh(_quad);
            _cells = new NativeList<StrataTreeCell>(Allocator.Persistent);
            _runs = new NativeList<StrataTreeRun>(Allocator.Persistent);
            _prototypes = new NativeArray<PrototypeCull>(1, Allocator.Persistent);
            _slots = new NativeArray<SlotDraws>(SlotCount, Allocator.Persistent);
            _submeshes = new NativeArray<SubmeshDraw>(1, Allocator.Persistent);

            float2 centre = grid.Origin + grid.Size * 0.5f;
            _brg.SetGlobalBounds(new Bounds(new Vector3(centre.x, (grid.MinHeight + grid.MaxHeight) * 0.5f, centre.y),
                                            new Vector3(grid.Size.x + 200f, grid.HeightRange + 400f, grid.Size.y + 200f)));
            SetPrototypes(trees.Prototypes);
            Reallocate();
            trees.Changed += OnTreesChanged;
        }

        // Registers the prototypes' meshes and materials (after the trees' prototypes changed).
        public void SetPrototypes(IReadOnlyList<StrataTreePrototype> prototypes)
        {
            foreach (BatchMaterialID id in _materialIds) _brg.UnregisterMaterial(id);
            foreach (BatchMeshID id in _meshIds) _brg.UnregisterMesh(id);
            _materialIds.Clear();
            _meshIds.Clear();
            DestroyStandInMaterials();
            _prototypes.Dispose();
            _slots.Dispose();
            _submeshes.Dispose();

            _prototypeList = prototypes;
            _prototypeVersions = new int[prototypes.Count];
            for (int p = 0; p < prototypes.Count; p++) _prototypeVersions[p] = prototypes[p] != null ? prototypes[p].Version : 0;
            int count = math.max(1, prototypes.Count);
            _prototypes = new NativeArray<PrototypeCull>(count, Allocator.Persistent);
            _slots = new NativeArray<SlotDraws>(count * SlotCount, Allocator.Persistent);
            List<SubmeshDraw> submeshes = new();
            for (int p = 0; p < prototypes.Count; p++)
            {
                StrataTreePrototype prototype = prototypes[p];
                if (prototype == null || !prototype.IsValid) continue;
                PrototypeCull cull = new()
                {
                    LodCount = prototype.LodCount,
                    Crossfade = prototype.Crossfade,
                    HasImpostor = prototype.HasImpostor,
                    ImpostorEnd = prototype.ImpostorDistance,
                    CastShadows = prototype.CastShadows,
                    ImpostorShadows = prototype.ImpostorShadows,
                    ShadowImpostorFrom = prototype.HasImpostor && prototype.ImpostorShadows
                        ? math.max(1, prototype.LodCount - prototype.ImpostorShadowLods)
                        : prototype.LodCount,
                    MaxEnd = prototype.MaxDistance,
                    RenderingLayers = prototype.RenderingLayers
                };
                for (int l = 0; l < prototype.LodCount; l++)
                {
                    StrataTreePrototype.Lod lod = prototype.Lods[l];
                    cull.LodEnd[l] = lod.Distance;
                    BatchMeshID mesh = _brg.RegisterMesh(lod.Mesh);
                    _meshIds.Add(mesh);
                    _slots[p * SlotCount + l] = new SlotDraws { First = submeshes.Count, Count = lod.Mesh.subMeshCount };
                    for (int s = 0; s < lod.Mesh.subMeshCount; s++)
                    {
                        BatchMaterialID material = _brg.RegisterMaterial(lod.Materials[s]);
                        _materialIds.Add(material);
                        submeshes.Add(new SubmeshDraw { Mesh = mesh, Material = material, Submesh = (ushort)s });
                    }
                }
                if (prototype.HasImpostor)
                {
                    BatchMaterialID material = _brg.RegisterMaterial(prototype.ImpostorMaterial);
                    _materialIds.Add(material);
                    _slots[p * SlotCount + prototype.LodCount] = new SlotDraws { First = submeshes.Count, Count = 1 };
                    submeshes.Add(new SubmeshDraw { Mesh = _quadId, Material = material, Submesh = 0 });
                }
                if (cull.ShadowImpostorFrom < prototype.LodCount)
                {
                    Material standIn = new(prototype.ImpostorMaterial) { name = $"{prototype.ImpostorMaterial.name} (Shadow Stand-in)", hideFlags = HideFlags.HideAndDontSave };
                    standIn.SetFloat(ShadowPushId, prototype.ImpostorShadowPush);
                    _standInMaterials.Add(standIn);
                    BatchMaterialID material = _brg.RegisterMaterial(standIn);
                    _materialIds.Add(material);
                    _slots[p * SlotCount + StandInSlot] = new SlotDraws { First = submeshes.Count, Count = 1 };
                    submeshes.Add(new SubmeshDraw { Mesh = _quadId, Material = material, Submesh = 0 });
                }
                _prototypes[p] = cull;
            }
            _submeshes = new NativeArray<SubmeshDraw>(math.max(1, submeshes.Count), Allocator.Persistent);
            for (int i = 0; i < submeshes.Count; i++) _submeshes[i] = submeshes[i];
        }

        // Registers the prototypes again if any was edited since.
        public void Refresh()
        {
            for (int p = 0; p < _prototypeVersions.Length; p++)
            {
                StrataTreePrototype prototype = p < _prototypeList.Count ? _prototypeList[p] : null;
                if ((prototype != null ? prototype.Version : 0) == _prototypeVersions[p]) continue;
                SetPrototypes(_prototypeList);
                return;
            }
        }

        private void OnTreesChanged(int tile)
        {
            if (tile < 0 || _trees[tile].Instances.Length > _segmentCapacity[tile])
            {
                Reallocate();
                return;
            }
            Upload(tile);
            _cellsDirty = true;
        }

        // Lays the tiles' segments out afresh with room to grow, and uploads everything.
        private void Reallocate()
        {
            StrataGrid grid = _trees.Grid;
            int total = 0;
            for (int t = 0; t < grid.TileCount; t++)
            {
                int count = _trees[t].Instances.Length;
                _segmentOffset[t] = total;
                _segmentCapacity[t] = count + count / 4 + 64;
                total += _segmentCapacity[t];
            }
            if (total > (1 << 24)) Debug.LogError($"Strata trees: {total} instance slots is more than one batch can address (16.7M).");

            if (_instanceData != null)
            {
                _brg.RemoveBatch(_batch);
                _instanceData.Dispose();
            }
            if (_instances.IsCreated) _instances.Dispose();
            _capacity = total;
            _instances = new NativeArray<float4>(total, Allocator.Persistent);
            int float4s = TreeDataOffset / 16 + total;
            _instanceData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, float4s * 4, 4);
            NativeArray<float4> header = new(TreeDataOffset / 16, Allocator.Temp, NativeArrayOptions.ClearMemory);
            // unity_ObjectToWorld and unity_WorldToObject, shared by every instance: identity as packed 3x4 columns
            for (int m = 0; m < 2; m++)
            for (int i = 0; i < 3; i++)
                header[HeaderBytes / 16 + m * 3 + i] = new float4(1f, 0f, 0f, 0f);
            _instanceData.SetData(header, 0, 0, header.Length);
            header.Dispose();

            NativeArray<MetadataValue> metadata = new(3, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = ObjectToWorldId, Value = HeaderBytes };
            metadata[1] = new MetadataValue { NameID = WorldToObjectId, Value = HeaderBytes + MatrixBytes };
            metadata[2] = new MetadataValue { NameID = TreeId, Value = 0x80000000 | TreeDataOffset };
            _batch = _brg.AddBatch(metadata, _instanceData.bufferHandle);
            metadata.Dispose();

            for (int t = 0; t < grid.TileCount; t++) Upload(t);
            _cellsDirty = true;
        }

        private void Upload(int tile)
        {
            NativeArray<float4> instances = _trees[tile].Instances;
            if (instances.Length == 0) return;
            NativeArray<float4>.Copy(instances, 0, _instances, _segmentOffset[tile], instances.Length);
            _instanceData.SetData(instances, 0, TreeDataOffset / 16 + _segmentOffset[tile], instances.Length);
        }

        // Every tile's cells and runs in one list, runs pointing at their trees' places in the instance buffer.
        private void RebuildCells()
        {
            _cellsDirty = false;
            _cells.Clear();
            _runs.Clear();
            for (int t = 0; t < _trees.Grid.TileCount; t++)
            {
                StrataTrees.Tile tile = _trees[t];
                int runBase = _runs.Length;
                for (int r = 0; r < tile.Runs.Length; r++)
                {
                    StrataTreeRun run = tile.Runs[r];
                    run.Start += _segmentOffset[t];
                    _runs.Add(run);
                }
                for (int c = 0; c < tile.Cells.Length; c++)
                {
                    StrataTreeCell cell = tile.Cells[c];
                    if (cell.RunCount == 0) continue;
                    cell.RunStart += runBase;
                    _cells.Add(cell);
                }
            }
        }

        private void DestroyStandInMaterials()
        {
            foreach (Material material in _standInMaterials) UnityEngine.Object.DestroyImmediate(material);
            _standInMaterials.Clear();
        }

        public void Dispose()
        {
            _trees.Changed -= OnTreesChanged;
            _brg.RemoveBatch(_batch);
            foreach (BatchMaterialID id in _materialIds) _brg.UnregisterMaterial(id);
            foreach (BatchMeshID id in _meshIds) _brg.UnregisterMesh(id);
            _brg.UnregisterMesh(_quadId);
            _brg.Dispose();
            DestroyStandInMaterials();
            _instanceData?.Dispose();
            if (_instances.IsCreated) _instances.Dispose();
            _cells.Dispose();
            _runs.Dispose();
            _prototypes.Dispose();
            _slots.Dispose();
            _submeshes.Dispose();
            UnityEngine.Object.DestroyImmediate(_quad);
        }

        private static Mesh BuildQuad()
        {
            Mesh mesh = new() { name = "Strata Impostor Quad", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f) });
            mesh.SetIndices(new[] { 0, 2, 1, 1, 2, 3 }, MeshTopology.Triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        private JobHandle OnPerformCulling(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr userContext)
        {
            if (!Enabled) return default;
            bool shadow = context.viewType == BatchCullingViewType.Light;
            if (context.viewType != BatchCullingViewType.Camera && !shadow) return default;
            if (shadow && !CastShadows) return default;
            if ((context.cullingLayerMask & (1u << Layer)) == 0) return default;
            if (!context.cullingPlanes.IsCreated || output.drawCommands.Length == 0 || _trees.Count == 0) return default;
            if (_cellsDirty) RebuildCells();

            int splits = shadow ? math.clamp(context.cullingSplits.Length, 1, MaxSplits) : 1;
            LODParameters lod = context.lodParameters;
            float fovScale = lod.isOrthographic ? 1f : math.tan(math.radians(lod.fieldOfView) * 0.5f) / math.tan(ReferenceHalfFov);
            BatchCullingOutputDrawCommands* commands = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            *commands = default;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            NativeArray<StrataTreeCell> cells = _cells.AsArray();
            NativeStream items = new(cells.Length, Allocator.TempJob);
            JobHandle classify = new ClassifyJob
            {
                Cells = cells,
                Runs = _runs.AsArray(),
                Instances = _instances,
                Prototypes = _prototypes,
                Slots = _slots,
                Planes = context.cullingPlanes,
                Splits = context.cullingSplits,
                SplitCount = splits,
                Shadow = shadow,
                CameraPosition = lod.cameraPosition,
                DistanceFactor = math.max(fovScale, 1e-3f) / math.max(DistanceScale, 1e-3f),
                Items = items.AsWriter()
            }.Schedule(cells.Length, 16);
            new EmitJob
            {
                Items = items.AsReader(),
                CellCount = cells.Length,
                BucketCount = _prototypes.Length * SlotCount * (shadow ? 1 << splits : 1),
                MaskCount = shadow ? 1 << splits : 1,
                Shadow = shadow,
                Prototypes = _prototypes,
                Slots = _slots,
                Submeshes = _submeshes,
                Output = commands,
                Batch = _batch,
                Layer = (byte)Layer
            }.Schedule(classify).Complete();
            items.Dispose();
            // completed inline, as the terrain renderer does, so the output is never touched after Unity releases it
            LastCullMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!shadow) (LastCameraCullMilliseconds, LastVisibleTrees) = (LastCullMilliseconds, commands->visibleInstanceCount);
            else (LastShadowCullMilliseconds, LastShadowTrees) = (LastCullMilliseconds, commands->visibleInstanceCount);
            return default;
        }

        // A cull result: a range of instances (x first, y count) or one instance with its fade packed in (x, y = -1),
        // and the draw bucket (z) it goes to.
        [BurstCompile]
        private struct ClassifyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<StrataTreeCell> Cells;
            [ReadOnly] public NativeArray<StrataTreeRun> Runs;
            [ReadOnly] public NativeArray<float4> Instances;
            [ReadOnly] public NativeArray<PrototypeCull> Prototypes;
            [ReadOnly] public NativeArray<SlotDraws> Slots;
            [ReadOnly] public NativeArray<Plane> Planes;
            [ReadOnly] public NativeArray<CullingSplit> Splits;
            public int SplitCount;
            public bool Shadow;
            public float3 CameraPosition;
            public float DistanceFactor;
            public NativeStream.Writer Items;

            public void Execute(int c)
            {
                Items.BeginForEachIndex(c);
                StrataTreeCell cell = Cells[c];
                int mask = Visibility(cell.Min, cell.Max);
                if (mask != 0)
                {
                    float near = NearDistance(cell.Min, cell.Max) * DistanceFactor;
                    float far = FarDistance(cell.Min, cell.Max) * DistanceFactor;
                    for (int r = cell.RunStart; r < cell.RunStart + cell.RunCount; r++)
                    {
                        StrataTreeRun run = Runs[r];
                        PrototypeCull prototype = Prototypes[run.Prototype];
                        if (prototype.LodCount == 0 || near >= prototype.MaxEnd) continue;
                        if (Shadow && !prototype.CastShadows) continue;
                        Choose(prototype, near, out int nearSlot, out float nearFade, out _);
                        Choose(prototype, far, out int farSlot, out float farFade, out _);
                        if (nearSlot == farSlot && nearSlot >= 0 && nearFade == 0f && farFade == 0f)
                        {
                            int bucket = Bucket(run.Prototype, nearSlot, prototype, mask);
                            if (bucket >= 0) Items.Write(new int3(run.Start, run.Count, bucket));
                            continue;
                        }
                        for (int i = run.Start; i < run.Start + run.Count; i++)
                        {
                            float distance = math.distance(Instances[i].xyz, CameraPosition) * DistanceFactor;
                            Choose(prototype, distance, out int slot, out float fade, out bool both);
                            if (slot < 0) continue;
                            int bucket = Bucket(run.Prototype, slot, prototype, mask);
                            if (bucket >= 0) Items.Write(new int3(Pack(i, fade), -1, bucket));
                            if (!both) continue;
                            bucket = Bucket(run.Prototype, slot + 1, prototype, mask);
                            if (bucket >= 0) Items.Write(new int3(Pack(i, -fade), -1, bucket));
                        }
                    }
                }
                Items.EndForEachIndex();
            }

            // The slot (mesh LOD, or LodCount for the impostor; -1 for none) drawn at a distance, and its fade: 0 when
            // not fading, otherwise how much of it shows; both says the next slot draws too, with the opposite fade.
            private static void Choose(PrototypeCull p, float distance, out int slot, out float fade, out bool both)
            {
                slot = -1;
                fade = 0f;
                both = false;
                int slots = p.HasImpostor ? p.LodCount + 1 : p.LodCount;
                for (int i = 0; i < slots; i++)
                {
                    float end = i < p.LodCount ? p.LodEnd[i] : p.ImpostorEnd;
                    if (distance >= end) continue;
                    slot = i;
                    float bandStart = end * (1f - p.Crossfade);
                    if (p.Crossfade > 0f && distance > bandStart)
                    {
                        float t = (distance - bandStart) / (end - bandStart);
                        fade = math.max(1f - t, 1f / 127f);
                        both = i + 1 < slots;
                    }
                    return;
                }
            }

            private int Bucket(int prototype, int slot, PrototypeCull p, int mask)
            {
                if (Shadow)
                {
                    if (slot >= p.ShadowImpostorFrom && slot < p.LodCount) slot = StandInSlot;
                    if (slot == p.LodCount && !p.ImpostorShadows) return -1;
                    if (mask == 0) return -1;
                }
                if (Slots[prototype * SlotCount + slot].Count == 0) return -1;
                int maskCount = Shadow ? 1 << SplitCount : 1;
                return (prototype * SlotCount + slot) * maskCount + (Shadow ? mask : 0);
            }

            private static int Pack(int index, float fade)
            {
                int snorm = (int)math.round(math.clamp(fade, -1f, 1f) * 127f);
                return index | ((snorm & 0xff) << 24);
            }

            // A bit per split whose planes the box is inside (or 1 for a camera if visible).
            private int Visibility(float3 min, float3 max)
            {
                float3 centre = (min + max) * 0.5f;
                float3 extent = (max - min) * 0.5f;
                if (Splits.Length == 0) return Inside(centre, extent, 0, Planes.Length) ? 1 : 0;
                int mask = 0;
                for (int s = 0; s < SplitCount; s++)
                {
                    CullingSplit split = Splits[s];
                    if (Inside(centre, extent, split.cullingPlaneOffset, split.cullingPlaneCount)) mask |= 1 << s;
                }
                return Shadow ? mask : math.min(mask, 1);
            }

            private bool Inside(float3 centre, float3 extent, int first, int count)
            {
                for (int p = first; p < first + count; p++)
                {
                    Plane plane = Planes[p];
                    float3 normal = plane.normal;
                    if (math.dot(normal, centre) + plane.distance + math.dot(extent, math.abs(normal)) < 0f) return false;
                }
                return true;
            }

            private float NearDistance(float3 min, float3 max) => math.length(math.max(0f, math.max(min - CameraPosition, CameraPosition - max)));
            private float FarDistance(float3 min, float3 max) => math.length(math.max(math.abs(min - CameraPosition), math.abs(max - CameraPosition)));

        }

        // Counts the classified instances per bucket, lays the buckets out in the visible list, and writes one draw
        // command per bucket and submesh.
        [BurstCompile]
        private struct EmitJob : IJob
        {
            [ReadOnly] public NativeStream.Reader Items;
            public int CellCount;
            public int BucketCount;
            public int MaskCount;
            public bool Shadow;
            [ReadOnly] public NativeArray<PrototypeCull> Prototypes;
            [ReadOnly] public NativeArray<SlotDraws> Slots;
            [ReadOnly] public NativeArray<SubmeshDraw> Submeshes;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            public BatchID Batch;
            public byte Layer;

            public void Execute()
            {
                NativeArray<int> counts = new(BucketCount, Allocator.Temp);
                for (int c = 0; c < CellCount; c++)
                {
                    int n = Items.BeginForEachIndex(c);
                    for (int i = 0; i < n; i++)
                    {
                        int3 item = Items.Read<int3>();
                        counts[item.z] += item.y < 0 ? 1 : item.y;
                    }
                    Items.EndForEachIndex();
                }
                NativeArray<int> offsets = new(BucketCount, Allocator.Temp);
                int total = 0, commandCount = 0;
                for (int b = 0; b < BucketCount; b++)
                {
                    offsets[b] = total;
                    total += counts[b];
                    if (counts[b] > 0) commandCount += Slots[b / MaskCount].Count;
                }

                Output->visibleInstances = Malloc<int>(math.max(1, total));
                Output->drawCommands = Malloc<BatchDrawCommand>(math.max(1, commandCount));
                // buckets run prototype by prototype, so a draw range is a run of prototypes on the same rendering layers
                Output->drawRanges = Malloc<BatchDrawRange>(math.max(1, commandCount));
                int command = 0, ranges = 0, rangeBegin = 0;
                uint rangeLayers = 0;
                for (int b = 0; b < BucketCount; b++)
                {
                    if (counts[b] == 0) continue;
                    uint layers = Prototypes[b / MaskCount / SlotCount].RenderingLayers;
                    if (command > rangeBegin && layers != rangeLayers)
                    {
                        Output->drawRanges[ranges++] = Range(rangeBegin, command, rangeLayers);
                        rangeBegin = command;
                    }
                    rangeLayers = layers;
                    SlotDraws draws = Slots[b / MaskCount];
                    for (int s = draws.First; s < draws.First + draws.Count; s++)
                    {
                        SubmeshDraw draw = Submeshes[s];
                        Output->drawCommands[command++] = new BatchDrawCommand
                        {
                            visibleOffset = (uint)offsets[b],
                            visibleCount = (uint)counts[b],
                            batchID = Batch,
                            materialID = draw.Material,
                            meshID = draw.Mesh,
                            submeshIndex = draw.Submesh,
                            splitVisibilityMask = Shadow ? (ushort)(b % MaskCount) : (ushort)0xff,
                            flags = BatchDrawCommandFlags.LODCrossFadeValuePacked,
                            sortingPosition = 0
                        };
                    }
                }
                for (int c = 0; c < CellCount; c++)
                {
                    int n = Items.BeginForEachIndex(c);
                    for (int i = 0; i < n; i++)
                    {
                        int3 item = Items.Read<int3>();
                        if (item.y < 0)
                        {
                            Output->visibleInstances[offsets[item.z]++] = item.x;
                            continue;
                        }
                        int at = offsets[item.z];
                        for (int k = 0; k < item.y; k++) Output->visibleInstances[at + k] = item.x + k;
                        offsets[item.z] = at + item.y;
                    }
                    Items.EndForEachIndex();
                }

                if (command > rangeBegin) Output->drawRanges[ranges++] = Range(rangeBegin, command, rangeLayers);
                Output->drawCommandCount = command;
                Output->visibleInstanceCount = total;
                Output->drawRangeCount = ranges;
            }

            private BatchDrawRange Range(int begin, int end, uint renderingLayers) => new()
            {
                drawCommandsType = BatchDrawCommandType.Direct,
                drawCommandsBegin = (uint)begin,
                drawCommandsCount = (uint)(end - begin),
                filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = renderingLayers,
                    layer = Layer,
                    motionMode = MotionVectorGenerationMode.Camera,
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true
                }
            };

            private static T* Malloc<T>(int count) where T : unmanaged =>
                (T*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<T>() * count, UnsafeUtility.AlignOf<T>(), Allocator.TempJob);
        }
    }
}
