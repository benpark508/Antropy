## [2026-07-12] - Phase 1 Complete (Unified Perspective Refinement)
- Refactored CAD camera snapping system to remain in Perspective mode at all times.
- Eliminated true Orthogonal projection to prevent visual depth discontinuities.
- Synchronized visual framing during angle snaps using exact vertical FOV trigonometry.

## [2026-07-12] - Phase 2A Complete
- Implemented a framerate-decoupled simulation tick clock (0.1s default).
- Built a strict double-buffered array system (`_cells` and `_cellsBuffer`) to eliminate directional flow bias.
- Coded physics-accurate downward gravity flow and horizontal 4-way equalization.
- Refactored voxel rendering into a persistent mesh instance pool to eliminate runtime memory allocation churn.