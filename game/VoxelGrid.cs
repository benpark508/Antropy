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
    public const int MaxNutrient = 100;

    [Export] public float CellSize = 1.0f;
    [Export] public int MoistureVisibilityThreshold = 40;

    // Interval between simulation ticks. Decoupling the CA update from
    // _Process's frame rate keeps flow speed constant regardless of FPS.
    [Export] public float SimulationTickRate = 0.1f;

    // Fraction of a cell's dissolved Carbon/Phosphorus that leaches out per
    // unit of moisture that physically flows out of the cell this tick (see
    // LeachNutrients). Not all nutrient bound in soil water is mobile enough
    // to leach in one tick, hence < 1.0.
    [Export] public float LeachingEfficiency = 0.5f;

    // Fraction of the Carbon/Phosphorus concentration gap between a cell and
    // a neighbor that equalizes per tick via chemical diffusion, independent
    // of bulk water flow (see DiffuseNutrients).
    [Export] public float NutrientDiffusionRate = 0.1f;

    // Minimum moisture required in BOTH a cell and its neighbor for chemical
    // diffusion to occur between them — nutrients need a wet medium to
    // migrate through even when no bulk water is flowing.
    [Export] public int MinMoistureForDiffusion = 1;

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

    // Runs one CA tick of water movement plus nutrient leaching/diffusion,
    // reading exclusively from _cells and writing exclusively to
    // _cellsBuffer, then swaps the buffers.
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

                    int cellCarbon = _cells[index].Carbon;
                    int cellPhosphorus = _cells[index].Phosphorus;

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

                            // Leaching: the water carries a proportional
                            // slice of this cell's dissolved nutrients with it.
                            LeachNutrients(index, belowIndex, cellCarbon, cellPhosphorus, downOutflow, moisture);
                        }
                    }

                    // Chemical diffusion runs every tick regardless of bulk
                    // flow above, against all six neighbors (including up,
                    // which gravity and equalization never target).
                    DiffuseNutrients(index, x, y, z, moisture);

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
                        int northIndex = GetIndex(x, y, z - 1);
                        _cellsBuffer[northIndex].Moisture += share;
                        LeachNutrients(index, northIndex, cellCarbon, cellPhosphorus, share, moisture);
                        distributed += share;
                    }
                    if (hasSouth)
                    {
                        int southIndex = GetIndex(x, y, z + 1);
                        _cellsBuffer[southIndex].Moisture += share;
                        LeachNutrients(index, southIndex, cellCarbon, cellPhosphorus, share, moisture);
                        distributed += share;
                    }
                    if (hasEast)
                    {
                        int eastIndex = GetIndex(x + 1, y, z);
                        _cellsBuffer[eastIndex].Moisture += share;
                        LeachNutrients(index, eastIndex, cellCarbon, cellPhosphorus, share, moisture);
                        distributed += share;
                    }
                    if (hasWest)
                    {
                        int westIndex = GetIndex(x - 1, y, z);
                        _cellsBuffer[westIndex].Moisture += share;
                        LeachNutrients(index, westIndex, cellCarbon, cellPhosphorus, share, moisture);
                        distributed += share;
                    }

                    _cellsBuffer[index].Moisture -= distributed;
                }
            }
        }

        // Horizontal spreading and diffusion don't check a destination's
        // headroom against concurrent inflow from its other neighbors, so
        // clamp as a safety net before the buffer becomes the new
        // authoritative state.
        for (int i = 0; i < _cellsBuffer.Length; i++)
        {
            _cellsBuffer[i].Moisture = Mathf.Clamp(_cellsBuffer[i].Moisture, 0, MaxMoisture);
            _cellsBuffer[i].Carbon = Mathf.Clamp(_cellsBuffer[i].Carbon, 0, MaxNutrient);
            _cellsBuffer[i].Phosphorus = Mathf.Clamp(_cellsBuffer[i].Phosphorus, 0, MaxNutrient);
        }

        (_cells, _cellsBuffer) = (_cellsBuffer, _cells);
        UpdateDebugVisualization();
    }

    // Moves a proportional slice of Carbon/Phosphorus from source to
    // destination alongside a bulk water transfer of `waterMoved` units out
    // of the source cell's original `totalMoisture`. Nutrients are treated
    // as uniformly dissolved through the cell's moisture, so the fraction of
    // moisture leaving the cell carries that same fraction of each nutrient,
    // damped by LeachingEfficiency.
    private void LeachNutrients(int sourceIndex, int destIndex, int sourceCarbon, int sourcePhosphorus, int waterMoved, int totalMoisture)
    {
        int carbonLeached = ComputeLeachedAmount(sourceCarbon, waterMoved, totalMoisture);
        if (carbonLeached > 0)
        {
            _cellsBuffer[sourceIndex].Carbon -= carbonLeached;
            _cellsBuffer[destIndex].Carbon += carbonLeached;
        }

        int phosphorusLeached = ComputeLeachedAmount(sourcePhosphorus, waterMoved, totalMoisture);
        if (phosphorusLeached > 0)
        {
            _cellsBuffer[sourceIndex].Phosphorus -= phosphorusLeached;
            _cellsBuffer[destIndex].Phosphorus += phosphorusLeached;
        }
    }

    private int ComputeLeachedAmount(int nutrientAmount, int waterMoved, int totalMoisture)
    {
        if (nutrientAmount <= 0 || waterMoved <= 0 || totalMoisture <= 0)
            return 0;

        float fraction = (float)waterMoved / totalMoisture;
        int leached = Mathf.RoundToInt(nutrientAmount * fraction * LeachingEfficiency);
        return Mathf.Clamp(leached, 0, nutrientAmount);
    }

    // Slowly equalizes Carbon/Phosphorus concentration against all six
    // neighbors regardless of bulk water movement this tick, gated by both
    // cells holding at least MinMoistureForDiffusion moisture to act as the
    // transport medium. Each cell only ever pushes toward a neighbor it is
    // MORE concentrated than, so a given pair transfers in at most one
    // direction per tick even though both ends independently evaluate it.
    private void DiffuseNutrients(int index, int x, int y, int z, int moisture)
    {
        TryDiffuseToNeighbor(index, moisture, x - 1, y, z);
        TryDiffuseToNeighbor(index, moisture, x + 1, y, z);
        TryDiffuseToNeighbor(index, moisture, x, y - 1, z);
        TryDiffuseToNeighbor(index, moisture, x, y + 1, z);
        TryDiffuseToNeighbor(index, moisture, x, y, z - 1);
        TryDiffuseToNeighbor(index, moisture, x, y, z + 1);
    }

    private void TryDiffuseToNeighbor(int index, int moisture, int nx, int ny, int nz)
    {
        if (!IsInBounds(nx, ny, nz))
            return;

        int neighborIndex = GetIndex(nx, ny, nz);
        int neighborMoisture = _cells[neighborIndex].Moisture;

        if (moisture < MinMoistureForDiffusion || neighborMoisture < MinMoistureForDiffusion)
            return;

        int carbonFlux = ComputeDiffusionFlux(_cells[index].Carbon, _cells[neighborIndex].Carbon);
        if (carbonFlux > 0)
        {
            _cellsBuffer[index].Carbon -= carbonFlux;
            _cellsBuffer[neighborIndex].Carbon += carbonFlux;
        }

        int phosphorusFlux = ComputeDiffusionFlux(_cells[index].Phosphorus, _cells[neighborIndex].Phosphorus);
        if (phosphorusFlux > 0)
        {
            _cellsBuffer[index].Phosphorus -= phosphorusFlux;
            _cellsBuffer[neighborIndex].Phosphorus += phosphorusFlux;
        }
    }

    private int ComputeDiffusionFlux(int sourceValue, int neighborValue)
    {
        int gradient = sourceValue - neighborValue;
        if (gradient <= 0)
            return 0;

        return Mathf.RoundToInt(gradient * NutrientDiffusionRate);
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
