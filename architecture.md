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

## 2. Perspective Controller (The CAD Viewport)
- File Path: game/CadCameraController.cs
- Mouse Rigging: Right-click drag orbits target, Middle-click drag pans, Mouse wheel zooms.
- View Snapping: Key 1 (Front View), Key 2 (Top View), Key 3 (Side View). 
- Projection Mode: Always Perspective. Hotkey snaps only change yaw/pitch (never the projection mode), so resuming mouse orbit from a snapped view never alters the render output.