# Strata Terrain

Non-destructive tiled terrain for Unity 6 URP (PC, DX12/Vulkan).

- Heights come from scene objects under a `StrataWorld`, evaluated in hierarchy order: texture stamps, noise stamps,
  splines (road, river, ridge) and filters (smooth, terrace, thermal and hydraulic erosion), with stamp groups.
- Everything is configured in the scene: each feature keeps its settings on its component, and the `StrataWorld` has
  Settings (grid, heights, texturing, streaming, collision, probes), Palette and Baked Data blocks. Only prototypes and
  layers are assets.
- Texturing: `StrataLayer` assets (albedo, normal, mask map, tiling, per-layer rule) listed in the world's palette. The
  **Auto Splat** feature paints the layers by their rules (height, slope, curvature, moisture from flow and water,
  noise; the moisture inputs are world settings); any feature can also paint a layer where it acts. Splats keep the four strongest layers per texel at 1, 2
  or 4 m. The surface shader height-blends them with triplanar mapping on steep ground, far-distance tiling, macro
  variation and optional hex-tiled stochastic sampling, under URP PBR lighting (forward, or written into the GBuffer
  under deferred rendering).
- Rock strata: the **Rock Strata** feature terraces steep ground into ledges along jittered, dipping rock beds (baked
  into the heights, so collision follows), and marks it for the surface shader, which pushes each bed's face out
  (on extra half-metre detail patches near the camera, where the slope still leaves the mesh room for it), shades
  the beds out to a longer range, tints each bed and lines textures up with the beds. Ledge tops can paint a layer.
- Trees: `StrataTreePrototype` assets are made from LODGroup prefabs (**Assets > Strata > Create Tree Prototypes**):
  their materials become `Strata/Tree` copies, LOD distances come from the LODGroup (the last mesh LOD reaches
  150 m), a CapsuleCollider on the prefab becomes the trunk capsule, and **Bake Impostor** renders the hemi-octahedral
  impostor drawn to 2 km. A **Tree Scatter** is a forest: weighted species (spacing, scale, lean, ground alignment,
  sink and their own height/slope/moisture ranges) under a forest rule (height, slope, moisture, terrain layers to
  grow on or avoid, noise for stands and clearings), placed over the world or a footprint (Replace swaps out the
  forests above it; the floor paint lays a layer by density). Every feature's tree effect can thin or clear trees
  where it acts (roads, rivers), plant a Tree Scatter's species in a line along a spline, or grow its forest where an
  area feature acts (a forest on a stamped mountain). SpeedTree and HDRP prefabs
  convert too (billboard LODs dropped, vertex occlusion kept); prototypes with a MeshCollider (rocks) get mesh
  colliders. Grass prototypes come from small prefabs (**Assets > Strata > Create Grass Prototypes**) and can take the
  terrain's colour (greyscale textures are multiplied by it).
- Grass: `StrataGrassPrototype` assets (a small mesh with a `Strata/Tree` material, clumps per square metre, range,
  variation) mixed and placed by the **Grass Scatter** feature (its meadow: density, ranges, layers, noise and
  entries); tree effects on every forest
  clear grass too. Density maps (up to 8 prototypes) are baked; the clumps themselves are generated on the GPU around
  each camera every frame and drawn with indirect draws. Assign `StrataGrass.compute` on the world for builds.
- Caves: **Cave Tunnel** (a tube along a spline, with a flat floor and radius per point) and **Cave Chamber** (a
  noisy ellipsoid) features carve tunnels and caverns, or with Add make rock (arches, overhangs), in a signed distance
  field that starts as the ground and fillets into it. They are meshed on the CPU (Burst surface nets, at the world's
  Cave Voxel Size) into bricks stored with the tiles, drawn as plain MeshRenderers with the `Strata/CaveSurface`
  material (palette layers for walls and floors, blending into the ground's layers near the surface), and cut holes
  in the terrain where they open it: the surface shader clips them, and collision, trees and grass skip them. A
  cave's paint and tree effect act on the ground around its entrances (by default clearing trees and grass). Caves that
  reach the surface are also meshed coarsely and switch level by distance (and a streamed world keeps the coarse ones
  for the whole world, so arches are seen from afar); Mouth Carve ramps the ground into a tunnel's ends; **Rock Arch**
  and **Rock Blob** are the same features adding rock. **Cave Formations** scatters `StrataFormationPrototype` meshes
  (stalactites, stalagmites, crystals) over the cave floors, ceilings and walls inside its box, drawn instanced.
- Biomes: **Biome Area** features (a colour, Surface or Underground, closed spline outlines with an edge falloff and
  noise; no outline for the whole world) paint a surface and an underground biome map in hierarchy order. The
  palette's Biome Masks, forests, meadows, cave wall and floor layers and cave formations can be limited to biomes,
  and the maps are baked with the tiles for gameplay queries.
- Edits preview live in the editor; **Bake** writes one compact `.bytes` file per tile (heights, splats, trees,
  grass densities, biome maps, and any cave holes and meshes) and a far file to `StreamingAssets/Strata/<world>/`, and records them in the world's Baked Data
  (save the scene afterwards).
- Streaming (play mode): tiles near cameras and focus transforms are loaded asynchronously into a fixed number of
  slots; everything else is drawn from the resident far copy (heights every 8 m, splats every 8 m at the default
  spacing). Trees are all resident (16 bytes each on the GPU); grass and collision exist only on resident tiles.
- `StrataGround` answers height, normal, terrain-layer and biome queries for gameplay code; the ground ones fail over
  cave holes, and `IsHole` and `IsUnderground` tell when a position is over one or in a cave (`TryGetBiome` then uses
  the underground map).
- The surface, caves, trees and grass render forward or, under deferred rendering, into the GBuffer (leaves keep their
  translucency there, added to the lighting buffer).
- The surface, trees and grass are drawn through `BatchRendererGroup`: GPU-displaced CDLOD patches, and trees with
  per-tree LOD crossfade and impostors at 16 bytes of GPU memory a tree.
- Terrain mesh collision, tree trunk capsules and cave colliders are streamed in chunks around focus transforms
  (`RegisterFocus`, or a `StrataFocus` component).
- A `StrataNavMesh` next to the world streams NavMesh around the same foci in play mode, from that collision plus
  colliders on its Extra Layers, with one NavMesh per listed agent type. It uses the engine's `NavMeshBuilder`, so the
  AI Navigation package isn't needed.

Namespace: `Strata`.
