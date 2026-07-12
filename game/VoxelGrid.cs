using Godot;

public struct VoxelCell
{
    public int Moisture;
    public int Carbon;
    public int Phosphorus;
    public bool FungalPresence;
}

public partial class VoxelGrid : Node3D
{
    public const int Width = 20;
    public const int Height = 20;
    public const int Depth = 20;
    public const int MaxMoisture = 100;

    [Export] public float CellSize = 1.0f;
    [Export] public int MoistureVisibilityThreshold = 40;

    // Interval between simulation ticks. Decoupling the CA update from
    // _Process's frame rate keeps flow speed constant regardless of FPS.
    [Export] public float SimulationTickRate = 0.1f;

    private VoxelCell[] _cells;

    // Write target for the tick currently being computed. Every read during
    // a tick goes through _cells (last tick's settled state) and every write
    // goes through _cellsBuffer, so a cell can never consume moisture that
    // was produced earlier in the same tick. Without this, iterating the
    // grid in a fixed x/y/z order would let flow "chain" arbitrarily far in
    // one tick whenever it happened to run downhill/downstream of the loop
    // direction. The two arrays are swapped wholesale once the tick finishes.
    private VoxelCell[] _cellsBuffer;

    private float _tickAccumulator;

    private MeshInstance3D[] _debugInstances;

    public override void _Ready()
    {
        _cells = new VoxelCell[Width * Height * Depth];
        _cellsBuffer = new VoxelCell[Width * Height * Depth];
        PopulateBaseline();
        SpawnDebugVisualization();
    }

    public override void _Process(double delta)
    {
        _tickAccumulator += (float)delta;

        while (_tickAccumulator >= SimulationTickRate)
        {
            _tickAccumulator -= SimulationTickRate;
            SimulateWaterFlow();
        }
    }

    public static int GetIndex(int x, int y, int z)
    {
        return x + (y * Width) + (z * Width * Height);
    }

    public static bool IsInBounds(int x, int y, int z)
    {
        return x >= 0 && x < Width
            && y >= 0 && y < Height
            && z >= 0 && z < Depth;
    }

    public bool TryGetCell(int x, int y, int z, out VoxelCell cell)
    {
        if (!IsInBounds(x, y, z))
        {
            cell = default;
            return false;
        }

        cell = _cells[GetIndex(x, y, z)];
        return true;
    }

    public bool TrySetCell(int x, int y, int z, VoxelCell cell)
    {
        if (!IsInBounds(x, y, z))
            return false;

        _cells[GetIndex(x, y, z)] = cell;
        return true;
    }

    // Runs one CA tick of water movement, reading exclusively from _cells
    // and writing exclusively to _cellsBuffer, then swaps the buffers.
    private void SimulateWaterFlow()
    {
        // Seed the write buffer with the current state so untouched fields
        // (Carbon, Phosphorus, FungalPresence, and any moisture nobody
        // moves) carry over unchanged; the loop below only applies deltas.
        System.Array.Copy(_cells, _cellsBuffer, _cells.Length);

        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = GetIndex(x, y, z);
                    int moisture = _cells[index].Moisture;
                    if (moisture <= 0)
                        continue;

                    int downOutflow = 0;
                    bool hasBelow = IsInBounds(x, y - 1, z);

                    if (hasBelow)
                    {
                        int belowIndex = GetIndex(x, y - 1, z);
                        int belowCapacity = MaxMoisture - _cells[belowIndex].Moisture;

                        if (belowCapacity > 0)
                        {
                            downOutflow = Mathf.Min(moisture, belowCapacity);
                            _cellsBuffer[index].Moisture -= downOutflow;
                            _cellsBuffer[belowIndex].Moisture += downOutflow;
                        }
                    }

                    int remaining = moisture - downOutflow;
                    if (remaining <= 0)
                        continue;

                    // Path down is either off the grid or the cell beneath
                    // is saturated: bleed the leftover out to whichever
                    // horizontal neighbors exist, split evenly.
                    bool hasNorth = IsInBounds(x, y, z - 1);
                    bool hasSouth = IsInBounds(x, y, z + 1);
                    bool hasEast = IsInBounds(x + 1, y, z);
                    bool hasWest = IsInBounds(x - 1, y, z);

                    int neighborCount = (hasNorth ? 1 : 0) + (hasSouth ? 1 : 0)
                        + (hasEast ? 1 : 0) + (hasWest ? 1 : 0);
                    if (neighborCount == 0)
                        continue;

                    int share = remaining / neighborCount;
                    if (share <= 0)
                        continue;

                    int distributed = 0;

                    if (hasNorth)
                    {
                        _cellsBuffer[GetIndex(x, y, z - 1)].Moisture += share;
                        distributed += share;
                    }
                    if (hasSouth)
                    {
                        _cellsBuffer[GetIndex(x, y, z + 1)].Moisture += share;
                        distributed += share;
                    }
                    if (hasEast)
                    {
                        _cellsBuffer[GetIndex(x + 1, y, z)].Moisture += share;
                        distributed += share;
                    }
                    if (hasWest)
                    {
                        _cellsBuffer[GetIndex(x - 1, y, z)].Moisture += share;
                        distributed += share;
                    }

                    _cellsBuffer[index].Moisture -= distributed;
                }
            }
        }

        // Horizontal spreading doesn't check a destination's headroom
        // against concurrent inflow from its other neighbors, so clamp as a
        // safety net before the buffer becomes the new authoritative state.
        for (int i = 0; i < _cellsBuffer.Length; i++)
        {
            int clamped = Mathf.Clamp(_cellsBuffer[i].Moisture, 0, MaxMoisture);
            _cellsBuffer[i].Moisture = clamped;
        }

        (_cells, _cellsBuffer) = (_cellsBuffer, _cells);
        UpdateDebugVisualization();
    }

    private void PopulateBaseline()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var cell = new VoxelCell();

                    if (y == 0)
                    {
                        // Soil floor: saturated moisture, moderate organic content.
                        cell.Moisture = rng.RandiRange(80, 100);
                        cell.Carbon = rng.RandiRange(30, 60);
                        cell.Phosphorus = rng.RandiRange(30, 60);
                        cell.FungalPresence = rng.Randf() < 0.35f;
                    }
                    else
                    {
                        cell.Moisture = rng.RandiRange(0, 40);
                        cell.Carbon = rng.RandiRange(0, 100);
                        cell.Phosphorus = rng.RandiRange(0, 100);
                        cell.FungalPresence = false;
                    }

                    _cells[GetIndex(x, y, z)] = cell;
                }
            }
        }
    }

    private void SpawnDebugVisualization()
    {
        var debugRoot = new Node3D { Name = "DebugMoistureVisualization" };
        AddChild(debugRoot);

        var boxMesh = new BoxMesh { Size = Vector3.One * CellSize * 0.9f };
        _debugInstances = new MeshInstance3D[_cells.Length];

        // One instance per grid cell, all sharing the same BoxMesh resource.
        // Ticks toggle Visible/AlbedoColor on these existing nodes instead of
        // tearing down and rebuilding the tree every SimulationTickRate.
        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var meshInstance = new MeshInstance3D
                    {
                        Mesh = boxMesh,
                        Position = new Vector3(x, y, z) * CellSize,
                        MaterialOverride = new StandardMaterial3D
                        {
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
                        }
                    };

                    debugRoot.AddChild(meshInstance);
                    _debugInstances[GetIndex(x, y, z)] = meshInstance;
                }
            }
        }

        UpdateDebugVisualization();
    }

    private void UpdateDebugVisualization()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            var meshInstance = _debugInstances[i];

            bool visible = cell.Moisture >= MoistureVisibilityThreshold;
            meshInstance.Visible = visible;

            if (visible)
            {
                var material = (StandardMaterial3D)meshInstance.MaterialOverride;
                material.AlbedoColor = new Color(0.15f, 0.45f, 0.95f, cell.Moisture / 100.0f);
            }
        }
    }
}
