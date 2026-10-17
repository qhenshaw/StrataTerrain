# Changelog

## 0.6.0
- Milestone 6: streaming. Bakes write the tile files and a far file (the whole world's heights every 8th sample, splats
  every 4th texel, and every CDLOD node's bounds) to StreamingAssets/Strata/<world>/; the world data points at that
  folder (re-bake worlds baked as TextAssets). In play mode a world with more tiles than Resident Tiles streams: tiles
  within the streaming radius of any camera or focus are read with AsyncReadManager into pooled slots (heights, splats,
  strata, grass), the rest draw from the far copy at CDLOD level 3 and up, whose vertices fall on the far samples so
  near and far patches meet exactly. Collision waits for its tiles; trees stand on far heights until their tile
  arrives. The editor keeps every tile resident for editing.
- StrataHeights: a job-friendly view of heights (slot or far) used by collision, trees, placement and queries.
- StrataGround: height, ground point, normal and strongest terrain layer at a position.
- Shaders and the grass compute look tiles up through a tile-to-slot buffer.

## 0.5.0
- Milestone 5: grass. Grass prototypes (small meshes drawn with Strata/Tree materials, density per square metre,
  range, scale, ground alignment, lean) mixed by Meadow assets (rule like a forest's, weighted entries with their own
  ranges) and placed by the Grass Scatter feature (optionally replacing grass above it). Tree effects on every forest
  thin and clear grass too. Up to 8 grass prototypes per world; their density maps (bytes at splat resolution) are
  evaluated straight into a GPU array while editing and baked into the tile files (version 5).
- Grass is generated on the GPU for each camera every frame (StrataGrass.compute: world-anchored jittered candidates,
  density-weighted prototype choice, distance thinning, frustum culling) and drawn with BatchRendererGroup indirect
  draws using the tree shader's instance format; nothing per clump is stored. No grass shadows.
- Tools > Strata > Generate Example Grass makes low-poly test clumps and flowers.

## 0.4.0
- Milestone 4: trees. Tree prototypes built from LODGroup prefabs (Strata/Tree materials converted from the source,
  trunk capsule, LOD distances) with baked hemi-octahedral impostors (Strata/TreeImpostor). Forest assets (rule by
  height, slope, moisture, terrain layers and noise; weighted species with spacing, variation and their own rules),
  placed by the Tree Scatter feature (optionally replacing forests above it, painting a forest floor by density).
  Every feature has a tree effect: scale or clear density where it acts, or plant along a spline.
- Placement: forest densities per tile on the GPU, then Burst placement on the CPU with per-species jittered
  candidates, priority Poisson rejection and cross-species spacing; deterministic and tile-independent. Live preview
  re-places the changed tiles and their neighbours; trees never stand below sea level.
- Trees are baked into the tile files (version 4, 10 bytes a tree) and drawn through one BatchRendererGroup batch at
  16 bytes of GPU memory a tree, with Burst culling by 64 m cells, per-tree LOD with packed dithered crossfade, and
  impostors (shadows in the last cascade only). Trunk capsules stream with the collision chunks.
- StrataFocus component: any object with it gets terrain collision streamed around it.
- Fixed: the terrain got no ambient light under BatchRendererGroup (the fragment stage now sets up the instance).

## 0.3.0
- Milestone 3: rock strata. Rock Strata feature (terraced ledges in the heights, strata map and definitions in the
  bake, tile files version 3), vertex-pushed bands on half-metre CDLOD detail patches, analytic band normals, per-bed
  tint, bed-aligned textures and ledge painting.
- Culling runs inline in the BatchRendererGroup callback (a returned job could outlive the view's output right after
  the group was created).
- The renderer rebinds its material before each camera, since a shader reimport drops undeclared textures and buffers.
- Debug views for projection weights and strata strength.

## 0.2.0
- Milestone 2: texturing. Layers and palettes, Auto Splat rules with a world-wide moisture map, painting on every
  feature, top-4 index/weight splats in tile files (version 2), and a PBR surface shader with height blending,
  triplanar, far tiling, macro variation and stochastic sampling.
- Hydraulic erosion drains water over its footprint's edges instead of pooling against them.

## 0.1.0
- Milestone 1: heights end-to-end (authoring, live preview, bake, CDLOD rendering through BRG, streamed collision).
