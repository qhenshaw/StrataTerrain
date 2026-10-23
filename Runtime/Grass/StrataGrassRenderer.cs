using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // Draws a world's grass through a BatchRendererGroup with indirect draws, generating it on the GPU for each camera
    // just before the camera renders (StrataGrass.compute): every grass prototype gets a region of the camera's instance
    // buffer and an indirect argument entry per submesh, which the compute shader fills. The BRG batch's only
    // per-instance property is Strata/Tree's _StrataTree float4, so grass draws with the tree shader. Nothing per clump
    // is kept between frames; a camera's buffers are dropped once the camera is gone. Grass casts no shadows.
    public sealed unsafe class StrataGrassRenderer : IDisposable
    {
        private const int HeaderBytes = 64;
        private const int MatrixBytes = 48;
        private const int InstanceOffset = HeaderBytes + MatrixBytes * 2;
        private const int MaxTypes = 8;

        private static readonly int ObjectToWorldId = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectId = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int TreeId = Shader.PropertyToID("_StrataTree");

        private sealed class CameraState
        {
            public GraphicsBuffer Instances;
            public GraphicsBuffer Args;
            public BatchID Batch;
        }

        private struct Draw
        {
            public BatchMeshID Mesh;
            public BatchMaterialID Material;
            public ushort Submesh;
            public int Entry;
            public int Type;
        }

        private readonly StrataHeightfield _heights;
        private readonly ComputeShader _shader;
        private readonly BatchRendererGroup _brg;
        private readonly Dictionary<Camera, CameraState> _cameras = new();
        private readonly List<Camera> _gone = new();
        private readonly List<BatchMeshID> _meshIds = new();
        private readonly List<BatchMaterialID> _materialIds = new();
        private readonly List<Draw> _draws = new();
        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _planeVectors = new Vector4[6];
        private readonly int _reset, _scatter, _finish;
        private StrataGrassMap _map;
        private IReadOnlyList<StrataGrassPrototype> _prototypes = Array.Empty<StrataGrassPrototype>();
        private GraphicsBuffer _types;
        private GraphicsBuffer _visible;
        private GraphicsBufferIndirectArgs[] _argsTemplate = Array.Empty<GraphicsBufferIndirectArgs>();
        private Vector4[] _typeData = Array.Empty<Vector4>();
        private int _capacity;
        private float _spacing = 1f;
        private float _range;
        private CameraState _current;

        public int Layer { get; set; }
        // Off generates and draws nothing (for profiling what the grass costs).
        public bool Enabled { get; set; } = true;
        // Off still generates but draws nothing (profiling).
        public bool DrawEnabled { get; set; } = true;
        public int TypeCount => _prototypes.Count;

        private struct GraphicsBufferIndirectArgs
        {
            public uint IndexCount, InstanceCount, StartIndex, BaseVertex, StartInstance;
        }

        public StrataGrassRenderer(StrataHeightfield heights, ComputeShader shader)
        {
            _heights = heights;
            _shader = shader;
            _reset = shader.FindKernel("GrassReset");
            _scatter = shader.FindKernel("GrassScatter");
            _finish = shader.FindKernel("GrassFinish");
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            StrataGrid grid = heights.Grid;
            float2 centre = grid.Origin + grid.Size * 0.5f;
            _brg.SetGlobalBounds(new Bounds(new Vector3(centre.x, (grid.MinHeight + grid.MaxHeight) * 0.5f, centre.y),
                                            new Vector3(grid.Size.x + 200f, grid.HeightRange + 200f, grid.Size.y + 200f)));
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        // The prototypes the density map's types are, in order, and the map itself.
        public void SetPrototypes(IReadOnlyList<StrataGrassPrototype> prototypes, StrataGrassMap map)
        {
            ReleaseCameras();
            foreach (BatchMaterialID id in _materialIds) _brg.UnregisterMaterial(id);
            foreach (BatchMeshID id in _meshIds) _brg.UnregisterMesh(id);
            _materialIds.Clear();
            _meshIds.Clear();
            _draws.Clear();
            _types?.Dispose();
            _visible?.Dispose();
            _types = null;
            _visible = null;
            _map = map;
            _prototypes = prototypes ?? Array.Empty<StrataGrassPrototype>();
            if (_prototypes.Count == 0 || _prototypes.Count > MaxTypes) return;

            float maxDensity = 0.01f;
            _range = 0f;
            foreach (StrataGrassPrototype p in _prototypes)
            {
                if (p == null) continue;
                maxDensity = math.max(maxDensity, p.Density);
                _range = math.max(_range, p.Range);
            }
            _spacing = 1f / math.sqrt(maxDensity);

            List<GraphicsBufferIndirectArgs> args = new();
            _typeData = new Vector4[_prototypes.Count * 3];
            _capacity = 0;
            for (int t = 0; t < _prototypes.Count; t++)
            {
                StrataGrassPrototype p = _prototypes[t];
                bool valid = p != null && p.IsValid;
                int submeshes = valid ? p.Mesh.subMeshCount : 0;
                // about half the circle of the prototype's range can be in view; the regions clamp anything more
                int capacity = valid ? (int)math.ceil(math.PI * p.Range * p.Range * p.Density * 0.5f) + 256 : 0;
                float radius = valid ? math.max(p.Mesh.bounds.extents.magnitude, 0.1f) : 0.1f;
                _typeData[t * 3] = valid ? new Vector4(p.Density, p.Range, p.Scale.x, p.Scale.y) : Vector4.zero;
                _typeData[t * 3 + 1] = new Vector4(valid ? p.AlignToGround : 0f, valid ? p.Lean : 0f, radius, args.Count);
                _typeData[t * 3 + 2] = new Vector4(submeshes, _capacity, capacity, 0f);
                if (valid)
                {
                    BatchMeshID mesh = _brg.RegisterMesh(p.Mesh);
                    _meshIds.Add(mesh);
                    for (int s = 0; s < submeshes; s++)
                    {
                        SubMeshDescriptor sub = p.Mesh.GetSubMesh(s);
                        BatchMaterialID material = _brg.RegisterMaterial(p.Materials[s]);
                        _materialIds.Add(material);
                        _draws.Add(new Draw { Mesh = mesh, Material = material, Submesh = (ushort)s, Entry = args.Count, Type = t });
                        args.Add(new GraphicsBufferIndirectArgs { IndexCount = (uint)sub.indexCount, StartIndex = (uint)sub.indexStart, BaseVertex = (uint)sub.baseVertex });
                    }
                }
                else args.Add(default);
                _capacity += capacity;
            }
            _argsTemplate = args.ToArray();
            _types = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _typeData.Length, 16);
            _types.SetData(_typeData);
            _visible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, math.max(1, _capacity), 4);
            NativeArray<int> identity = new(math.max(1, _capacity), Allocator.Temp);
            for (int i = 0; i < identity.Length; i++) identity[i] = i;
            _visible.SetData(identity);
            identity.Dispose();
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            _current = null;
            if (!Enabled || _types == null || _map == null || camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) return;
            if ((camera.cullingMask & (1 << Layer)) == 0) return;
            if (!_cameras.TryGetValue(camera, out CameraState state)) _cameras[camera] = state = CreateState();
            _current = state;

            StrataGrid grid = _heights.Grid;
            Vector3 position = camera.transform.position;
            int cells = (int)math.ceil(_range * 2f / _spacing) + 1;
            float2 first = math.floor((new float2(position.x, position.z) - _range) / _spacing);
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (int i = 0; i < 6; i++) _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);

            CommandBuffer cmd = CommandBufferPool.Get("Strata Grass");
            cmd.BeginSample("Strata Grass");
            cmd.SetComputeVectorParam(_shader, "_StrataGrid", new Vector4(grid.Origin.x, grid.Origin.y, grid.TileSize, grid.TilesX));
            cmd.SetComputeVectorParam(_shader, "_StrataHeightParams", new Vector4(grid.MinHeight, grid.HeightRange, grid.Resolution, grid.Spacing));
            cmd.SetComputeVectorParam(_shader, "_GrassGrid", new Vector4(grid.TilesZ, grid.SplatResolution, _prototypes.Count, _argsTemplate.Length));
            cmd.SetComputeVectorParam(_shader, "_CameraPosition", new Vector4(position.x, position.y, position.z, _range));
            cmd.SetComputeVectorArrayParam(_shader, "_FrustumPlanes", _planeVectors);
            cmd.SetComputeVectorParam(_shader, "_Candidates", new Vector4(first.x, first.y, _spacing, cells));
            cmd.SetComputeIntParam(_shader, "_Seed", 0x2f6b3a1);
            cmd.SetComputeBufferParam(_shader, _reset, "_Args", state.Args);
            cmd.DispatchCompute(_shader, _reset, (_argsTemplate.Length + 63) / 64, 1, 1);
            cmd.SetComputeTextureParam(_shader, _scatter, "_StrataHeights", _heights.Texture);
            cmd.SetComputeTextureParam(_shader, _scatter, "_StrataFarHeights", _heights.FarTexture);
            cmd.SetComputeBufferParam(_shader, _scatter, "_StrataTileSlots", _heights.TileSlotBuffer);
            cmd.SetComputeTextureParam(_shader, _scatter, "_StrataHoles", _heights.Holes.Texture);
            cmd.SetComputeBufferParam(_shader, _scatter, "_StrataHoleSlots", _heights.Holes.TileSlotBuffer);
            int2 far = StrataHeights.FarSamples(grid) - 1;
            cmd.SetComputeVectorParam(_shader, "_StrataFar", new Vector4(far.x, far.y, 0f, 0f));
            cmd.SetComputeTextureParam(_shader, _scatter, "_GrassMap", _map.Texture);
            cmd.SetComputeBufferParam(_shader, _scatter, "_Types", _types);
            cmd.SetComputeBufferParam(_shader, _scatter, "_Instances", state.Instances);
            cmd.SetComputeBufferParam(_shader, _scatter, "_Args", state.Args);
            cmd.DispatchCompute(_shader, _scatter, (cells + 7) / 8, (cells + 7) / 8, 1);
            cmd.SetComputeBufferParam(_shader, _finish, "_Types", _types);
            cmd.SetComputeBufferParam(_shader, _finish, "_Args", state.Args);
            cmd.DispatchCompute(_shader, _finish, 1, 1, 1);
            cmd.EndSample("Strata Grass");
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // drop the buffers of cameras that have been destroyed
            _gone.Clear();
            foreach (Camera key in _cameras.Keys)
                if (key == null) _gone.Add(key);
            foreach (Camera key in _gone)
            {
                Release(_cameras[key]);
                _cameras.Remove(key);
            }
        }

        // How many clumps of each prototype a camera drew last (read back from the GPU, so for debugging only).
        public int[] ReadCounts(Camera camera)
        {
            int[] counts = new int[_prototypes.Count];
            if (!_cameras.TryGetValue(camera, out CameraState state)) return counts;
            uint[] args = new uint[_argsTemplate.Length * 5];
            state.Args.GetData(args);
            for (int t = 0; t < counts.Length; t++) counts[t] = (int)args[(int)_typeData[t * 3 + 1].w * 5 + 1];
            return counts;
        }

        private CameraState CreateState()
        {
            CameraState state = new()
            {
                Instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, InstanceOffset / 4 + _capacity * 4, 4),
                Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, _argsTemplate.Length * 5, 4)
            };
            NativeArray<float4> header = new(InstanceOffset / 16, Allocator.Temp, NativeArrayOptions.ClearMemory);
            // unity_ObjectToWorld and unity_WorldToObject, shared by every instance: identity as packed 3x4 columns
            for (int m = 0; m < 2; m++)
            for (int i = 0; i < 3; i++)
                header[HeaderBytes / 16 + m * 3 + i] = new float4(1f, 0f, 0f, 0f);
            state.Instances.SetData(header, 0, 0, header.Length);
            header.Dispose();
            state.Args.SetData(_argsTemplate);

            NativeArray<MetadataValue> metadata = new(3, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = ObjectToWorldId, Value = HeaderBytes };
            metadata[1] = new MetadataValue { NameID = WorldToObjectId, Value = HeaderBytes + MatrixBytes };
            metadata[2] = new MetadataValue { NameID = TreeId, Value = 0x80000000 | InstanceOffset };
            state.Batch = _brg.AddBatch(metadata, state.Instances.bufferHandle);
            metadata.Dispose();
            return state;
        }

        private JobHandle OnPerformCulling(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr userContext)
        {
            if (!DrawEnabled || context.viewType != BatchCullingViewType.Camera || _current == null || _draws.Count == 0) return default;
            if ((context.cullingLayerMask & (1u << Layer)) == 0 || output.drawCommands.Length == 0) return default;
            BatchCullingOutputDrawCommands* commands = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            *commands = default;
            commands->indirectDrawCommands = Malloc<BatchDrawCommandIndirect>(_draws.Count);
            commands->drawRanges = Malloc<BatchDrawRange>(1);
            for (int i = 0; i < _draws.Count; i++)
            {
                Draw draw = _draws[i];
                commands->indirectDrawCommands[i] = new BatchDrawCommandIndirect
                {
                    flags = BatchDrawCommandFlags.None,
                    batchID = _current.Batch,
                    materialID = draw.Material,
                    splitVisibilityMask = 0xff,
                    sortingPosition = 0,
                    visibleOffset = (uint)_typeData[draw.Type * 3 + 2].y,
                    meshID = draw.Mesh,
                    topology = MeshTopology.Triangles,
                    visibleInstancesBufferHandle = _visible.bufferHandle,
                    // raw buffers take no window
                    visibleInstancesBufferWindowOffset = 0,
                    visibleInstancesBufferWindowSizeBytes = 0,
                    indirectArgsBufferHandle = _current.Args.bufferHandle,
                    indirectArgsBufferOffset = (uint)(draw.Entry * 20)
                };
            }
            commands->indirectDrawCommandCount = _draws.Count;
            commands->drawRangeCount = 1;
            commands->drawRanges[0] = new BatchDrawRange
            {
                drawCommandsType = BatchDrawCommandType.Indirect,
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)_draws.Count,
                filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = 1,
                    layer = (byte)Layer,
                    motionMode = MotionVectorGenerationMode.Camera,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = true
                }
            };
            return default;
        }

        private static T* Malloc<T>(int count) where T : unmanaged =>
            (T*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<T>() * count, UnsafeUtility.AlignOf<T>(), Allocator.TempJob);

        private void Release(CameraState state)
        {
            _brg.RemoveBatch(state.Batch);
            state.Instances.Dispose();
            state.Args.Dispose();
        }

        private void ReleaseCameras()
        {
            foreach (CameraState state in _cameras.Values) Release(state);
            _cameras.Clear();
            _current = null;
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseCameras();
            foreach (BatchMaterialID id in _materialIds) _brg.UnregisterMaterial(id);
            foreach (BatchMeshID id in _meshIds) _brg.UnregisterMesh(id);
            _brg.Dispose();
            _types?.Dispose();
            _visible?.Dispose();
        }
    }
}
