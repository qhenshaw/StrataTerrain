using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // Streams terrain collision as MeshCollider chunks around a set of focus transforms (players, NPC crowds, the
    // camera). Each chunk is a full-resolution grid built from the CPU heights in a Burst job and baked for PhysX on
    // worker threads, without the triangles over cave holes (StrataCaveColliders covers the caves); colliders are pooled. ChunkActivated fires once a chunk's collider is assigned and live, so a
    // NavMesh builder that collects physics colliders can rebuild over it; ChunkDeactivated fires before it goes.
    // The meshes stay CPU-readable, which NavMesh source collection needs.
    public sealed class StrataCollision : IDisposable
    {
        private const int MaxBuildsPerUpdate = 8;
        // the bake and the collider must agree, or PhysX cooks the mesh again on assignment
        private const MeshColliderCookingOptions CookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                                                                  MeshColliderCookingOptions.EnableMeshCleaning |
                                                                  MeshColliderCookingOptions.WeldColocatedVertices |
                                                                  MeshColliderCookingOptions.UseFastMidphase;

        private readonly StrataHeightfield _heights;
        private readonly Transform _root;
        private readonly int _chunkQuads;
        private readonly int _layer;
        private readonly float _radius;
        private readonly int2 _chunkCount;
        private readonly Dictionary<int2, MeshCollider> _active = new();
        private readonly Stack<MeshCollider> _pool = new();
        private readonly HashSet<int2> _wanted = new();
        private readonly List<int2> _missing = new();
        private readonly List<int2> _leaving = new();

        public event Action<int2, Bounds> ChunkActivated;
        public event Action<int2, Bounds> ChunkDeactivated;

        public int ActiveCount => _active.Count;
        // every chunk collider lives under this, for gathering them (NavMesh source collection)
        public Transform Root => _root;
        public float ChunkSize => _chunkQuads * _heights.Grid.Spacing;

        public StrataCollision(StrataHeightfield heights, Transform parent, float chunkSize, float radius, int layer)
        {
            _heights = heights;
            StrataGrid grid = heights.Grid;
            _chunkQuads = math.max(1, (int)math.round(chunkSize / grid.Spacing));
            _radius = radius;
            _layer = layer;
            _chunkCount = (new int2(grid.WorldSamplesX, grid.WorldSamplesZ) - 1 + _chunkQuads - 1) / _chunkQuads;
            _root = new GameObject("Strata Collision") { hideFlags = HideFlags.DontSave }.transform;
            _root.SetParent(parent, false);
        }

        public bool IsActive(int2 chunk) => _active.ContainsKey(chunk);

        public Bounds ChunkBounds(int2 chunk)
        {
            StrataGrid grid = _heights.Grid;
            int2 first = chunk * _chunkQuads;
            int2 last = math.min(first + _chunkQuads, new int2(grid.WorldSamplesX, grid.WorldSamplesZ) - 1);
            float2 min = grid.SamplePosition(first), max = grid.SamplePosition(last);
            return new Bounds(new Vector3((min.x + max.x) * 0.5f, (grid.MinHeight + grid.MaxHeight) * 0.5f, (min.y + max.y) * 0.5f),
                              new Vector3(max.x - min.x, grid.HeightRange, max.y - min.y));
        }

        // Activates chunks within the radius of any focus (nearest first, a few per call) and drops the rest.
        public void Update(IReadOnlyList<Transform> foci)
        {
            StrataGrid grid = _heights.Grid;
            float chunkSize = ChunkSize;
            _wanted.Clear();
            foreach (Transform focus in foci)
            {
                if (focus == null) continue;
                float2 position = ((float3)focus.position).xz - grid.Origin;
                int2 min = math.max(0, (int2)math.floor((position - _radius) / chunkSize));
                int2 max = math.min(_chunkCount - 1, (int2)math.floor((position + _radius) / chunkSize));
                for (int z = min.y; z <= max.y; z++)
                for (int x = min.x; x <= max.x; x++)
                {
                    float2 closest = math.clamp(position, new float2(x, z) * chunkSize, new float2(x + 1, z + 1) * chunkSize);
                    // only once the full heights under the chunk have streamed in
                    if (math.distancesq(closest, position) <= _radius * _radius && Resident(new int2(x, z))) _wanted.Add(new int2(x, z));
                }
            }

            _leaving.Clear();
            foreach (int2 chunk in _active.Keys)
                if (!_wanted.Contains(chunk)) _leaving.Add(chunk);
            foreach (int2 chunk in _leaving) Deactivate(chunk);

            _missing.Clear();
            foreach (int2 chunk in _wanted)
                if (!_active.ContainsKey(chunk)) _missing.Add(chunk);
            if (_missing.Count == 0) return;
            float2 anchor = foci.Count > 0 && foci[0] != null ? ((float3)foci[0].position).xz - grid.Origin : float2.zero;
            _missing.Sort((a, b) => math.distancesq(((float2)a + 0.5f) * chunkSize, anchor).CompareTo(math.distancesq(((float2)b + 0.5f) * chunkSize, anchor)));
            Build(_missing.GetRange(0, math.min(_missing.Count, MaxBuildsPerUpdate)));
        }

        private bool Resident(int2 chunk)
        {
            StrataHeights view = _heights.View;
            int2 first = chunk * _chunkQuads;
            int2 last = first + _chunkQuads;
            return view.IsResident(first) && view.IsResident(last) && view.IsResident(new int2(first.x, last.y)) && view.IsResident(new int2(last.x, first.y));
        }

        // Rebuilds every active chunk, e.g. after the heights changed.
        public void RebuildAll()
        {
            List<int2> chunks = new(_active.Keys);
            foreach (int2 chunk in chunks) Deactivate(chunk);
            for (int i = 0; i < chunks.Count; i += MaxBuildsPerUpdate)
                Build(chunks.GetRange(i, math.min(MaxBuildsPerUpdate, chunks.Count - i)));
        }

        private void Build(List<int2> chunks)
        {
            StrataGrid grid = _heights.Grid;
            Mesh.MeshDataArray meshData = Mesh.AllocateWritableMeshData(chunks.Count);
            NativeArray<JobHandle> fills = new(chunks.Count, Allocator.Temp);
            MeshCollider[] colliders = new MeshCollider[chunks.Count];
            for (int i = 0; i < chunks.Count; i++)
            {
                int2 first = chunks[i] * _chunkQuads;
                int2 quads = math.min(_chunkQuads, new int2(grid.WorldSamplesX, grid.WorldSamplesZ) - 1 - first);
                Mesh.MeshData mesh = meshData[i];
                mesh.SetVertexBufferParams((quads.x + 1) * (quads.y + 1), new VertexAttributeDescriptor(VertexAttribute.Position));
                mesh.SetIndexBufferParams(quads.x * quads.y * 6, IndexFormat.UInt32);
                fills[i] = new FillJob { Heights = _heights.View, First = first, Quads = quads, Mesh = mesh }.Schedule(_heights.Readers);
                colliders[i] = Take(chunks[i]);
            }
            JobHandle.CompleteAll(fills);
            fills.Dispose();

            Mesh[] meshes = new Mesh[chunks.Count];
            NativeArray<EntityId> ids = new(chunks.Count, Allocator.TempJob);
            for (int i = 0; i < chunks.Count; i++)
            {
                Mesh.MeshData mesh = meshData[i];
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, mesh.GetIndexData<int>().Length), MeshUpdateFlags.DontRecalculateBounds);
                meshes[i] = colliders[i].sharedMesh != null ? colliders[i].sharedMesh : new Mesh { name = "Strata Collision", hideFlags = HideFlags.DontSave };
            }
            Mesh.ApplyAndDisposeWritableMeshData(meshData, meshes);
            for (int i = 0; i < chunks.Count; i++)
            {
                meshes[i].RecalculateBounds();
                ids[i] = meshes[i].GetEntityId();
            }
            new BakeJob { Meshes = ids }.Schedule(ids.Length, 1).Complete();
            ids.Dispose();

            for (int i = 0; i < chunks.Count; i++)
            {
                MeshCollider collider = colliders[i];
                // activate first: a pooled collider given its previous mesh again while inactive comes back with none
                collider.gameObject.SetActive(true);
                collider.sharedMesh = null;
                collider.sharedMesh = meshes[i];
                _active[chunks[i]] = collider;
                ChunkActivated?.Invoke(chunks[i], ChunkBounds(chunks[i]));
            }
        }

        private MeshCollider Take(int2 chunk)
        {
            MeshCollider collider = _pool.Count > 0 ? _pool.Pop() : null;
            if (collider == null)
            {
                GameObject go = new("Chunk") { hideFlags = HideFlags.DontSave, layer = _layer };
                go.SetActive(false);
                go.transform.SetParent(_root, false);
                collider = go.AddComponent<MeshCollider>();
                collider.cookingOptions = CookingOptions;
            }
            collider.gameObject.name = $"Chunk {chunk.x},{chunk.y}";
            float2 origin = _heights.Grid.SamplePosition(chunk * _chunkQuads);
            collider.transform.SetPositionAndRotation(new Vector3(origin.x, 0f, origin.y), Quaternion.identity);
            return collider;
        }

        private void Deactivate(int2 chunk)
        {
            if (!_active.Remove(chunk, out MeshCollider collider)) return;
            ChunkDeactivated?.Invoke(chunk, ChunkBounds(chunk));
            collider.gameObject.SetActive(false);
            _pool.Push(collider);
        }

        public void Dispose()
        {
            foreach (MeshCollider collider in _active.Values) Destroy(collider);
            foreach (MeshCollider collider in _pool) Destroy(collider);
            _active.Clear();
            _pool.Clear();
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        private static void Destroy(MeshCollider collider)
        {
            if (collider == null) return;
            if (collider.sharedMesh != null) UnityEngine.Object.Destroy(collider.sharedMesh);
            UnityEngine.Object.Destroy(collider.gameObject);
        }

        // Writes one chunk's vertices (relative to its first sample) and triangles.
        [BurstCompile]
        private struct FillJob : IJob
        {
            public StrataHeights Heights;
            public int2 First;
            public int2 Quads;
            public Mesh.MeshData Mesh;

            private bool Solid(int2 sample) => Heights.Presence(sample) >= 0.5f;

            public void Execute()
            {
                NativeArray<float3> vertices = Mesh.GetVertexData<float3>();
                NativeArray<int> indices = Mesh.GetIndexData<int>();
                int across = Quads.x + 1;
                for (int z = 0; z <= Quads.y; z++)
                for (int x = 0; x <= Quads.x; x++)
                {
                    int2 sample = First + new int2(x, z);
                    vertices[z * across + x] = new float3(x * Heights.Grid.Spacing, Heights.At(sample), z * Heights.Grid.Spacing);
                }
                // a triangle touching a hole sample is left degenerate, which cooking (mesh cleaning) removes
                int n = 0;
                for (int z = 0; z < Quads.y; z++)
                for (int x = 0; x < Quads.x; x++)
                {
                    int i = z * across + x;
                    int2 sample = First + new int2(x, z);
                    bool a = Solid(sample), b = Solid(sample + new int2(1, 0)), c = Solid(sample + new int2(0, 1)), d = Solid(sample + new int2(1, 1));
                    if (a && c && b) { indices[n++] = i; indices[n++] = i + across; indices[n++] = i + 1; }
                    else { indices[n++] = i; indices[n++] = i; indices[n++] = i; }
                    if (b && c && d) { indices[n++] = i + 1; indices[n++] = i + across; indices[n++] = i + across + 1; }
                    else { indices[n++] = i; indices[n++] = i; indices[n++] = i; }
                }
            }
        }

        // not Burst: BakeMesh is a managed call, but it is thread-safe, so chunks still cook in parallel
        private struct BakeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<EntityId> Meshes;

            public void Execute(int index) => Physics.BakeMesh(Meshes[index], false, CookingOptions);
        }
    }
}
