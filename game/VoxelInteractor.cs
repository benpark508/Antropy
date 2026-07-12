using Godot;

// Left-click brush for stress-testing the water CA: raycasts the mouse
// against the VoxelGrid's spatial bounds and writes Moisture directly into
// the grid's authoritative _cells array via TryGetCell/TrySetCell. This
// node never touches rendering — the existing SimulationTickRate loop in
// VoxelGrid picks up the mutated cell on its next tick and drives the
// persistent mesh instance pool exactly like a CA-generated change would.
public partial class VoxelInteractor : Node
{
    [Export] public NodePath VoxelGridPath;

    private VoxelGrid _voxelGrid;

    public override void _Ready()
    {
        _voxelGrid = GetNode<VoxelGrid>(VoxelGridPath);
    }

    public override void _Process(double delta)
    {
        // Polling (rather than reacting only to button-down events) is what
        // makes click-and-hold painting free: every frame the button is
        // down, we just re-resolve the ray and re-apply the same edit.
        if (!Input.IsMouseButtonPressed(MouseButton.Left))
            return;

        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        Vector2 mousePosition = GetViewport().GetMousePosition();
        Vector3 rayOrigin = camera.ProjectRayOrigin(mousePosition);
        Vector3 rayDirection = camera.ProjectRayNormal(mousePosition);

        if (!TryResolveVoxel(rayOrigin, rayDirection, out int x, out int y, out int z))
            return;

        if (!_voxelGrid.TryGetCell(x, y, z, out VoxelCell cell))
            return;

        bool drain = Input.IsKeyPressed(Key.Shift);
        cell.Moisture = drain ? 0 : VoxelGrid.MaxMoisture;
        _voxelGrid.TrySetCell(x, y, z, cell);
    }

    // Projects the mouse ray into the VoxelGrid's local space, intersects it
    // against the grid's AABB, then marches inward from the entry point
    // looking for the first cell that's actually rendered (Moisture at or
    // above the debug-viz threshold). This is what lets a Top View click
    // reach through dry ceiling layers down to water the user can actually
    // see, instead of always editing the outermost shell voxel. If the
    // entire ray path through the grid is dry, falls back to the entry
    // cell so Add Water can still seed the first drop on an empty grid.
    private bool TryResolveVoxel(Vector3 rayOrigin, Vector3 rayDirection, out int x, out int y, out int z)
    {
        x = y = z = 0;

        Transform3D gridTransform = _voxelGrid.GlobalTransform;
        Vector3 localOrigin = gridTransform.AffineInverse() * rayOrigin;
        Vector3 localDirection = (gridTransform.Basis.Inverse() * rayDirection).Normalized();

        float cellSize = _voxelGrid.CellSize;
        float half = cellSize * 0.5f;
        Vector3 boundsMin = new Vector3(-half, -half, -half);
        Vector3 boundsMax = new Vector3(
            (VoxelGrid.Width - 1) * cellSize + half,
            (VoxelGrid.Height - 1) * cellSize + half,
            (VoxelGrid.Depth - 1) * cellSize + half);

        if (!TryIntersectAabb(localOrigin, localDirection, boundsMin, boundsMax, out float tEntry, out float tExit))
            return false;

        // Nudge both ends a hair inward so floating-point error at the
        // boundary faces can't round a sample outside the grid.
        float t = tEntry + 0.001f;
        float exit = tExit - 0.001f;
        float step = cellSize * 0.25f;

        bool haveEntryCell = false;
        int entryX = 0, entryY = 0, entryZ = 0;
        int lastX = int.MinValue, lastY = int.MinValue, lastZ = int.MinValue;

        for (; t <= exit; t += step)
        {
            Vector3 point = localOrigin + localDirection * t;
            int cx = Mathf.Clamp(Mathf.RoundToInt(point.X / cellSize), 0, VoxelGrid.Width - 1);
            int cy = Mathf.Clamp(Mathf.RoundToInt(point.Y / cellSize), 0, VoxelGrid.Height - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt(point.Z / cellSize), 0, VoxelGrid.Depth - 1);

            if (cx == lastX && cy == lastY && cz == lastZ)
                continue;
            lastX = cx; lastY = cy; lastZ = cz;

            if (!haveEntryCell)
            {
                entryX = cx; entryY = cy; entryZ = cz;
                haveEntryCell = true;
            }

            if (_voxelGrid.TryGetCell(cx, cy, cz, out VoxelCell cell)
                && cell.Moisture >= _voxelGrid.MoistureVisibilityThreshold)
            {
                x = cx; y = cy; z = cz;
                return true;
            }
        }

        if (!haveEntryCell)
            return false;

        x = entryX; y = entryY; z = entryZ;
        return true;
    }

    // Standard slab-method ray/AABB intersection. Returns both the entry
    // and exit distances along the ray (entry clamped to 0 so a ray whose
    // origin already sits inside the grid still resolves), or false if the
    // ray misses the box entirely.
    private static bool TryIntersectAabb(Vector3 origin, Vector3 direction, Vector3 boundsMin, Vector3 boundsMax, out float tEntry, out float tExit)
    {
        float tMin = 0.0f;
        float tMax = float.PositiveInfinity;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            float min = boundsMin[axis];
            float max = boundsMax[axis];

            if (Mathf.Abs(d) < 1e-8f)
            {
                if (o < min || o > max)
                {
                    tEntry = 0.0f;
                    tExit = 0.0f;
                    return false;
                }
                continue;
            }

            float inv = 1.0f / d;
            float t1 = (min - o) * inv;
            float t2 = (max - o) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);

            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);

            if (tMin > tMax)
            {
                tEntry = 0.0f;
                tExit = 0.0f;
                return false;
            }
        }

        tEntry = tMin;
        tExit = tMax;
        return true;
    }
}
