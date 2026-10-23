# Changelog

## 0.12.1
- Bake Probes now really bakes against the caves: their probe proxies were merged with `CombineMeshes`, which drops
  the vertices of the cave meshes' compressed formats, so the proxies were empty (no probes placed around caves, no
  occlusion in them or under arches). They are merged by hand into plain float positions and normals.
- The terrain and cave surfaces render deferred: `Strata/TerrainSurface` and `Strata/CaveSurface` have a GBuffer pass
  (as URP's Lit: albedo, specular, smoothness, normal, and their probe and reflection lighting into the lighting
  buffer; decals applied) and their forward pass is `UniversalForward` instead of `UniversalForwardOnly`, so under
  deferred rendering they are in the GBuffer for deferred lights and everything that reads it. The lit passes share
  their surface code (`StrataTerrainPasses.hlsl`, `StrataCavePasses.hlsl`).
- Trees, grass and impostors render deferred too: `Strata/Tree` and `Strata/TreeImpostor` get the same GBuffer pass
  (`StrataTreePasses.hlsl`, `StrataTreeImpostorPasses.hlsl`), with the light through the leaves (the main light from
  behind, `StrataTranslucency.hlsl`) added to the lighting buffer, so they look as they did forward.

## 0.12.0
Cave formations.
- `StrataFormationPrototype` assets: a mesh growing up +Y from its base, instancing-capable materials, the surface it
  grows on (floor, ceiling or wall), how far it leans towards the surface's normal, scale range, sink, draw distance,
  shadows and an optional mesh collider. **Tools > Strata > Generate Example Formations** makes a stalactite, a
  stalagmite and a crystal cluster to test with.
- The **Cave Formations** feature (GameObject > Strata) scatters its entries' prototypes at a density per 100 square
  metres over the cave surfaces inside its box. Placement runs on the level 0 cave meshes, each triangle's formations
  hashed from its own centre, so bakes are repeatable; the ground around cave mouths gets none.
- Tiles store their formations as 16-byte `StrataFormationRecord`s (flag Formations) and Baked Data the prototypes.
  `StrataFormations` draws them with `Graphics.RenderMeshInstanced` per camera, culled by frustum and draw distance
  in 64 m cells, and `StrataCaveColliders` gives colliding ones a MeshCollider in active collision chunks (runtime
  collider meshes must be readable). The live preview places them again with the caves, or when a Cave Formations
  feature changes.

## 0.11.0
Cave detail levels, rock and mouths.
- Regions of caves that reach the surface (a level 2 brick, 128 cells across) are also meshed at twice and four times
  the cell size, and each camera sees a region at one level by distance (Settings > Caves > Cave LOD Distance, level 1
  out to 2.5 times it); a coarse level that loses much of its region's rock (thin arches) isn't used there. Coarse
  bricks have skirts into the rock to cover cracks between levels. Caves that are all underground are drawn only within
  Cave Underground Distance.
- The cave data has a level and flags per brick (magic "STR2"); 0.10 data still reads. A streamed world loads
  `caves_far.bytes` (each tile's coarsest bricks, written by the bake) and shows them for tiles that aren't resident, so
  arches and cave mouths stay in view beyond the streaming radius. Colliders and probe proxies use level 0.
- **Mouth Carve** on a Cave Tunnel lowers the heightfield towards the tunnel's floor over Mouth Length from each end of
  its spline, fading inwards, so the ends open as ramps.
- **Rock Arch** and **Rock Blob** under GameObject > Strata: a Cave Tunnel and a Cave Chamber set to Add.
- The live preview only meshes and cuts holes again over what an edit touched (keeping the other bricks), meshes the
  levels in parallel, and while a handle is dragged updates level 0 only, the coarse levels following when it is let
  go. Blocks of cells far from the surface or from every shape aren't evaluated in full, primitives are culled per row
  for ground weights, and noise is left off shapes too far away to matter; the meshes are unchanged.

## 0.10.0
Caves, tunnels and rock.
- **Cave Tunnel** (needs a SplineContainer: radius, height over width, flat floor, with radius and floor per point)
  and **Cave Chamber** (an ellipsoid the size of its scale, with a flat floor) features, under GameObject > Strata.
  Carve hollows the ground out; Add makes rock, for arches and overhangs. Each has a fillet Smoothness, noise, wall
  and floor palette layers and a floor slope, and a paint and tree effect for the ground around it (by default the
  tree effect clears trees and grass).
- They act on a signed distance field that starts as the heightfield's ground, in hierarchy order. The bake (and the
  live preview, for the tiles an edit touches) meshes it near the features with Burst surface nets in bricks of 32
  cells on a world lattice (Settings > Caves > Cave Voxel Size, default 1 m), so tiles meet exactly and a bake is
  repeatable. Ground near a feature is kept just under the terrain, so entrances have no seam.
- Where a carve opens the ground the terrain gets a hole: a byte of presence per height sample (`StrataHoles`, owned
  by the heightfield and part of `StrataHeights`). The surface shader's `_STRATA_HOLES` variant, drawn only for CDLOD
  nodes over a hole, clips it in every pass; terrain collision leaves those triangles out; trees and grass aren't
  placed there.
- Tiles with holes or caves are written as version 6, with the holes and the encoded cave meshes (`StrataCaveData`)
  after the grass; other tiles stay version 5, so a world without caves bakes byte for byte as before. Baked Data
  records the voxel size and how many tiles have holes (a streamed world's hole slots).
- `StrataCaves` turns each loaded tile's cave data into hidden MeshRenderers drawn with the world's **Cave Material**
  (`Strata/CaveSurface`, `Shaders/StrataCave.mat`; assign it for builds), textured from the palette like the terrain
  (`StrataSurfaceWith`, split out of `StrataSurface`) and sampling probes; `StrataCaveColliders` gives the bricks in
  each active collision chunk MeshColliders, which `StrataNavMesh` builds from.
- `StrataGround.TryGetHeight`, `TryGetNormal` and `TryGetLayer` fail over a hole; `TryGetGround` raycasts against
  the world's collision first in play mode; `IsHole` and `IsUnderground` are new.
- The probe baker leaves the terrain proxy open over holes and adds the caves as proxies.
- Evaluating features skips the world's own hidden objects.

## 0.9.0
A world is configured entirely in the scene: the settings assets become blocks on the components.
- The world settings, palette and baked data are blocks on the `StrataWorld` (Settings, Palette, Baked Data) instead
  of `StrataWorldSettings`, `StrataPalette` and `StrataWorldData` assets. The palette's texture check and **Fix Import
  Settings** are in the world's inspector. **Bake** writes the Baked Data block, so save the scene after baking.
- A forest is a block on its Tree Scatter and a meadow a block on its Grass Scatter, instead of `StrataForest` and
  `StrataMeadow` assets; each scatter is its own forest or meadow. A tree effect names a **Tree Scatter** (Scatter)
  instead of a forest; a disabled Tree Scatter places nothing but can still be planted that way.
- Prototypes (`StrataTreePrototype`, `StrataGrassPrototype`) and layers (`StrataLayer`) are still assets.
- The spline's Profile curve is now called Cross Section (`SplineFeature.CrossSection`).
- Plant works on area features (texture and noise stamps, filters, rock strata): it grows the chosen forest, by the
  forest's own rules, where the feature acts, on top of what is there. Before, only splines planted (a line of
  trees) and Plant on anything else did nothing.
- Tree and Grass Scatter lose their mask, blend, paint and tree effect: the forest or meadow block decides where and
  how densely it grows. The forest's **Floor Paint** replaces the Tree Scatter's paint.
- Auto Splat's Flow For Full Moisture and Water Reach move to the world's settings (Texturing). Editing the moisture
  settings or sea level now refreshes the preview.
- Existing scenes need migrating before updating, or the asset data is lost: copy each settings, palette, data,
  forest and meadow asset into the block on the component that used it (they keep their field names, so
  `EditorJsonUtility` round trips them), point tree effects at Tree Scatters, and copy each Tree Scatter's paint into
  its forest's floor paint and Auto Splat's moisture into the world settings.

Navigation and imports.
- `StrataNavMesh` (add it next to a `StrataWorld`) streams NavMesh around the collision foci in play mode: a box per
  focus, built from the streamed terrain chunks and tree colliders plus colliders on Extra Layers (moving bodies,
  agents and foci are skipped), one NavMeshData per agent type in Agent Types. A box rebuilds asynchronously once its
  focus moves Rebuild Distance or a collision chunk inside it changes. `StrataCollision.Root` and
  `StrataTreeColliders.Root` expose where their colliders live. The package now depends on the built-in AI module.
- The tree builder converts Amplify materials too (`_Albedo` base maps, and `_SmoothnessMap` gloss or roughness maps
  as a constant smoothness), and a prototype with no collider on its prefab that doesn't sway (a rock) collides with
  its LOD0 mesh.

## 0.8.0
Performance. A 1920x1080 render of the 8 km test world (CPU and GPU) went from 11.7 to 6.3 ms from above the forest
and from 11.2 to 9.0 ms at ground level.
- Tree culling runs in parallel: a job per 64 m cell classifies its runs into a NativeStream, and a second job sorts
  them into draw commands (0.47 to 0.29 ms per view).
- Impostor cards are trimmed to each frame's silhouette (Frame Bounds, one texel per frame, made when the impostor is
  baked), so far fewer transparent pixels are shaded. Re-bake impostors to get them.
- Impostor Shadow LODs (per prototype, default 1): the last mesh LODs cast the impostor's shadow instead of their own,
  which saves about 0.9 ms. That stand-in card is pushed towards the light (Impostor Shadow Push, default a quarter
  of the tree's radius), so the mesh keeps shading itself (through the centre it lit the mesh's whole sunward half, a
  too-bright band at mid range).
- Edits to a tree prototype reach the tree renderer straight away.
- Strata/Tree can take smoothness from a map's alpha (Smoothness From, Smoothness Map, Smoothness Range): the tree
  builder uses SpeedTree's extra map, HDRP's mask map (its remap range, capped at the material's Smoothness, since
  Shader Graph copies keep stale remap fields) or an MHER mask (alpha as roughness), and impostors take the map's
  average.
- Grass thins out past 30% of its range, with the remaining clumps growing to keep the ground covered (about a third
  fewer clumps drawn).
- The terrain samples only the near tiling stochastically (the far tiling hides repeats on its own).
- Streaming: a tile's strata leaf ranges are found by a Burst function instead of a managed loop (installing a tile
  went from about 4.2 to 1.1 ms), and tile slices upload through reused staging textures.
- Editor: changing only a forest's species (or a prototype, or the tree seed) places the trees again from the cached
  densities without evaluating the splats again. Placement runs without Burst safety checks. A bake rewrites only the
  tile files whose contents changed.

## 0.7.0
- Realistic assets. The tree builder converts materials whose shader is missing (HDRP, SpeedTree) from their saved
  properties, drops billboard LODs, reads alpha cutoffs from the HDRP and SpeedTree names, tells bark from foliage by
  subsurface/thickness maps, gives every material of a prototype one wind (none without foliage), uses SpeedTree's
  vertex colour as occlusion (baked into impostors too), and turns BoxColliders into trunk capsules and MeshColliders
  into mesh colliders. Prototypes have an Impostor Brightness to match their meshes.
- Mesh-collider prototypes (rocks): pooled MeshColliders turned and scaled like their instances.
- Grass prototypes from prefabs (Assets > Strata > Create Grass Prototypes). Grass can take the terrain's colour: Ground
  Multiply colours a greyscale texture by it, Ground Tint blends a coloured one's base into it.
- Layers: MHER mask layout (metallic, height, emission, roughness) and DirectX normal maps.
- Natural tree placement: random candidates (four per spacing cell) instead of a jittered grid, a per-tree room that
  varies and shrinks inside groups, a clump field per species (Clumping, Clump Size) that gathers trees into groups
  with gaps, and single-species stands per forest (Patchiness, Patch Size). Trees in group cores are a little bigger.
- The live preview rebuilds a world that is still streamed when play mode has just ended.
- Fixed trees standing in rows: tree placement hashed with math.hash, which is a plain weighted sum, so neighbouring
  cells' random values stepped by a constant; it now uses an avalanching mix (lowbias32).
- The palette inspector checks its textures without packing them on every repaint.
- Fixed a band without tree shadows: impostors cast only into the last shadow cascade, but cascades split by view depth
  while tree LODs go by distance, so from an elevated camera impostors stood over ground in the nearer cascades. They
  now cast into every cascade that sees them.

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
