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

    // Carbon/Phosphorus a fungal cell must pay out of its own pre-tick
    // stock every tick just to stay alive. Paying both in full is what
    // "successfully consumed its survival resources" means below — if
    // either can't be afforded, the colony at that cell starves instead of
    // merely skipping growth for the tick.
    [Export] public int FungalCarbonCost = 2;
    [Export] public int FungalPhosphorusCost = 1;

    // Upper bound on spread probability per neighbor per tick, scaled down
    // by how favorable (wet + nutrient-rich) that neighbor actually is.
    [Export] public float FungalBaseSpreadChance = 0.15f;

    // Relative weight of a neighbor's moisture vs. its Carbon+Phosphorus
    // when scoring how attractive it is to colonize. Combined as a
    // weighted average in [0, 1] rather than multiplied, so a neighbor
    // that's rich in one but middling in the other still gets a fair
    // chance instead of being crushed to near-zero.
    [Export] public float FungalMoistureWeight = 0.6f;
    [Export] public float FungalNutrientWeight = 0.4f;

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

    private MultiMesh _debugMultiMesh;

    private RandomNumberGenerator _fungalRng;

    public override void _Ready()
    {
        _cells = new VoxelCell[Width * Height * Depth];
        _cellsBuffer = new VoxelCell[Width * Height * Depth];
        _fungalRng = new RandomNumberGenerator();
        _fungalRng.Randomize();
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

        // Fungal growth runs as its own pass rather than being folded into
        // the loop above: that loop's `if (moisture <= 0) continue` is a
        // valid short-circuit for water/diffusion (both are about THIS
        // cell's own moisture), but fungal survival depends only on this
        // cell's Carbon/Phosphorus stock, not its moisture — a colony
        // sitting in a voxel gravity just drained dry must still be
        // evaluated, so it needs an unconditional sweep of its own.
        SimulateFungalGrowth();

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

    // Resource-gated fungal expansion: every fungal cell first pays its
    // upkeep cost out of its own pre-tick Carbon/Phosphorus (read from
    // _cells) or starves, then — only if it paid — rolls a spread chance
    // against each of its 6 neighbors, written to _cellsBuffer. Runs as an
    // unconditional sweep over the whole grid, independent of the
    // moisture-gated water/diffusion loop above.
    private void SimulateFungalGrowth()
    {
        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = GetIndex(x, y, z);
                    if (!_cells[index].FungalPresence)
                        continue;

                    int carbon = _cells[index].Carbon;
                    int phosphorus = _cells[index].Phosphorus;

                    bool paidUpkeep = carbon >= FungalCarbonCost && phosphorus >= FungalPhosphorusCost;
                    if (!paidUpkeep)
                    {
                        // Starvation: couldn't afford survival cost this tick.
                        _cellsBuffer[index].FungalPresence = false;
                        continue;
                    }

                    _cellsBuffer[index].Carbon -= FungalCarbonCost;
                    _cellsBuffer[index].Phosphorus -= FungalPhosphorusCost;

                    TrySpreadToNeighbor(x - 1, y, z);
                    TrySpreadToNeighbor(x + 1, y, z);
                    TrySpreadToNeighbor(x, y - 1, z);
                    TrySpreadToNeighbor(x, y + 1, z);
                    TrySpreadToNeighbor(x, y, z - 1);
                    TrySpreadToNeighbor(x, y, z + 1);
                }
            }
        }
    }

    // Rolls a resource-weighted chance for a surviving fungal cell to
    // spread FungalPresence into one neighbor. Reads exclusively from
    // _cells so every source cell this tick scores neighbors against the
    // same pre-tick snapshot; only ever targets a neighbor that was NOT
    // already fungal in that snapshot, which is what keeps this safe to
    // call in any iteration order against a starvation pass that only ever
    // touches cells that WERE already fungal in the snapshot — the two
    // never write to the same cell, so there's no order-dependent race.
    private void TrySpreadToNeighbor(int nx, int ny, int nz)
    {
        if (!IsInBounds(nx, ny, nz))
            return;

        int neighborIndex = GetIndex(nx, ny, nz);
        if (_cells[neighborIndex].FungalPresence)
            return;

        int neighborMoisture = _cells[neighborIndex].Moisture;
        if (neighborMoisture <= 0)
            return;

        float moistureFactor = (float)neighborMoisture / MaxMoisture;
        float nutrientFactor = (_cells[neighborIndex].Carbon + _cells[neighborIndex].Phosphorus) / (float)(2 * MaxNutrient);
        float weight = (moistureFactor * FungalMoistureWeight + nutrientFactor * FungalNutrientWeight)
            / (FungalMoistureWeight + FungalNutrientWeight);

        float spreadChance = Mathf.Clamp(FungalBaseSpreadChance * weight, 0f, 1f);
        if (_fungalRng.Randf() < spreadChance)
        {
            _cellsBuffer[neighborIndex].FungalPresence = true;
        }
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

    // Single MultiMeshInstance3D standing in for the old 8,000-node
    // MeshInstance3D pool: one draw call and one shared material instead of
    // 8,000 of each. Per-cell transforms never change after spawn (grid
    // geometry is static), so they're written once here; only per-instance
    // color is touched on the simulation tick's visual update pass.
    private void SpawnDebugVisualization()
    {
        var boxMesh = new BoxMesh { Size = Vector3.One * CellSize * 0.9f };

        _debugMultiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = boxMesh,
            InstanceCount = _cells.Length
        };

        var multiMeshInstance = new MultiMeshInstance3D
        {
            Name = "DebugMoistureVisualization",
            Multimesh = _debugMultiMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                VertexColorUseAsAlbedo = true
            }
        };
        AddChild(multiMeshInstance);

        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = GetIndex(x, y, z);
                    var transform = new Transform3D(Basis.Identity, new Vector3(x, y, z) * CellSize);
                    _debugMultiMesh.SetInstanceTransform(index, transform);
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

            // MultiMesh instances have no per-instance Visible flag, so
            // "invisible" is expressed as alpha 0 against the shared
            // Alpha-transparency material rather than a toggled bool.
            bool visible = cell.Moisture >= MoistureVisibilityThreshold;
            float alpha = visible ? cell.Moisture / 100.0f : 0.0f;

            _debugMultiMesh.SetInstanceColor(i, new Color(0.15f, 0.45f, 0.95f, alpha));
        }
    }
}
