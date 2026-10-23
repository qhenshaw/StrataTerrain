using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Strata
{
    // Streams NavMesh around the world's collision foci (its registered foci and every StrataFocus) in play mode, built
    // from the streamed terrain collision chunks and tree colliders, plus other colliders on Extra Layers so props
    // shape it too (anything moving, a NavMeshAgent or a focus is skipped, so the player and creatures don't carve
    // holes). Each focus gets a box Radius wide on each side, with one NavMeshData per agent type in Agent Types, so
    // agents of every type find their own surface. A box is rebuilt around its focus once the focus has moved Rebuild
    // Distance from where it was last built, or when a collision chunk inside it comes or goes; builds run
    // asynchronously, one focus and agent type at a time, using the engine's NavMeshBuilder (no AI Navigation package).
    [DisallowMultipleComponent, RequireComponent(typeof(StrataWorld))]
    public class StrataNavMesh : MonoBehaviour
    {
        [Serializable]
        public class AgentBuild
        {
            [Tooltip("The NavMesh agent type (Project Settings > Navigation) to build for.")]
            public int AgentTypeID;
            [Tooltip("The area the built surface is marked as (0 is Walkable).")]
            public int Area;
            [Tooltip("Voxel size override in metres; 0 uses the agent type's.")]
            [Min(0f)] public float VoxelSize;
            [Tooltip("Tile size override in voxels; 0 uses the agent type's.")]
            [Min(0)] public int TileSize;
        }

        [SerializeField] private List<AgentBuild> _agentTypes = new() { new AgentBuild() };
        [Tooltip("Metres the NavMesh reaches from each focus; at most the world's collision radius, beyond which there is no collision.")]
        [SerializeField, Min(1f)] private float _radius = 96f;
        [Tooltip("Metres a focus moves before its NavMesh is rebuilt around it.")]
        [SerializeField, Min(0.5f)] private float _rebuildDistance = 24f;
        [Tooltip("Shortest time in seconds between rebuilds of one focus's NavMesh.")]
        [SerializeField, Min(0f)] private float _minRebuildInterval = 0.5f;
        [Tooltip("Other colliders that shape the NavMesh, such as rocks and buildings. Moving bodies, agents and foci are skipped.")]
        [SerializeField] private LayerMask _extraLayers = 1;

        private const float HeightMargin = 20f;

        private class Volume
        {
            public Transform Focus;
            public Vector3 Centre;
            public bool Built;
            public bool Dirty = true;
            public bool Removed;
            public float LastBuild = float.NegativeInfinity;
            public NavMeshData[] Data;
            public NavMeshDataInstance[] Instances;
            public readonly List<NavMeshBuildSource> Sources = new();
        }

        private static readonly List<NavMeshBuildMarkup> s_noMarkups = new();

        private readonly List<Volume> _volumes = new();
        private readonly List<NavMeshBuildSource> _scratch = new();
        private readonly List<Collider> _colliders = new();
        private readonly HashSet<Transform> _foci = new();
        private StrataWorld _world;
        private StrataCollision _subscribed;
        private StrataCaveColliders _subscribedCaves;
        private Volume _building;
        private int _buildingAgent;
        private bool _dirtiedDuringBuild;
        private AsyncOperation _operation;

        public IReadOnlyList<AgentBuild> AgentTypes => _agentTypes;

        private void OnEnable()
        {
            _world = GetComponent<StrataWorld>();
            _world.Rebuilt += Clear;
        }

        private void OnDisable()
        {
            if (_world != null) _world.Rebuilt -= Clear;
            Clear();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            StrataCollision collision = _world.Collision;
            if (collision != _subscribed || _world.CaveColliders != _subscribedCaves) Subscribe(collision);
            if (collision == null) return;

            SyncVolumes();
            foreach (Volume volume in _volumes)
                if (volume.Built && !volume.Dirty && volume.Focus != null && Flat(volume.Focus.position - volume.Centre).sqrMagnitude > _rebuildDistance * _rebuildDistance)
                    volume.Dirty = true;

            if (_operation != null)
            {
                if (!_operation.isDone) return;
                _operation = null;
                if (!_building.Removed && ++_buildingAgent < _agentTypes.Count)
                {
                    BuildAgent();
                    return;
                }
                Finish();
            }
            StartNext();
        }

        // a volume per current focus; one whose focus has gone is dropped (after its build, if one is running)
        private void SyncVolumes()
        {
            _foci.Clear();
            foreach (Transform focus in _world.Foci)
                if (focus != null) _foci.Add(focus);
            foreach (Transform focus in StrataFocus.Active)
                if (focus != null) _foci.Add(focus);
            for (int i = _volumes.Count - 1; i >= 0; i--)
            {
                Volume volume = _volumes[i];
                if (volume.Focus != null && _foci.Contains(volume.Focus)) continue;
                volume.Removed = true;
                if (volume != _building) Release(volume);
                _volumes.RemoveAt(i);
            }
            foreach (Transform focus in _foci)
                if (_volumes.FindIndex(v => v.Focus == focus) < 0)
                    _volumes.Add(new() { Focus = focus, Data = new NavMeshData[_agentTypes.Count], Instances = new NavMeshDataInstance[_agentTypes.Count] });
        }

        // the next dirty volume that hasn't been rebuilt too recently, unbuilt ones first
        private void StartNext()
        {
            Volume next = null;
            foreach (Volume volume in _volumes)
            {
                if (!volume.Dirty || Time.time < volume.LastBuild + _minRebuildInterval) continue;
                if (next == null || !volume.Built && next.Built) next = volume;
            }
            if (next == null || _agentTypes.Count == 0) return;
            _building = next;
            _buildingAgent = 0;
            _dirtiedDuringBuild = false;
            next.Dirty = false;
            next.Centre = next.Focus.position;
            CollectSources(next);
            BuildAgent();
        }

        private void BuildAgent()
        {
            AgentBuild agent = _agentTypes[_buildingAgent];
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(agent.AgentTypeID);
            if (settings.agentTypeID == -1)
            {
                Debug.LogWarning($"{name}: StrataNavMesh agent type {agent.AgentTypeID} doesn't exist; skipped.", this);
                _operation = null;
                Finish();
                return;
            }
            if (agent.VoxelSize > 0f)
            {
                settings.overrideVoxelSize = true;
                settings.voxelSize = agent.VoxelSize;
            }
            if (agent.TileSize > 0)
            {
                settings.overrideTileSize = true;
                settings.tileSize = agent.TileSize;
            }

            Volume volume = _building;
            if (volume.Data.Length != _agentTypes.Count) Resize(volume);
            if (volume.Data[_buildingAgent] == null)
            {
                volume.Data[_buildingAgent] = new NavMeshData(agent.AgentTypeID) { name = $"{name} NavMesh" };
                volume.Instances[_buildingAgent] = NavMesh.AddNavMeshData(volume.Data[_buildingAgent]);
            }
            // each build gets its own copy, marked with this agent type's area
            List<NavMeshBuildSource> sources = new(volume.Sources.Count);
            foreach (NavMeshBuildSource source in volume.Sources)
            {
                NavMeshBuildSource marked = source;
                marked.area = agent.Area;
                sources.Add(marked);
            }
            _operation = NavMeshBuilder.UpdateNavMeshDataAsync(volume.Data[_buildingAgent], settings, sources, Bounds(volume.Centre));
        }

        private void Finish()
        {
            Volume volume = _building;
            _building = null;
            if (volume == null) return;
            if (volume.Removed)
            {
                Release(volume);
                return;
            }
            volume.Built = true;
            volume.LastBuild = Time.time;
            if (_dirtiedDuringBuild) volume.Dirty = true;
        }

        private void CollectSources(Volume volume)
        {
            Bounds bounds = Bounds(volume.Centre);
            volume.Sources.Clear();
            Transform terrain = _world.Collision.Root;
            Transform trees = _world.TreeColliders?.Root;
            Transform caves = _world.CaveColliders?.Root;
            AddStrata(volume, terrain, bounds);
            AddStrata(volume, trees, bounds);
            AddStrata(volume, caves, bounds);
            if (_extraLayers.value == 0) return;
            _scratch.Clear();
            NavMeshBuilder.CollectSources(bounds, _extraLayers, NavMeshCollectGeometry.PhysicsColliders, 0, s_noMarkups, _scratch);
            foreach (NavMeshBuildSource source in _scratch)
            {
                if (source.component is not Collider collider) continue;
                if (collider.isTrigger || (terrain != null && collider.transform.IsChildOf(terrain)) || (trees != null && collider.transform.IsChildOf(trees)) ||
                    (caves != null && collider.transform.IsChildOf(caves))) continue;
                if (collider.attachedRigidbody != null && !collider.attachedRigidbody.isKinematic) continue;
                if (collider.GetComponentInParent<NavMeshAgent>() != null || collider.GetComponentInParent<StrataFocus>() != null) continue;
                volume.Sources.Add(source);
            }
        }

        // Strata's own colliders, read directly (their objects are hidden from saving, which source collection may skip)
        private void AddStrata(Volume volume, Transform root, Bounds bounds)
        {
            if (root == null) return;
            root.GetComponentsInChildren(false, _colliders);
            foreach (Collider collider in _colliders)
                if (collider.enabled && collider.bounds.Intersects(bounds) && TrySource(collider, out NavMeshBuildSource source))
                    volume.Sources.Add(source);
        }

        private static bool TrySource(Collider collider, out NavMeshBuildSource source)
        {
            Transform t = collider.transform;
            source = new NavMeshBuildSource { component = collider };
            switch (collider)
            {
                case MeshCollider mesh when mesh.sharedMesh != null:
                    source.shape = NavMeshBuildSourceShape.Mesh;
                    source.sourceObject = mesh.sharedMesh;
                    source.transform = t.localToWorldMatrix;
                    return true;
                case CapsuleCollider capsule:
                    // NavMesh capsules stand on Y; turn X- or Z-aligned ones onto it
                    Quaternion turn = capsule.direction == 0 ? Quaternion.Euler(0f, 0f, 90f) : capsule.direction == 2 ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                    source.shape = NavMeshBuildSourceShape.Capsule;
                    source.size = new Vector3(capsule.radius * 2f, capsule.height, capsule.radius * 2f);
                    source.transform = t.localToWorldMatrix * Matrix4x4.TRS(capsule.center, turn, Vector3.one);
                    return true;
                case BoxCollider box:
                    source.shape = NavMeshBuildSourceShape.Box;
                    source.size = box.size;
                    source.transform = t.localToWorldMatrix * Matrix4x4.Translate(box.center);
                    return true;
                case SphereCollider sphere:
                    source.shape = NavMeshBuildSourceShape.Sphere;
                    source.size = Vector3.one * sphere.radius * 2f;
                    source.transform = t.localToWorldMatrix * Matrix4x4.Translate(sphere.center);
                    return true;
                default:
                    return false;
            }
        }

        private void OnChunkChanged(Unity.Mathematics.int2 chunk, Bounds chunkBounds)
        {
            foreach (Volume volume in _volumes)
            {
                if (volume.Focus == null || !Bounds(volume.Built ? volume.Centre : volume.Focus.position).Intersects(chunkBounds)) continue;
                if (volume == _building) _dirtiedDuringBuild = true;
                else volume.Dirty = true;
            }
        }

        private Bounds Bounds(Vector3 centre)
        {
            float radius = Mathf.Min(_radius, _world.Settings.CollisionRadius);
            StrataGrid grid = _world.Heightfield.Grid;
            return new Bounds(new Vector3(centre.x, (grid.MinHeight + grid.MaxHeight) * 0.5f, centre.z),
                              new Vector3(radius * 2f, grid.HeightRange + HeightMargin * 2f, radius * 2f));
        }

        private void Subscribe(StrataCollision collision)
        {
            if (_subscribed != null)
            {
                _subscribed.ChunkActivated -= OnChunkChanged;
                _subscribed.ChunkDeactivated -= OnChunkChanged;
            }
            if (_subscribedCaves != null) _subscribedCaves.Changed -= OnCavesChanged;
            _subscribed = collision;
            _subscribedCaves = collision != null ? _world.CaveColliders : null;
            if (_subscribedCaves != null) _subscribedCaves.Changed += OnCavesChanged;
            if (collision == null) return;
            collision.ChunkActivated += OnChunkChanged;
            collision.ChunkDeactivated += OnChunkChanged;
        }

        private void OnCavesChanged(Bounds bounds) => OnChunkChanged(default, bounds);

        // the agent list changed while playing: start that volume's data over
        private void Resize(Volume volume)
        {
            Release(volume);
            volume.Data = new NavMeshData[_agentTypes.Count];
            volume.Instances = new NavMeshDataInstance[_agentTypes.Count];
        }

        private static void Release(Volume volume)
        {
            for (int i = 0; i < volume.Instances.Length; i++)
            {
                if (volume.Instances[i].valid) NavMesh.RemoveNavMeshData(volume.Instances[i]);
                volume.Instances[i] = default;
                volume.Data[i] = null;
            }
        }

        private void Clear()
        {
            Subscribe(null);
            foreach (Volume volume in _volumes) Release(volume);
            if (_building != null && !_volumes.Contains(_building)) Release(_building);
            _volumes.Clear();
            _building = null;
            _operation = null;
        }

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        private void OnDrawGizmosSelected()
        {
            if (_world == null || _world.Heightfield == null) return;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
            foreach (Volume volume in _volumes)
            {
                if (volume.Focus == null && !volume.Built) continue;
                Bounds bounds = Bounds(volume.Built ? volume.Centre : volume.Focus.position);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }
        }
    }
}
