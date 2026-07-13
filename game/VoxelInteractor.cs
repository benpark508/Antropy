using Godot;

// Mouse-driven dev tooling for the hybrid 2D cross-section CA: reads the
// mouse's native 2D canvas position directly (no raycasting or viewport
// projection needed on a flat Godot 2D scene) and writes straight into
// VoxelGrid's authoritative _cells array via TryGetCell/TrySetCell. This
// node never touches rendering -- the existing SimulationTickRate loop in
// VoxelGrid picks up any mutated cell on its next tick and drives the
// MultiMesh visualization exactly like a CA-generated change would.
public partial class VoxelInteractor : Node2D
{
    [Export] public NodePath VoxelGridPath;

    private VoxelGrid _voxelGrid;

    // Cell currently under the mouse cursor, refreshed once per frame in
    // _Process and shared by the resource brush (polled, hold-to-apply)
    // and the click logger (_Input, fires once per press).
    private bool _hasHoveredVoxel;
    private int _hoverX, _hoverY;

    public override void _Ready()
    {
        _voxelGrid = GetNode<VoxelGrid>(VoxelGridPath);
    }

    public override void _Process(double delta)
    {
        _hasHoveredVoxel = TryResolveVoxel(out _hoverX, out _hoverY);
        if (!_hasHoveredVoxel)
            return;

        // Developer keys (C, P, F) still target deep internal coordinates --
        // i.e. whatever cell the mouse is currently hovering, same as the
        // erase brush below, not the surface-clamped rain brush.
        ApplyDeveloperResourceBrush();

        // Polling (rather than reacting only to button-down events) is what
        // makes click-and-hold painting free: every frame the button is
        // down, we just re-apply the same edit to the already-resolved
        // hover cell.
        if (!Input.IsMouseButtonPressed(MouseButton.Left))
            return;

        if (Input.IsKeyPressed(Key.Shift))
        {
            ApplyEraseBrush();
            return;
        }

        if (Input.IsKeyPressed(Key.Ctrl))
        {
            ApplyFillBrush();
            return;
        }

        ApplyWaterBrush();
    }

    // Fires once per left-click press (not every frame the button is held,
    // unlike the brushes above) so the console gets exactly one clean
    // summary line per click instead of being spammed for the duration of
    // a held click.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton
            || mouseButton.ButtonIndex != MouseButton.Left
            || !mouseButton.Pressed)
            return;

        LogVoxelInspection();
    }

    private void LogVoxelInspection()
    {
        if (!_hasHoveredVoxel)
            return;

        if (!_voxelGrid.TryGetCell(_hoverX, _hoverY, out VoxelCell cell))
            return;

        GD.Print($"Cell ({_hoverX}, {_hoverY}) -> Type={cell.Type} Water={cell.Water:F1} Carbon={cell.Carbon:F1} Phosphorus={cell.Phosphorus:F1} Fungus={cell.FungalPresence}");
    }

    // Water Brush: adding water always deposits onto the highest Solid cell
    // in the hovered column (the ground surface), not the hovered depth, so
    // it has to filter down through Ground Percolation like rain instead of
    // being injected directly into a mid-column tunnel.
    private void ApplyWaterBrush()
    {
        if (!TryFindHighestSolidRow(_hoverX, out int surfaceY))
            return;

        if (!_voxelGrid.TryGetCell(_hoverX, surfaceY, out VoxelCell cell))
            return;

        cell.Water = VoxelGrid.MaxWater;
        _voxelGrid.TrySetCell(_hoverX, surfaceY, cell);
    }

    private bool TryFindHighestSolidRow(int x, out int y)
    {
        for (int candidate = VoxelGrid.Height - 1; candidate >= 0; candidate--)
        {
            if (_voxelGrid.TryGetCell(x, candidate, out VoxelCell cell) && cell.Type == CellType.Solid)
            {
                y = candidate;
                return true;
            }
        }

        y = 0;
        return false;
    }

    // Erase Tool: carves a tunnel by converting the hovered cell to Air and
    // zeroing its resources, letting a developer manually open cavities to
    // test Open Fluid Pooling and Tunnel Seeping.
    private void ApplyEraseBrush()
    {
        if (!_voxelGrid.TryGetCell(_hoverX, _hoverY, out VoxelCell cell))
            return;

        cell.Type = CellType.Air;
        cell.Water = 0f;
        cell.Carbon = 0f;
        cell.Phosphorus = 0f;
        cell.FungalPresence = false;
        _voxelGrid.TrySetCell(_hoverX, _hoverY, cell);
    }

    // Fill Tool: the inverse of Erase -- converts the hovered cell back to
    // blank Solid earth (zeroed, not restored to whatever it held before),
    // so carving a tunnel to test pooling is reversible without restarting
    // the whole simulation to undo a mistake.
    private void ApplyFillBrush()
    {
        if (!_voxelGrid.TryGetCell(_hoverX, _hoverY, out VoxelCell cell))
            return;

        cell.Type = CellType.Solid;
        cell.Water = 0f;
        cell.Carbon = 0f;
        cell.Phosphorus = 0f;
        cell.FungalPresence = false;
        _voxelGrid.TrySetCell(_hoverX, _hoverY, cell);
    }

    // Dev-only diagnostic brush: while hovering a resolved cell, holding
    // C/P/F force-sets Carbon/Phosphorus to max or FungalPresence to true,
    // bypassing CA rules entirely. Exists purely to hand-seed test states
    // (e.g. force-feed a starving colony, or seed fungus somewhere the
    // spread roll would never reach) without waiting on the simulation.
    private void ApplyDeveloperResourceBrush()
    {
        if (!_voxelGrid.TryGetCell(_hoverX, _hoverY, out VoxelCell cell))
            return;

        bool changed = false;

        if (Input.IsKeyPressed(Key.C))
        {
            cell.Carbon = VoxelGrid.MaxNutrient;
            changed = true;
        }

        if (Input.IsKeyPressed(Key.P))
        {
            cell.Phosphorus = VoxelGrid.MaxNutrient;
            changed = true;
        }

        if (Input.IsKeyPressed(Key.F))
        {
            cell.FungalPresence = true;
            changed = true;
        }

        if (changed)
            _voxelGrid.TrySetCell(_hoverX, _hoverY, cell);
    }

    // Reads the mouse's real-time position on the native 2D canvas
    // (GetGlobalMousePosition() already accounts for the active Camera2D's
    // pan/zoom) and converts it to an integer grid cell -- no raycasting,
    // ray-plane math, or viewport projection needed on a flat Godot 2D
    // scene. The Y axis goes through VoxelGrid.ScreenYToRow rather than a
    // raw division, since screen Y grows downward while the simulation's
    // row index grows upward (see VoxelGrid.RowToScreenY/ScreenYToRow).
    private bool TryResolveVoxel(out int x, out int y)
    {
        Vector2 mousePosition = GetGlobalMousePosition();

        int gridX = Mathf.FloorToInt(mousePosition.X / _voxelGrid.TileSize);
        int gridY = VoxelGrid.ScreenYToRow(mousePosition.Y, _voxelGrid.TileSize);

        if (!VoxelGrid.IsInBounds(gridX, gridY))
        {
            x = y = 0;
            return false;
        }

        x = gridX;
        y = gridY;
        return true;
    }
}
