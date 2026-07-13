## [2026-07-12] - Phase 1 Complete (Unified Perspective Refinement)
- Refactored CAD camera snapping system to remain in Perspective mode at all times.
- Eliminated true Orthogonal projection to prevent visual depth discontinuities.
- Synchronized visual framing during angle snaps using exact vertical FOV trigonometry.

## [2026-07-12] - Phase 2A Complete
- Implemented a framerate-decoupled simulation tick clock (0.1s default).
- Built a strict double-buffered array system (`_cells` and `_cellsBuffer`) to eliminate directional flow bias.
- Coded physics-accurate downward gravity flow and horizontal 4-way equalization.
- Refactored voxel rendering into a persistent mesh instance pool to eliminate runtime memory allocation churn.

## [2026-07-12] - Phase 2B Complete
- Added `VoxelInteractor.cs`: a left-click mouse brush for stress-testing the water CA.
- Implemented ray/grid-AABB intersection via the slab method, using the camera's active `ProjectRayOrigin`/`ProjectRayNormal`.
- Left-Click injects `Moisture = 100`; Shift + Left-Click drains to `Moisture = 0`; both support click-and-hold painting.
- Writes go directly into `VoxelGrid`'s authoritative `_cells` array, leaving the existing tick loop and mesh instance pool to handle visualization — no changes to `CadCameraController`'s Right-click orbit / Middle-click pan.
- Fixed Top View clicks always resolving to the topmost, permanently-dry ceiling layer (gravity only pulls moisture down, so nothing refills it): the ray now marches inward from the AABB entry point and targets the first cell actually above the debug-visibility threshold, falling back to the entry cell only when the whole ray path is dry.

## [2026-07-12] - Phase 3A Complete
- Implemented nutrient advection (leaching), forcing Carbon and Phosphorus to migrate proportionally with active water flow.
- Built a 6-way nutrient diffusion pass based on concentration gradients (Fick's Law).
- Gated chemical diffusion to require active moisture presence in both source and destination cells.

## [2026-07-12] - Phase 3B Complete (Mycelial Networks)
- Implemented a resource-gated fungal growth loop driven by Carbon and Phosphorus consumption.
- Developed a 6-way weighted probability spreading algorithm that prioritizes damp, nutrient-dense neighbor cells.
- Enforced a hard environmental constraint preventing fungal expansion into bone-dry (`Moisture = 0`) voxels.
- Isolated fungal growth simulations into a dedicated loop sweep to prevent early-exit water logic from freezing dry fungal colonies.

## [2026-07-12] - Phase 4 Complete (GPU MultiMesh Optimization)
- Refactored the voxel rendering pipeline from an individual Node3D pool to a single `MultiMeshInstance3D`.
- Collapsed 8,000 independent draw calls and material allocations down to a single GPU instancing operation.
- Implemented per-instance vertex coloring by enabling `VertexColorUseAsAlbedo` on a shared material.
- Managed voxel visibility dynamically by driving instance color alpha channels to 0, eliminating scene-tree allocation churn.

## [2026-07-12] - Phase 4.7 Complete (Soil Physics & Interface Calibration)
- Introduced a configurable `PercolationRate` parameter to govern slow, realistic fluid seeping.
- Refactored player water brush to automatically clamp to the highest surface layer, simulating localized rainfall.
- Built a soil stratification engine initializing realistic soil horizons (surface Carbon blanket and 6 deep procedural Phosphorus clusters).

## [2026-07-12] - Phase 5 Complete (2D Ant Farm Pivot)
- Pivoted from a 20x20x20 voxel cube to a flat 100x50 2D cross-section grid; replaced `Moisture` with a `CellType` (`Solid`/`Air`) + `Water` model.
- Replaced the single gravity/equalization water rule with a hybrid fluid engine: Ground Percolation (Solid -> Solid), Tunnel Seeping (Solid -> Air), and Open Fluid Pooling (Air <-> Air, instant free-fall + equalization).
- Narrowed nutrient leaching/diffusion and fungal spread to the 2D 4-neighbor grid; rebuilt ecosystem stratification for the new row layout.
- Simplified `VoxelInteractor` to a single ray/plane intersection (no raymarching); added the Erase tool and a surface-clamped rain brush.

## [2026-07-12] - Phase 6 Complete (Native Godot 2D Migration)
- Migrated fully off 3D nodes: `MultiMeshInstance2D` + `Camera2D` + `Node2D` throughout, no `Node3D`/`Camera3D`/`Transform3D` left anywhere.
- Mouse picking now reads `GetGlobalMousePosition()` directly (divided by a new `TileSize` export) instead of raycasting.
- Rebuilt the camera as a smooth pan/zoom side-scroller; renamed `CadCameraController` -> `FarmCameraController` to match.
- Split simulation from rendering: `VoxelGrid` drives a referenced sibling `MultiMeshInstance2D` rather than inheriting from it.
- Fixed a Y-axis convention mismatch (`RowToScreenY`/`ScreenYToRow`) that rendered the whole cross-section upside down, and a half-tile visual/pick misalignment from `QuadMesh`'s origin-centered geometry.

## [2026-07-12] - Phase 7 Complete (Fluid Engine Realism Pass)
- Added `SoilFieldCapacity`: water below this threshold is permanently retained in `Solid` soil, only the excess is mobile -- soil now settles into a lasting damp baseline instead of draining to zero.
- Added Lateral Tunnel Seeping and Lateral Soil Spread: a `Solid` cell now weeps into `Air` cavities and spreads into drier `Solid` neighbors beside it, not just below -- rain fans out through soil instead of falling in a single straight column. Raised `TunnelSeepRate` (`0.05` -> `0.15`) so it's actually visible next to `PercolationRate`.
- Added a Fill tool (Ctrl + Left-Click), the inverse of Erase, so tunnel/pooling experiments are reversible without restarting the simulation.
- Removed the dead `3d/physics_engine="Jolt Physics"` setting from `project.godot` -- the game has no physics bodies.

