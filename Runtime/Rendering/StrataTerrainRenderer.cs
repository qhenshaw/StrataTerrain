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
    // Draws a heightfield as CDLOD patches through a BatchRendererGroup. Every quadtree node of every tile is one
    // persistent BRG instance of a shared 33x33 grid mesh, its origin, size and level uploaded once; the surface shader
    // places the vertices from the height texture and morphs each patch towards the next coarser grid as it nears the
    // end of its range, so neighbouring levels meet without cracks. Per view, a Burst job walks each tile's quadtree,
    // frustum-culling nodes against their height bounds and choosing levels by camera distance, and writes the
    // visible instance list; shadow views group instances by the cascades that see them. Leaf nodes holding rock strata
    // can be swapped, within the strata's geometry range, for four detail instances at twice the density (level -1),
    // which the shader keeps on the leaf grid at their edges so they still meet their neighbours exactly. Nodes over a
    // cave hole are drawn with a second material, the shader's _STRATA_HOLES variant, which clips the holes; the rest keep
    // the plain variant so they still get early depth testing.
    public sealed unsafe class StrataTerrainRenderer : IDisposable
    {
        private const int HeaderBytes = 64;
        private const int MatrixBytes = 48;
        private const int NodeDataOffset = HeaderBytes + MatrixBytes * 2;
        private const int MaxSplits = 6;
        // patches start morphing to the coarser grid at this fraction of their range
        private const float MorphStart = 0.8f;

        private static readonly int ObjectToWorldId = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectId = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int NodeId = Shader.PropertyToID("_StrataNode");
        private const string HolesKeyword = "_STRATA_HOLES";
        // tiles that aren't resident are drawn no finer than this level, whose vertices fall on the far copy's samples
        private const int FarLevel = 3;
        // detail patches only where the leaf level isn't morphing yet, so their forced edges match it
        private const float DetailLimit = 0.75f;

        private readonly StrataHeightfield _heights;
        private readonly BatchRendererGroup _brg;
        private readonly GraphicsBuffer _instanceData;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly Material _holeMaterial;
        private readonly BatchID _batch;
        private readonly BatchMeshID _meshId;
        private readonly BatchMaterialID _materialId;
        private readonly BatchMaterialID _holeMaterialId;
        private readonly float _leafRange;
        private readonly int _detailCount;
        private StrataSplatmap _splats;
        private StrataPaletteTextures _palette;
        private GraphicsBuffer _bands;
        private NativeArray<float> _noDetail;

        public bool CastShadows { get; set; } = true;
        // Off draws nothing (for profiling what the terrain costs).
        public bool Enabled { get; set; } = true;
        // How long the last view took to cull, for the inspector.
        public double LastCullMilliseconds { get; private set; }
        // Patches (each 2048 triangles) the last camera view drew.
        public int LastVisiblePatches { get; private set; }
        public int Layer { get; set; }
        public Material Material => _material;
        public GraphicsBuffer Bands => _bands;
        // _StrataLod: leaf range, morph start, tiles z, detail range
        public Vector4 Lod => new(_leafRange, MorphStart, _heights.Grid.TilesZ, _leafRange * DetailLimit);

        public StrataTerrainRenderer(StrataHeightfield heights, Material material, float lodDistanceRatio)
        {
            _heights = heights;
            StrataGrid grid = heights.Grid;
            _leafRange = grid.NodeSize(0) * lodDistanceRatio;

            _material = new(material) { name = material.name + " (Strata)", hideFlags = HideFlags.HideAndDontSave };
            _material.DisableKeyword(HolesKeyword);
            _holeMaterial = new(material) { name = material.name + " (Strata Holes)", hideFlags = HideFlags.HideAndDontSave };
            _holeMaterial.EnableKeyword(HolesKeyword);

            _mesh = BuildPatchMesh();
            _brg = new(OnPerformCulling, IntPtr.Zero);
            _meshId = _brg.RegisterMesh(_mesh);
            _materialId = _brg.RegisterMaterial(_material);
            _holeMaterialId = _brg.RegisterMaterial(_holeMaterial);

            _detailCount = grid.TileCount * grid.LeavesPerSide * grid.LeavesPerSide * 4;
            _noDetail = new(grid.TileCount * grid.LeavesPerSide * grid.LeavesPerSide, Allocator.Persistent);
            int float4Count = NodeDataOffset / 16 + grid.NodeCount + _detailCount;
            _instanceData = new(GraphicsBuffer.Target.Raw, float4Count * 4, 4);
            NativeArray<float4> data = new(float4Count, Allocator.Temp, NativeArrayOptions.ClearMemory);
            // unity_ObjectToWorld and unity_WorldToObject, shared by every instance: identity as packed 3x4 columns
            for (int m = 0; m < 2; m++)
            for (int i = 0; i < 3; i++)
                data[HeaderBytes / 16 + m * 3 + i] = new float4(1f, 0f, 0f, 0f);
            for (int tz = 0; tz < grid.TilesZ; tz++)
            for (int tx = 0; tx < grid.TilesX; tx++)
            for (int level = 0; level < grid.Levels; level++)
            {
                int tile = grid.TileIndex(tx, tz);
                float size = grid.NodeSize(level);
                float2 tileOrigin = grid.TileOrigin(tx, tz);
                for (int z = 0; z < grid.NodesPerSide(level); z++)
                for (int x = 0; x < grid.NodesPerSide(level); x++)
                {
                    float2 origin = tileOrigin + new float2(x, z) * size;
                    data[NodeDataOffset / 16 + grid.NodeIndex(tile, level, x, z)] = new float4(origin.x, origin.y, size, level);
                }
            }
            // detail instances: four half-size children per leaf, after all the nodes
            float half = grid.NodeSize(0) * 0.5f;
            for (int tz = 0; tz < grid.TilesZ; tz++)
            for (int tx = 0; tx < grid.TilesX; tx++)
            for (int z = 0; z < grid.LeavesPerSide; z++)
            for (int x = 0; x < grid.LeavesPerSide; x++)
            {
                int leaf = (grid.TileIndex(tx, tz) * grid.LeavesPerSide + z) * grid.LeavesPerSide + x;
                float2 leafOrigin = grid.TileOrigin(tx, tz) + new float2(x, z) * grid.NodeSize(0);
                for (int c = 0; c < 4; c++)
                {
                    float2 origin = leafOrigin + new float2(c & 1, c >> 1) * half;
                    data[NodeDataOffset / 16 + grid.NodeCount + leaf * 4 + c] = new float4(origin.x, origin.y, half, -1f);
                }
            }
            _instanceData.SetData(data);
            data.Dispose();

            NativeArray<MetadataValue> metadata = new(3, Allocator.Temp);
            metadata[0] = new() { NameID = ObjectToWorldId, Value = HeaderBytes };
            metadata[1] = new() { NameID = WorldToObjectId, Value = HeaderBytes + MatrixBytes };
            metadata[2] = new() { NameID = NodeId, Value = 0x80000000 | NodeDataOffset };
            _batch = _brg.AddBatch(metadata, _instanceData.bufferHandle);
            metadata.Dispose();

            float2 centre = grid.Origin + grid.Size * 0.5f;
            _brg.SetGlobalBounds(new Bounds(new Vector3(centre.x, (grid.MinHeight + grid.MaxHeight) * 0.5f, centre.y),
                                            new Vector3(grid.Size.x, grid.HeightRange, grid.Size.y)));
            SetStrata(Array.Empty<StrataBandDefinition>());
            Rebind();
        }

        public void SetTexturing(StrataSplatmap splats, StrataPaletteTextures palette)
        {
            _splats = splats;
            _palette = palette;
            Rebind();
        }

        // Sets every property the surface shader reads. Unity drops textures and buffers a shader doesn't declare when
        // the shader is reimported, so the world calls this before each camera renders.
        public void Rebind()
        {
            StrataMaterialBinding.Bind(_material, _heights, _splats, _palette, _bands, Lod);
            StrataMaterialBinding.Bind(_holeMaterial, _heights, _splats, _palette, _bands, Lod);
        }

        // The rock strata definitions the strata map's indices refer to.
        public void SetStrata(IReadOnlyList<StrataBandDefinition> definitions)
        {
            int count = Math.Max(1, definitions.Count);
            Vector4[] packed = new Vector4[count * StrataBandDefinition.Float4s];
            for (int i = 0; i < definitions.Count; i++) definitions[i].Pack(packed, i);
            if (_bands == null || _bands.count != packed.Length)
            {
                _bands?.Dispose();
                _bands = new GraphicsBuffer(GraphicsBuffer.Target.Structured, packed.Length, 16);
                StrataMaterialBinding.SetBands(_material, _bands);
                StrataMaterialBinding.SetBands(_holeMaterial, _bands);
            }
            _bands.SetData(packed);
        }

        public void Dispose()
        {
            _heights.Readers.Complete();
            _bands?.Dispose();
            if (_noDetail.IsCreated) _noDetail.Dispose();
            _brg.RemoveBatch(_batch);
            _brg.UnregisterMaterial(_materialId);
            _brg.UnregisterMaterial(_holeMaterialId);
            _brg.UnregisterMesh(_meshId);
            _brg.Dispose();
            _instanceData.Dispose();
            UnityEngine.Object.DestroyImmediate(_mesh);
            UnityEngine.Object.DestroyImmediate(_material);
            UnityEngine.Object.DestroyImmediate(_holeMaterial);
        }

        // A flat grid in patch units (0..32 on x and z); the shader scales and places it per node.
        private static Mesh BuildPatchMesh()
        {
            const int side = StrataGrid.PatchQuads + 1;
            Vector3[] vertices = new Vector3[side * side];
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                vertices[z * side + x] = new Vector3(x, 0f, z);

            int[] indices = new int[StrataGrid.PatchQuads * StrataGrid.PatchQuads * 6];
            int n = 0;
            for (int z = 0; z < StrataGrid.PatchQuads; z++)
            for (int x = 0; x < StrataGrid.PatchQuads; x++)
            {
                int i = z * side + x;
                indices[n++] = i; indices[n++] = i + side; indices[n++] = i + 1;
                indices[n++] = i + 1; indices[n++] = i + side; indices[n++] = i + side + 1;
            }

            Mesh mesh = new() { name = "Strata Patch", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
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
            if (!_heights.NodeMinMax.IsCreated || !context.cullingPlanes.IsCreated || output.drawCommands.Length == 0) return default;

            StrataGrid grid = _heights.Grid;
            int splits = shadow ? math.clamp(context.cullingSplits.Length, 1, MaxSplits) : 1;
            BatchCullingOutputDrawCommands* commands = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            *commands = default;
            commands->drawCommands = Malloc<BatchDrawCommand>(2 << splits);
            commands->drawRanges = Malloc<BatchDrawRange>(1);
            commands->visibleInstances = Malloc<int>(grid.NodeCount + _detailCount);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            JobHandle job = new CullJob
            {
                Grid = grid,
                NodeMinMax = _heights.NodeMinMax,
                NodeHoles = _heights.Holes.NodeHoles,
                TileSlots = _heights.TileSlots,
                FarLevel = math.min(FarLevel, grid.Levels - 1),
                LeafDetailRange = _splats != null && _splats.LeafDetailRange.IsCreated ? _splats.LeafDetailRange : _noDetail,
                DetailRange = _leafRange * DetailLimit,
                Planes = context.cullingPlanes,
                Splits = context.cullingSplits,
                SplitCount = splits,
                Shadow = shadow,
                CameraPosition = context.lodParameters.cameraPosition,
                LeafRange = _leafRange,
                Output = commands,
                Batch = _batch,
                Mesh = _meshId,
                Material = _materialId,
                HoleMaterial = _holeMaterialId,
                Layer = (byte)Layer
            }.Schedule(_heights.Readers);
            // Culled inline rather than handing the job back: in the first frames after a group is created, a returned
            // job could run after Unity had released this view's output (Burst null references while rebuilding).
            // Walking a few thousand nodes in Burst is cheap enough to do here.
            job.Complete();
            LastCullMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!shadow) LastVisiblePatches = commands->visibleInstanceCount;
            return default;
        }

        private static T* Malloc<T>(int count) where T : unmanaged =>
            (T*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<T>() * count, UnsafeUtility.AlignOf<T>(), Allocator.TempJob);

        [BurstCompile]
        private struct CullJob : IJob
        {
            public StrataGrid Grid;
            [ReadOnly] public NativeArray<float2> NodeMinMax;
            [ReadOnly] public NativeArray<byte> NodeHoles;
            [ReadOnly] public NativeArray<int> TileSlots;
            public int FarLevel;
            [ReadOnly] public NativeArray<float> LeafDetailRange;
            public float DetailRange;
            [ReadOnly] public NativeArray<Plane> Planes;
            [ReadOnly] public NativeArray<CullingSplit> Splits;
            public int SplitCount;
            public bool Shadow;
            public float3 CameraPosition;
            public float LeafRange;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            public BatchID Batch;
            public BatchMeshID Mesh;
            public BatchMaterialID Material;
            public BatchMaterialID HoleMaterial;
            public byte Layer;

            public void Execute()
            {
                // instance, split mask, and 1 if it is over a hole
                NativeList<int3> selected = new(1024, Allocator.Temp);
                NativeList<int4> stack = new(64, Allocator.Temp);
                int top = Grid.Levels - 1;
                for (int tz = 0; tz < Grid.TilesZ; tz++)
                for (int tx = 0; tx < Grid.TilesX; tx++)
                    stack.Add(new int4(Grid.TileIndex(tx, tz), top, 0, 0));

                while (stack.Length > 0)
                {
                    int4 node = stack[^1];
                    stack.RemoveAt(stack.Length - 1);
                    int tile = node.x, level = node.y;
                    int index = Grid.NodeIndex(tile, level, node.z, node.w);
                    float size = Grid.NodeSize(level);
                    float2 origin = Grid.TileOrigin(tile % Grid.TilesX, tile / Grid.TilesX) + new float2(node.z, node.w) * size;
                    float2 minMax = NodeMinMax[index];
                    float3 min = new(origin.x, minMax.x, origin.y);
                    float3 max = new(origin.x + size, minMax.y, origin.y + size);

                    int mask = Visibility(min, max);
                    if (mask == 0) continue;
                    int holed = NodeHoles[index];
                    if (level == 0)
                    {
                        // a leaf holding strata, near enough, is drawn as four denser detail patches instead
                        int leaf = (tile * Grid.LeavesPerSide + node.w) * Grid.LeavesPerSide + node.z;
                        float detail = math.min(LeafDetailRange[leaf], DetailRange);
                        if (detail > 0f && Distance(min, max) < detail)
                        {
                            float half = size * 0.5f;
                            for (int c = 0; c < 4; c++)
                            {
                                float3 childMin = min + new float3((c & 1) * half, 0f, (c >> 1) * half);
                                int childMask = Visibility(childMin, new float3(childMin.x + half, max.y, childMin.z + half));
                                if (childMask != 0) selected.Add(new int3(Grid.NodeCount + leaf * 4 + c, childMask, holed));
                            }
                            continue;
                        }
                        selected.Add(new int3(index, mask, holed));
                        continue;
                    }
                    // a tile that isn't resident has only the far copy's samples to show, every 2^FarLevel quads
                    if (Distance(min, max) > LeafRange * (1 << (level - 1)) || level == FarLevel && TileSlots[tile] < 0)
                    {
                        selected.Add(new int3(index, mask, holed));
                        continue;
                    }
                    for (int c = 0; c < 4; c++)
                        stack.Add(new int4(tile, level - 1, node.z * 2 + (c & 1), node.w * 2 + (c >> 1)));
                }

                // bucket instances by the set of splits that see them, and by whether they clip holes, one draw command per
                // bucket
                int sets = 1 << SplitCount;
                int buckets = sets * 2;
                NativeArray<int> counts = new(buckets, Allocator.Temp);
                for (int i = 0; i < selected.Length; i++) counts[selected[i].y + selected[i].z * sets]++;
                NativeArray<int> offsets = new(buckets, Allocator.Temp);
                int commandCount = 0, total = 0;
                for (int b = 0; b < buckets; b++)
                {
                    offsets[b] = total;
                    if (counts[b] == 0) continue;
                    Output->drawCommands[commandCount++] = new BatchDrawCommand
                    {
                        visibleOffset = (uint)total,
                        visibleCount = (uint)counts[b],
                        batchID = Batch,
                        materialID = b >= sets ? HoleMaterial : Material,
                        meshID = Mesh,
                        submeshIndex = 0,
                        splitVisibilityMask = Shadow ? (ushort)(b % sets) : (ushort)0xff,
                        flags = BatchDrawCommandFlags.None,
                        sortingPosition = 0
                    };
                    total += counts[b];
                }
                for (int i = 0; i < selected.Length; i++)
                {
                    int3 s = selected[i];
                    Output->visibleInstances[offsets[s.y + s.z * sets]++] = s.x;
                }

                Output->drawCommandCount = commandCount;
                Output->visibleInstanceCount = total;
                Output->drawRangeCount = commandCount > 0 ? 1 : 0;
                BatchFilterSettings filter = new()
                {
                    renderingLayerMask = 1,
                    layer = Layer,
                    motionMode = MotionVectorGenerationMode.Camera,
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true
                };
                Output->drawRanges[0] = new BatchDrawRange
                {
                    drawCommandsType = BatchDrawCommandType.Direct,
                    drawCommandsBegin = 0,
                    drawCommandsCount = (uint)commandCount,
                    filterSettings = filter
                };
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

            private float Distance(float3 min, float3 max) => math.length(math.max(0f, math.max(min - CameraPosition, CameraPosition - max)));
        }
    }
}
