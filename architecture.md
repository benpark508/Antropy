# Antropy - Global Architecture Blueprint

## 1. Grid & Cell Model
- 100x50 2D grid (`Width x Height`), one `VoxelCell` per tile, flattened to `index = x + y * Width`. File: `game/VoxelGrid.cs`.
- `CellType`: `Solid` (earth, rows `0..44`) or `Air` (sky, rows `45..49`). `SurfaceRow = Height - AirRows = 45` is the shared boundary constant.
- `VoxelCell`: `Type`, `Water` (0-`MaxWater`=100), `Carbon` (0-`MaxNutrient`=100), `Phosphorus` (0-`MaxNutrient`=100), `FungalPresence`. All numeric fields are `float`.
- `PopulateBaseline()` starts every cell dry (`Water = 0`); `InitializeSoilNutrients()` stratifies Carbon/Phosphorus afterward (Section 7).

## 2. Scene & Rendering
- Native Godot 2D throughout — no 3D nodes anywhere in the project.
- `VoxelGrid` (`Node2D`) owns the simulation only; it resolves a sibling `MultiMeshInstance2D` (`MultiMeshRendererPath`) and pushes `SetInstanceTransform2D`/`SetInstanceColor` into it rather than being the renderer itself.
- Instances sit at `(x * TileSize + halfTile, RowToScreenY(y, TileSize) + halfTile)`: `RowToScreenY`/`ScreenYToRow` are the single shared conversion between the simulation's Y-up row index and Godot 2D's Y-down screen space (used by both rendering and mouse picking); the `+ halfTile` offset centers each `QuadMesh` (which is origin-centered) inside the `TileSize` bucket picking assumes.
- `FarmCameraController` (`Camera2D`) is a standalone pan/zoom rig: Right-click drag or WASD/Arrows smoothly pan (`Position.Lerp`), mouse wheel smoothly zooms (`Zoom.Lerp`, clamped `MinZoom`-`MaxZoom`). `GridPixelSize` centers its starting position and must be kept in sync by hand with `Width/Height * TileSize`.

## 3. Mouse Interaction (`VoxelInteractor.cs`, `Node2D`)
- Picking: `GetGlobalMousePosition()` (Camera2D-aware) → grid cell via floor-division on X and `ScreenYToRow` on Y. No raycasting anywhere in the pipeline.
- **Left-Click**: rains onto the highest `Solid` row in the hovered column (`Water = MaxWater`) — water then has to percolate down naturally, not injected into a mid-column tunnel.
- **Shift + Left-Click (Erase)**: converts the hovered cell to `Air`, zeroing `Water`/`Carbon`/`Phosphorus`/`FungalPresence`.
- **Ctrl + Left-Click (Fill)**: inverse of Erase — converts the hovered cell back to blank `Solid`, so digging is reversible.
- **C / P / F (held)**: force-sets Carbon/Phosphorus to max or `FungalPresence` to true on the hovered cell — dev-only, bypasses all CA rules.
- **Left-Click (press)**: edge-triggered console log of the hovered cell's full state.
- All edits go through `VoxelGrid.TryGetCell`/`TrySetCell`; the next `SimulationTickRate` tick picks up the change like any CA-driven one.

## 4. Fluid Engine
- File: `game/VoxelGrid.cs`, `SimulateFluidFlow()`, called on a fixed `SimulationTickRate` (default 0.1s). Strictly double-buffered: every tick reads only `_cells` and writes only `_cellsBuffer`, then swaps.
- Behavior dispatches on `CellType`; `Solid` cells further split their water into a retained baseline and a movable excess.
  - **Field Capacity**: water below `SoilFieldCapacity` (30) never moves; only `movableWater = max(0, water - SoilFieldCapacity)` is eligible to flow. Keeps soil permanently damp after rain instead of draining to zero.
  - **Ground Percolation** (Solid → Solid below): moves `PercolationRate` (0.2) of `movableWater` per tick, capped by the neighbor's capacity. Carries leached nutrients (Section 5).
  - **Tunnel Seeping** (Solid → Air, below or beside): moves `TunnelSeepRate` (0.15) of `movableWater` into any adjacent open cavity — down, left, right, all independent and additive. No nutrient leaching.
  - **Lateral Soil Spread** (Solid → Solid, beside): moves `LateralSpreadRate` (0.1) of the movable-water gradient into a less-saturated `Solid` neighbor, so rain fans out sideways through undisturbed soil, not just at tunnel walls. Carries leached nutrients like percolation.
  - **Open Fluid Pooling** (`Air`): falls instantly into an `Air` cell below with capacity; if blocked, equalizes sideways with `Air` neighbors at `PoolEqualizationRate` (0.5) toward a flat level.
  - Every gradient-based rule compares only the higher side's excess, so a neighbor pair can't be double-counted from both directions.
  - Safety clamp: `Water`/`Carbon`/`Phosphorus` clamped to their valid ranges at the end of every tick.

## 5. Nutrients
- File: `game/VoxelGrid.cs`. Solid-only — nutrients never move through `Air`, which isn't a chemical transport medium in this model.
- **Leaching**: a proportional slice of Carbon/Phosphorus rides along with any bulk Solid→Solid water transfer (percolation or lateral spread), scaled by `LeachingEfficiency` (0.5).
- **Diffusion**: independent concentration-gradient equalization between the 4 Solid neighbors, gated by `MinWaterForDiffusion` (1), rate `NutrientDiffusionRate` (0.1).

## 6. Fungal Growth
- File: `game/VoxelGrid.cs`, `SimulateFungalGrowth()` — its own sweep after the fluid pass, since survival depends on Carbon/Phosphorus, not water.
- Each fungal cell pays `FungalCarbonCost` (2) / `FungalPhosphorusCost` (1) per tick or starves; a surviving cell rolls a spread chance against each of its 4 neighbors.
- Spread chance = `FungalBaseSpreadChance` (0.15) × a weighted average of the neighbor's water and nutrient fractions (`FungalMoistureWeight` 0.6 / `FungalNutrientWeight` 0.4). A bone-dry neighbor (`Water = 0`, `Solid` or `Air`) is refused outright.

## 7. Visualization & Diagnostics
- `ColorMode` (`,` Water / `.` Nutrient / `/` Fungus) selects what `ComputeInstanceColor()` maps to per-instance color.
- `Solid`: continuous brown→near-black gradient by water saturation in Water mode; flat baseline brown in Nutrient/Fungus modes when inactive.
- `Air`: transparent when dry (faint sky tint within the original sky band), opaque blue pool (color and alpha both scaling with fill) once wet.
- Window title bar shows live `Water`/`Carbon`/`Phosphorus`/fungal-cell-count totals each tick as a mass-conservation sanity check.

## 8. Ecosystem Stratification
- File: `game/VoxelGrid.cs`, `InitializeSoilNutrients()`.
- Top 3 `Solid` rows (`42-44`): Carbon `60-80` (organic layer).
- Middle `Solid` rows (`6-41`): neutral, zeroed.
- Bottom 6 `Solid` rows (`0-5`, bedrock): 6 randomized Phosphorus clusters (radius 6, intensity `60-100`, radial falloff, blended via `Max` where they overlap).
