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