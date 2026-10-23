using System;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace Strata
{
    // What a bake produced, as a block on the StrataWorld that Bake fills in: the grid the tiles were baked with, the
    // folder under StreamingAssets holding one .bytes file per tile (heights, splats, trees, grass, and any cave holes
    // and meshes) and the far file (the whole world coarsely, and every CDLOD node's height bounds), the definitions and
    // prototypes the files refer to by index, and the cave voxel size and how many tiles have holes. The files stay out of the asset database so a streaming world can read just the tiles it needs.
    [Serializable]
    public class StrataWorldData
    {
        [SerializeField] private StrataGrid _grid;
        [Tooltip("Folder under StreamingAssets holding the tile files.")]
        [SerializeField] private string _folder = "";
        [SerializeField] private StrataBandDefinition[] _strata = new StrataBandDefinition[0];
        [SerializeField] private StrataTreePrototype[] _treePrototypes = new StrataTreePrototype[0];
        [SerializeField] private StrataGrassPrototype[] _grassPrototypes = new StrataGrassPrototype[0];
        [SerializeField] private float _caveVoxelSize;
        [SerializeField] private int _holedTiles;

        public StrataGrid Grid => _grid;
        public string Folder => _folder;
        // The rock strata the tiles' strata maps refer to by index.
        public StrataBandDefinition[] Strata => _strata;
        // The tree prototypes the tiles' tree records refer to by index.
        public StrataTreePrototype[] TreePrototypes => _treePrototypes ?? new StrataTreePrototype[0];
        // The grass prototypes the tiles' grass density maps are for, in order.
        public StrataGrassPrototype[] GrassPrototypes => _grassPrototypes ?? new StrataGrassPrototype[0];
        public float CaveVoxelSize => _caveVoxelSize;
        // How many tiles have holes cut by caves, which sizes a streamed world's hole slots.
        public int HoledTiles => _holedTiles;
        public bool IsComplete => _grid.IsValid && !string.IsNullOrEmpty(_folder) && File.Exists(FarPath);

        public static string FolderPath(string folder) => Path.Combine(Application.streamingAssetsPath, folder);
        public string TilePath(int tile) => TilePath(_folder, tile % _grid.TilesX, tile / _grid.TilesX);
        public static string TilePath(string folder, int tileX, int tileZ) => Path.Combine(FolderPath(folder), $"tile_{tileX}_{tileZ}.bytes");
        public string FarPath => Path.Combine(FolderPath(_folder), StrataFarFile.Name);

        // A tile file's bytes, read now, in a new array the caller disposes.
        public NativeArray<byte> ReadTile(int tile, Allocator allocator) => new(File.ReadAllBytes(TilePath(tile)), allocator);

        public void Set(StrataGrid grid, string folder, StrataBandDefinition[] strata, StrataTreePrototype[] treePrototypes,
                        StrataGrassPrototype[] grassPrototypes, float caveVoxelSize, int holedTiles)
        {
            _caveVoxelSize = caveVoxelSize;
            _holedTiles = holedTiles;
            _grid = grid;
            _folder = folder;
            _strata = strata;
            _treePrototypes = treePrototypes;
            _grassPrototypes = grassPrototypes;
        }
    }
}
