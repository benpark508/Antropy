# Antropy - Global Architecture Blueprint

## 1. System Matrix Layout (The Soil Grid)
- Dimensions: 20x20x20 voxel grid.
- File Path: game/VoxelGrid.cs
- Memory Layout: Flattened 1D array (`VoxelCell[]`) mapped to 3D space coordinates: `index = x + (y * width) + (z * width * height)`.
- Cell Data Struct (`VoxelCell`):
  * int Moisture (0 to 100)
  * int Carbon (0 to 100)
  * int Phosphorus (0 to 100)
  * bool FungalPresence

## 1a. Mouse Interaction Brush (Phase 2B)
- File Path: game/VoxelInteractor.cs
- Ray-to-Grid Intersection: Every frame, resolves `GetViewport().GetCamera3D().ProjectRayOrigin/Normal()` for the current mouse position, transforms the ray into `VoxelGrid`'s local space, and intersects it against the grid's AABB via the slab method. The AABB is built around the debug mesh instances' actual centers (`(x, y, z) * CellSize`), so recovering a voxel index from a sample point rounds to the nearest center rather than flooring into a corner-aligned cell.
- Surface Picking: The outer AABB shell alone isn't enough — from Top View the entry face is always the topmost, near-permanently-dry layer (gravity only pulls moisture down), so a naive entry-point pick would edit an invisible, empty cell no matter where the visible water actually is. Instead the ray marches inward from the entry point in `CellSize * 0.25` steps and stops at the first cell whose `Moisture` is at or above `MoistureVisibilityThreshold` — i.e. the first cell actually rendered by the debug visualization. If the whole ray path through the grid is dry, it falls back to the entry cell so Add Water can still seed the first drop on an empty grid.
- Left-Click (Add Water): While the left mouse button is held over a valid voxel, writes `Moisture = 100` into that cell.
- Shift + Left-Click (Drain Water): While Shift is also held, writes `Moisture = 0` instead.
- Click-and-hold painting: implemented by polling `Input.IsMouseButtonPressed` in `_Process` rather than reacting to discrete button-down events, so holding the button re-resolves and re-applies the edit every frame at no extra cost.
- Decoupled Architecture: Edits go through `VoxelGrid.TryGetCell`/`TrySetCell`, writing straight into the authoritative `_cells` array. `VoxelInteractor` never touches rendering or the double-buffer swap directly — the next `SimulationTickRate` tick reads the mutated cell like any other CA state and drives the existing persistent mesh instance pool.

## 2. Perspective Controller (The CAD Viewport)
- File Path: game/CadCameraController.cs
- Mouse Rigging: Right-click drag orbits target, Middle-click drag pans, Mouse wheel zooms.
- View Snapping: Key 1 (Front View), Key 2 (Top View), Key 3 (Side View). 
- Projection Mode: Always Perspective. Hotkey snaps only change yaw/pitch (never the projection mode), so resuming mouse orbit from a snapped view never alters the render output.

## 3. Water Cellular Automata (Phase 2A)
- File Path: game/VoxelGrid.cs
- Centralized Simulation Tick: A `SimulationTickRate` export (default 0.1s) accumulates `_Process` delta time and fires `SimulateWaterFlow()` on fixed intervals, decoupling flow speed from framerate.
- Strict Double-Buffered State: `_cells` (last settled state) and `_cellsBuffer` (write target) are two persistent flat arrays, never reallocated per tick. Every tick reads exclusively from `_cells` and writes exclusively to `_cellsBuffer`, then the two references are swapped. This guarantees a cell can never consume moisture produced earlier in the same tick, preventing directional loop bias (water "teleporting" across the grid in one frame because the update order happened to run downhill).
- Water Flow Rules (evaluated per cell from the read buffer, applied as deltas to the write buffer):
  1. **Gravity**: A cell attempts to push its full moisture to `(x, y-1, z)`, capped by that neighbor's remaining capacity (`MaxMoisture - neighborMoisture`).
  2. **Horizontal Equalization**: Any moisture that couldn't go down (off-grid at `y=0`, or the cell below is saturated) is split evenly across whichever in-bounds N/S/E/W neighbors exist (mapped to Z-/Z+/X+/X-).
  3. **Safety Clamp**: After all cells are processed, the write buffer is clamped to `[0, MaxMoisture]` to guard against concurrent horizontal inflows stacking above capacity.
- Debug Visualization — Persistent Mesh Instance Pool: One `MeshInstance3D` per grid cell is allocated once (sharing a single `BoxMesh` resource), rather than rebuilt every tick. Each simulation tick only toggles `Visible` and updates `AlbedoColor` alpha (moisture-driven) on the existing pooled instances, keeping a 10Hz simulation update cheap on an 8,000-cell grid.