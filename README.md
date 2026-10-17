# Strata Terrain

Non-destructive tiled terrain for Unity 6 URP (PC, DX12/Vulkan).

- Heights come from scene objects under a `StrataWorld`, evaluated in hierarchy order: texture stamps, noise stamps,
  splines (road, river, ridge) and filters (smooth, terrace, thermal and hydraulic erosion), with stamp groups.
- Texturing: `StrataLayer` assets (albedo, normal, mask map, tiling, per-layer rule) listed in a `StrataPalette`. The
  **Auto Splat** feature paints the layers by their rules (height, slope, curvature, moisture from flow and water,
  noise); any feature can also paint a layer where it acts. Splats keep the four strongest layers per texel at 1, 2
  or 4 m. The surface shader height-blends them with triplanar mapping on steep ground, far-distance tiling, macro
  variation and optional hex-tiled stochastic sampling, under URP PBR lighting.
- Rock strata: the **Rock Strata** feature terraces steep ground into ledges along jittered, dipping rock beds (baked
  into the heights, so collision follows), and marks it for the surface shader, which pushes each bed's face out
  (on extra half-metre detail patches near the camera, where the slope still leaves the mesh room for it), shades
  the beds out to a longer range, tints each bed and lines textures up with the beds. Ledge tops can paint a layer.
- Trees: `StrataTreePrototype` assets are made from LODGroup prefabs (**Assets > Strata > Create Tree Prototypes**):
  their materials become `Strata/Tree` copies, LOD distances come from the LODGroup (the last mesh LOD reaches
  150 m), a CapsuleCollider on the prefab becomes the trunk capsule, and **Bake Impostor** renders the hemi-octahedral
  impostor drawn to 2 km. `StrataForest` assets mix weighted species (spacing, scale, lean, ground alignment, sink and
  their own height/slope/moisture ranges) under a forest rule (height, slope, moisture, terrain layers to grow on or
  avoid, noise for stands and clearings). The **Tree Scatter** feature places a forest over the world or a footprint
  (Replace swaps out the forests above it; its paint lays a forest floor by density), and every feature's tree effect
  can thin or clear trees where it acts (roads, rivers) or plant a line along a spline.
- Grass: `StrataGrassPrototype` assets (a small mesh with a `Strata/Tree` material, clumps per square metre, range,
  variation) mixed by `StrataMeadow` assets and placed by the **Grass Scatter** feature; tree effects on every forest
  clear grass too. Density maps (up to 8 prototypes) are baked; the clumps themselves are generated on the GPU around
  each camera every frame and drawn with indirect draws. Assign `StrataGrass.compute` on the world for builds.
- Edits preview live in the editor; **Bake** writes one compact `.bytes` file per tile (heights, splats, trees and
  grass densities) and a far file to `StreamingAssets/Strata/<world>/`.
- Streaming (play mode): tiles near cameras and focus transforms are loaded asynchronously into a fixed number of
  slots; everything else is drawn from the resident far copy (heights every 8 m, splats every 8 m at the default
  spacing). Trees are all resident (16 bytes each on the GPU); grass and collision exist only on resident tiles.
- `StrataGround` answers height, normal and terrain-layer queries for gameplay code.
- The surface, trees and grass are drawn through `BatchRendererGroup`: GPU-displaced CDLOD patches, and trees with
  per-tree LOD crossfade and impostors at 16 bytes of GPU memory a tree.
- Terrain mesh collision and tree trunk capsules are streamed in chunks around focus transforms (`RegisterFocus`, or
  a `StrataFocus` component).

Namespace: `Strata`.
