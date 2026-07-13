using Godot;

public enum CellType
{
    Solid,
    Air
}

public struct VoxelCell
{
    public CellType Type;
    public float Water;
    public float Carbon;
    public float Phosphorus;
    public bool FungalPresence;
}

// A plain Node2D that owns the simulation exclusively -- it is not itself
// a renderer. It pushes per-instance transforms/colors into a sibling
// MultiMeshInstance2D (resolved via MultiMeshRendererPath) rather than
// inheriting from MultiMeshInstance2D directly, so the fluid/nutrient/
// fungal CA can be reasoned about, or one day tested, independently of
// whatever node happens to draw it.
public partial class VoxelGrid : Node2D
{
    // Cinematic 2D cross-section canvas: a flat Width x Height grid of
    // cells laid out natively in Godot's 2D screen space, rendered as
    // exactly Width * Height = 5,000 MultiMesh instances.
    public const int Width = 100;
    public const int Height = 50;

    // The top AirRows rows are open sky/atmosphere; everything below that
    // (rows 0..SurfaceRow-1) starts as Solid earth. SurfaceRow is also the
    // first Air row, i.e. the boundary the erase brush and rain brush both
    // reason about.
    public const int AirRows = 5;
    public const int SurfaceRow = Height - AirRows;

    public const float MaxWater = 100f;
    public const float MaxNutrient = 100f;

    // Native 2D tile pitch, in screen pixels, between adjacent instances --
    // both VoxelGrid's own MultiMesh placement and VoxelInteractor's mouse
    // picking divide/multiply by this shared value.
    [Export] public float TileSize = 16f;

    // Sibling/child MultiMeshInstance2D that actually draws the grid.
    // VoxelGrid configures and updates its Multimesh but never inherits
    // from it, keeping simulation and rendering as separately swappable
    // pieces.
    [Export] public NodePath MultiMeshRendererPath;

    private MultiMeshInstance2D _renderer;

    // Interval between simulation ticks. Decoupling the CA update from
    // _Process's frame rate keeps flow speed constant regardless of FPS.
    [Export] public float SimulationTickRate = 0.1f;

    // Ground Percolation (Solid -> Solid below): fraction of the water a
    // Solid cell COULD push downward this tick (capped by the cell below's
    // remaining capacity) that actually moves. Throttling this way turns an
    // instant vertical drain into a slow multi-tick seep through the soil
    // column (see SimulateSolidCell).
    [Export] public float PercolationRate = 0.2f;

    // Tunnel Seeping (Solid -> Air): a Solid cell with water directly below
    // OR beside open Air drips into that cavity so a carved tunnel's walls
    // visibly weep rather than instantly flooding the space around them.
    // Kept close to (but still slightly under) PercolationRate rather than
    // far below it -- at a much smaller value this was mathematically
    // happening but too faint to ever notice next to straight-down flow.
    [Export] public float TunnelSeepRate = 0.15f;

    // Lateral Soil Spread (Solid -> Solid, sideways): fraction of the
    // "excess above field capacity" gap between a Solid cell and a
    // less-saturated Solid neighbor that equalizes sideways per tick. This
    // is what makes rain fan out into a wedge as it falls through ordinary,
    // untouched soil, rather than travelling in a single straight vertical
    // column with zero horizontal spread until it happens to reach a
    // player-carved tunnel wall.
    [Export] public float LateralSpreadRate = 0.1f;

    // Water below this level clings to the soil via capillary retention and
    // is never eligible to percolate, seep, or spread further -- only the
    // excess above it can move. Without this, soil would drain toward
    // bedrock indefinitely instead of settling into a permanent damp
    // baseline the way real soil field capacity works (see
    // SimulateSolidCell).
    [Export] public float SoilFieldCapacity = 30f;

    // Open Fluid Pooling (Air <-> Air): fraction of the water-level gradient
    // between two adjacent, non-falling Air cells that equalizes per tick.
    // Deliberately much faster than PercolationRate/TunnelSeepRate — open
    // water finds a flat level in a couple of ticks, soil does not.
    [Export] public float PoolEqualizationRate = 0.5f;

    // Fraction of a Solid cell's dissolved Carbon/Phosphorus that leaches
    // out per unit of water that percolates out of it this tick (see
    // LeachNutrients). Not all nutrient bound in soil water is mobile
    // enough to leach in one tick, hence < 1.0.
    [Export] public float LeachingEfficiency = 0.5f;

    // Fraction of the Carbon/Phosphorus concentration gap between a Solid
    // cell and a Solid neighbor that equalizes per tick via chemical
    // diffusion, independent of bulk water flow. Nutrients diffuse through
    // the soil medium only -- an open Air pool never participates.
    [Export] public float NutrientDiffusionRate = 0.1f;

    // Minimum water required in BOTH a Solid cell and its Solid neighbor
    // for chemical diffusion to occur between them.
    [Export] public float MinWaterForDiffusion = 1f;

    // Carbon/Phosphorus a fungal cell must pay out of its own pre-tick
    // stock every tick just to stay alive. Paying both in full is what
    // "successfully consumed its survival resources" means below -- if
    // either can't be afforded, the colony at that cell starves instead of
    // merely skipping growth for the tick.
    [Export] public float FungalCarbonCost = 2f;
    [Export] public float FungalPhosphorusCost = 1f;

    // Upper bound on spread probability per neighbor per tick, scaled down
    // by how favorable (wet + nutrient-rich) that neighbor actually is.
    [Export] public float FungalBaseSpreadChance = 0.15f;

    // Relative weight of a neighbor's water vs. its Carbon+Phosphorus when
    // scoring how attractive it is to colonize. Combined as a weighted
    // average in [0, 1] rather than multiplied, so a neighbor that's rich
    // in one but middling in the other still gets a fair chance instead of
    // being crushed to near-zero.
    [Export] public float FungalMoistureWeight = 0.6f;
    [Export] public float FungalNutrientWeight = 0.4f;

    private VoxelCell[] _cells;

    // Write target for the tick currently being computed. Every read during
    // a tick goes through _cells (last tick's settled state) and every
    // write goes through _cellsBuffer, so a cell can never consume water
    // that was produced earlier in the same tick. The two arrays are
    // swapped wholesale once the tick finishes.
    private VoxelCell[] _cellsBuffer;

    private float _tickAccumulator;

    private RandomNumberGenerator _fungalRng;

    // Global accounting, recomputed every tick in SimulateFluidFlow's
    // existing clamp pass so tracking mass conservation costs no extra
    // full-array iteration.
    private float _totalWater;
    private float _totalCarbon;
    private float _totalPhosphorus;
    private int _fungalCellCount;

    // Which data channel the MultiMesh's per-instance color currently
    // represents. Toggled via Comma/Period/Slash in _Input so a developer
    // can inspect layers (nutrient, fungus) the default water view hides.
    private enum ColorMode { Water, Nutrient, Fungus }
    private ColorMode _colorMode = ColorMode.Water;

    public override void _Ready()
    {
        _renderer = GetNode<MultiMeshInstance2D>(MultiMeshRendererPath);
        _cells = new VoxelCell[Width * Height];
        _cellsBuffer = new VoxelCell[Width * Height];
        _fungalRng = new RandomNumberGenerator();
        _fungalRng.Randomize();
        PopulateBaseline();
        InitializeSoilNutrients();
        SpawnDebugVisualization();
    }

    public override void _Process(double delta)
    {
        _tickAccumulator += (float)delta;

        while (_tickAccumulator >= SimulationTickRate)
        {
            _tickAccumulator -= SimulationTickRate;
            SimulateFluidFlow();
        }
    }

    // Dev-only diagnostic hotkeys for switching what the MultiMesh's color
    // channel visualizes. Handled as a discrete key-press event (not
    // polled in _Process) since this is a one-shot mode switch, not a
    // hold-to-paint action like VoxelInteractor's brushes.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
            return;

        switch (keyEvent.Keycode)
        {
            case Key.Comma:
                _colorMode = ColorMode.Water;
                UpdateDebugVisualization();
                break;
            case Key.Period:
                _colorMode = ColorMode.Nutrient;
                UpdateDebugVisualization();
                break;
            case Key.Slash:
                _colorMode = ColorMode.Fungus;
                UpdateDebugVisualization();
                break;
        }
    }

    public static int GetIndex(int x, int y)
    {
        return x + (y * Width);
    }

    public static bool IsInBounds(int x, int y)
    {
        return x >= 0 && x < Width && y >= 0 && y < Height;
    }

    // The simulation's row index grows upward (y = 0 is bedrock, y = Height
    // - 1 is the top of the sky), but Godot's 2D screen space grows
    // downward (screen Y = 0 is the top of the viewport). These two static
    // helpers are the single, shared place that flips between the two, so
    // VoxelGrid's own instance placement and VoxelInteractor's mouse
    // picking can never drift out of sync with each other.
    public static float RowToScreenY(int row, float tileSize)
    {
        return (Height - 1 - row) * tileSize;
    }

    public static int ScreenYToRow(float screenY, float tileSize)
    {
        return Height - 1 - Mathf.FloorToInt(screenY / tileSize);
    }

    public bool TryGetCell(int x, int y, out VoxelCell cell)
    {
        if (!IsInBounds(x, y))
        {
            cell = default;
            return false;
        }

        cell = _cells[GetIndex(x, y)];
        return true;
    }

    public bool TrySetCell(int x, int y, VoxelCell cell)
    {
        if (!IsInBounds(x, y))
            return false;

        _cells[GetIndex(x, y)] = cell;
        return true;
    }

    // Runs one CA tick of the hybrid fluid engine plus nutrient
    // leaching/diffusion and fungal growth, reading exclusively from _cells
    // and writing exclusively to _cellsBuffer, then swaps the buffers.
    private void SimulateFluidFlow()
    {
        // Seed the write buffer with the current state so untouched fields
        // carry over unchanged; the passes below only apply deltas.
        System.Array.Copy(_cells, _cellsBuffer, _cells.Length);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, y);
                VoxelCell cell = _cells[index];

                if (cell.Type == CellType.Solid)
                    SimulateSolidCell(x, y, index, cell);
                else
                    SimulateAirCell(x, y, index, cell);
            }
        }

        // Fungal growth runs as its own pass rather than being folded into
        // the loop above: fungal survival depends only on a cell's
        // Carbon/Phosphorus stock, not its water, so a colony sitting in a
        // cell gravity just drained dry must still be evaluated.
        SimulateFungalGrowth();

        // Horizontal pooling/diffusion don't check a destination's headroom
        // against concurrent inflow from its other neighbors, so clamp as a
        // safety net before the buffer becomes the new authoritative state.
        // The global accounting totals are folded into this same pass since
        // it already visits every cell in its final, about-to-become-
        // authoritative form.
        float totalWater = 0f;
        float totalCarbon = 0f;
        float totalPhosphorus = 0f;
        int fungalCellCount = 0;

        for (int i = 0; i < _cellsBuffer.Length; i++)
        {
            _cellsBuffer[i].Water = Mathf.Clamp(_cellsBuffer[i].Water, 0f, MaxWater);
            _cellsBuffer[i].Carbon = Mathf.Clamp(_cellsBuffer[i].Carbon, 0f, MaxNutrient);
            _cellsBuffer[i].Phosphorus = Mathf.Clamp(_cellsBuffer[i].Phosphorus, 0f, MaxNutrient);

            totalWater += _cellsBuffer[i].Water;
            totalCarbon += _cellsBuffer[i].Carbon;
            totalPhosphorus += _cellsBuffer[i].Phosphorus;
            if (_cellsBuffer[i].FungalPresence)
                fungalCellCount++;
        }

        _totalWater = totalWater;
        _totalCarbon = totalCarbon;
        _totalPhosphorus = totalPhosphorus;
        _fungalCellCount = fungalCellCount;

        (_cells, _cellsBuffer) = (_cellsBuffer, _cells);
        UpdateDebugVisualization();
        UpdateVitalsDisplay();
    }

    // Ground Percolation + Tunnel Seeping. A Solid cell's water above field
    // capacity always tries to move into whatever sits below or beside it:
    // slowly into more Solid soil (percolation, straight down only), or
    // even more slowly into any open Air cavity it directly touches
    // (seeping, down or sideways). Chemical diffusion against Solid
    // neighbors runs unconditionally alongside it, using the cell's full
    // water content -- retention only gates bulk movement, not chemistry.
    private void SimulateSolidCell(int x, int y, int index, VoxelCell cell)
    {
        float water = cell.Water;
        if (water <= 0f)
            return;

        // Only water above field capacity is mobile; the retained portion
        // stays put regardless of what's around it, so a column of soil
        // settles into a permanent damp baseline instead of draining
        // itself dry over time.
        float movableWater = Mathf.Max(0f, water - SoilFieldCapacity);

        if (movableWater > 0f)
        {
            if (IsInBounds(x, y - 1))
            {
                int belowIndex = GetIndex(x, y - 1);
                VoxelCell below = _cells[belowIndex];
                float belowCapacity = MaxWater - below.Water;

                if (belowCapacity > 0f)
                {
                    if (below.Type == CellType.Solid)
                    {
                        float outflow = Mathf.Min(movableWater, belowCapacity) * PercolationRate;
                        if (outflow > 0f)
                        {
                            _cellsBuffer[index].Water -= outflow;
                            _cellsBuffer[belowIndex].Water += outflow;
                            LeachNutrients(index, belowIndex, cell.Carbon, cell.Phosphorus, outflow, water);
                        }
                    }
                    else
                    {
                        // Tunnel Seeping: this Solid cell has an open cavity
                        // beneath it (carved by the erase brush, or naturally
                        // exposed at the surface boundary) -- it weeps into the
                        // cavity instead of percolating into more soil.
                        float seep = Mathf.Min(movableWater, belowCapacity) * TunnelSeepRate;
                        if (seep > 0f)
                        {
                            _cellsBuffer[index].Water -= seep;
                            _cellsBuffer[belowIndex].Water += seep;
                        }
                    }
                }
            }

            // Lateral Tunnel Seeping: a carved cavity beside this cell
            // catches water too, not just one directly underneath --
            // otherwise a tunnel next to (rather than under) a draining
            // column would be invisible to that water, which isn't
            // physically sound once the soil beside an open cavity is
            // above field capacity.
            SeepLaterally(index, movableWater, x - 1, y);
            SeepLaterally(index, movableWater, x + 1, y);

            // Lateral Soil Spread: even with no carved tunnel anywhere
            // nearby, water above field capacity also equalizes sideways
            // against ordinary Solid neighbors, not just downward. This is
            // independent of (and additive with) the tunnel-seeping calls
            // above -- SeepLaterally and SpreadLaterallyThroughSoil target
            // the same two neighbor cells but are mutually exclusive per
            // neighbor by CellType, so a given neighbor is only ever
            // touched by whichever one actually applies to it.
            SpreadLaterallyThroughSoil(index, water, movableWater, x - 1, y);
            SpreadLaterallyThroughSoil(index, water, movableWater, x + 1, y);
        }

        DiffuseNutrients(index, x, y, water);
    }

    private void SeepLaterally(int index, float movableWater, int nx, int ny)
    {
        if (!IsInBounds(nx, ny))
            return;

        int neighborIndex = GetIndex(nx, ny);
        VoxelCell neighbor = _cells[neighborIndex];
        if (neighbor.Type != CellType.Air)
            return;

        float neighborCapacity = MaxWater - neighbor.Water;
        if (neighborCapacity <= 0f)
            return;

        float seep = Mathf.Min(movableWater, neighborCapacity) * TunnelSeepRate;
        if (seep <= 0f)
            return;

        _cellsBuffer[index].Water -= seep;
        _cellsBuffer[neighborIndex].Water += seep;
    }

    // Only the higher side of a pair ever produces a positive gradient
    // (compared on each side's *movable* water, not raw Water, so retained
    // moisture never gets pulled sideways any more than it can percolate
    // downward), so evaluating both directions independently can't
    // double-move the same water -- the same pattern EqualizeAirNeighbor
    // and TryDiffuseToNeighbor already use. Carries leached nutrients like
    // Ground Percolation does, since this is the same Solid-to-Solid bulk
    // water movement, just sideways instead of straight down.
    private void SpreadLaterallyThroughSoil(int index, float water, float movableWater, int nx, int ny)
    {
        if (!IsInBounds(nx, ny))
            return;

        int neighborIndex = GetIndex(nx, ny);
        VoxelCell neighbor = _cells[neighborIndex];
        if (neighbor.Type != CellType.Solid)
            return;

        float neighborMovable = Mathf.Max(0f, neighbor.Water - SoilFieldCapacity);
        float gradient = movableWater - neighborMovable;
        if (gradient <= 0f)
            return;

        float neighborCapacity = MaxWater - neighbor.Water;
        if (neighborCapacity <= 0f)
            return;

        float spread = Mathf.Min(gradient, neighborCapacity) * LateralSpreadRate;
        if (spread <= 0f)
            return;

        _cellsBuffer[index].Water -= spread;
        _cellsBuffer[neighborIndex].Water += spread;
        LeachNutrients(index, neighborIndex, _cells[index].Carbon, _cells[index].Phosphorus, spread, water);
    }

    // Open Fluid Pooling. Water cannot stay suspended in Air: it falls
    // instantly into an Air cell below with spare capacity, or -- if
    // blocked (Solid floor, or a saturated Air cell below) -- pools up and
    // rapidly equalizes horizontally with adjacent Air cells to converge on
    // a flat liquid level.
    private void SimulateAirCell(int x, int y, int index, VoxelCell cell)
    {
        float water = cell.Water;
        if (water <= 0f)
            return;

        bool fellThisTick = false;

        if (IsInBounds(x, y - 1))
        {
            int belowIndex = GetIndex(x, y - 1);
            VoxelCell below = _cells[belowIndex];

            if (below.Type == CellType.Air)
            {
                float belowCapacity = MaxWater - below.Water;
                if (belowCapacity > 0f)
                {
                    float fallAmount = Mathf.Min(water, belowCapacity);
                    if (fallAmount > 0f)
                    {
                        _cellsBuffer[index].Water -= fallAmount;
                        _cellsBuffer[belowIndex].Water += fallAmount;
                        fellThisTick = fallAmount >= water - 0.0001f;
                    }
                }
            }
        }

        // Water that fully drained downward this tick has nothing left to
        // pool with; anything blocked (partially or entirely) settles and
        // spreads sideways instead.
        if (fellThisTick)
            return;

        EqualizeAirNeighbor(index, x - 1, y, water);
        EqualizeAirNeighbor(index, x + 1, y, water);
    }

    private void EqualizeAirNeighbor(int index, int nx, int ny, float water)
    {
        if (!IsInBounds(nx, ny))
            return;

        int neighborIndex = GetIndex(nx, ny);
        VoxelCell neighbor = _cells[neighborIndex];
        if (neighbor.Type != CellType.Air)
            return;

        // Only the higher side of a pair ever produces a positive gradient,
        // so evaluating both directions independently can't double-move
        // the same water: the lower side's own call always sees a
        // non-positive gradient and returns immediately.
        float gradient = water - neighbor.Water;
        if (gradient <= 0f)
            return;

        float flux = gradient * PoolEqualizationRate;
        if (flux <= 0f)
            return;

        _cellsBuffer[index].Water -= flux;
        _cellsBuffer[neighborIndex].Water += flux;
    }

    // Surfaces the global accounting totals in the OS window title bar,
    // deliberately avoiding a Control/UI scene tree just to show four
    // numbers a developer needs for a quick mass-conservation sanity check.
    private void UpdateVitalsDisplay()
    {
        DisplayServer.WindowSetTitle(
            $"Antropy — Water: {_totalWater:F0} | Carbon: {_totalCarbon:F0} | Phosphorus: {_totalPhosphorus:F0} | Fungal Cells: {_fungalCellCount}");
    }

    // Moves a proportional slice of Carbon/Phosphorus from source to
    // destination alongside a bulk water transfer of `waterMoved` units out
    // of the source cell's original `totalWater`. Nutrients are treated as
    // uniformly dissolved through the cell's water, so the fraction of
    // water leaving the cell carries that same fraction of each nutrient,
    // damped by LeachingEfficiency.
    private void LeachNutrients(int sourceIndex, int destIndex, float sourceCarbon, float sourcePhosphorus, float waterMoved, float totalWater)
    {
        float carbonLeached = ComputeLeachedAmount(sourceCarbon, waterMoved, totalWater);
        if (carbonLeached > 0f)
        {
            _cellsBuffer[sourceIndex].Carbon -= carbonLeached;
            _cellsBuffer[destIndex].Carbon += carbonLeached;
        }

        float phosphorusLeached = ComputeLeachedAmount(sourcePhosphorus, waterMoved, totalWater);
        if (phosphorusLeached > 0f)
        {
            _cellsBuffer[sourceIndex].Phosphorus -= phosphorusLeached;
            _cellsBuffer[destIndex].Phosphorus += phosphorusLeached;
        }
    }

    private float ComputeLeachedAmount(float nutrientAmount, float waterMoved, float totalWater)
    {
        if (nutrientAmount <= 0f || waterMoved <= 0f || totalWater <= 0f)
            return 0f;

        float fraction = waterMoved / totalWater;
        float leached = nutrientAmount * fraction * LeachingEfficiency;
        return Mathf.Clamp(leached, 0f, nutrientAmount);
    }

    // Slowly equalizes Carbon/Phosphorus concentration against Solid
    // neighbors regardless of bulk water movement this tick. Nutrients
    // diffuse through the soil medium only -- TryDiffuseToNeighbor refuses
    // any Air neighbor, since an open water pool isn't a chemical transport
    // medium for dissolved soil nutrients in this model.
    private void DiffuseNutrients(int index, int x, int y, float water)
    {
        TryDiffuseToNeighbor(index, water, x - 1, y);
        TryDiffuseToNeighbor(index, water, x + 1, y);
        TryDiffuseToNeighbor(index, water, x, y - 1);
        TryDiffuseToNeighbor(index, water, x, y + 1);
    }

    private void TryDiffuseToNeighbor(int index, float water, int nx, int ny)
    {
        if (!IsInBounds(nx, ny))
            return;

        int neighborIndex = GetIndex(nx, ny);
        VoxelCell neighbor = _cells[neighborIndex];
        if (neighbor.Type != CellType.Solid)
            return;

        if (water < MinWaterForDiffusion || neighbor.Water < MinWaterForDiffusion)
            return;

        float carbonFlux = ComputeDiffusionFlux(_cells[index].Carbon, neighbor.Carbon);
        if (carbonFlux > 0f)
        {
            _cellsBuffer[index].Carbon -= carbonFlux;
            _cellsBuffer[neighborIndex].Carbon += carbonFlux;
        }

        float phosphorusFlux = ComputeDiffusionFlux(_cells[index].Phosphorus, neighbor.Phosphorus);
        if (phosphorusFlux > 0f)
        {
            _cellsBuffer[index].Phosphorus -= phosphorusFlux;
            _cellsBuffer[neighborIndex].Phosphorus += phosphorusFlux;
        }
    }

    private float ComputeDiffusionFlux(float sourceValue, float neighborValue)
    {
        float gradient = sourceValue - neighborValue;
        if (gradient <= 0f)
            return 0f;

        return gradient * NutrientDiffusionRate;
    }

    // Resource-gated fungal expansion: every fungal cell first pays its
    // upkeep cost out of its own pre-tick Carbon/Phosphorus (read from
    // _cells) or starves, then -- only if it paid -- rolls a spread chance
    // against each of its 4 neighbors, written to _cellsBuffer.
    private void SimulateFungalGrowth()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, y);
                if (!_cells[index].FungalPresence)
                    continue;

                float carbon = _cells[index].Carbon;
                float phosphorus = _cells[index].Phosphorus;

                bool paidUpkeep = carbon >= FungalCarbonCost && phosphorus >= FungalPhosphorusCost;
                if (!paidUpkeep)
                {
                    _cellsBuffer[index].FungalPresence = false;
                    continue;
                }

                _cellsBuffer[index].Carbon -= FungalCarbonCost;
                _cellsBuffer[index].Phosphorus -= FungalPhosphorusCost;

                TrySpreadToNeighbor(x - 1, y);
                TrySpreadToNeighbor(x + 1, y);
                TrySpreadToNeighbor(x, y - 1);
                TrySpreadToNeighbor(x, y + 1);
            }
        }
    }

    // Rolls a resource-weighted chance for a surviving fungal cell to
    // spread FungalPresence into one neighbor. Reads exclusively from
    // _cells so every source cell this tick scores neighbors against the
    // same pre-tick snapshot; only ever targets a neighbor that was NOT
    // already fungal in that snapshot, which is what keeps this safe to
    // call in any iteration order against a starvation pass that only ever
    // touches cells that WERE already fungal -- the two never write to the
    // same cell, so there's no order-dependent race.
    private void TrySpreadToNeighbor(int nx, int ny)
    {
        if (!IsInBounds(nx, ny))
            return;

        int neighborIndex = GetIndex(nx, ny);
        if (_cells[neighborIndex].FungalPresence)
            return;

        float neighborWater = _cells[neighborIndex].Water;
        if (neighborWater <= 0f)
            return;

        float moistureFactor = neighborWater / MaxWater;
        float nutrientFactor = (_cells[neighborIndex].Carbon + _cells[neighborIndex].Phosphorus) / (2f * MaxNutrient);
        float weight = (moistureFactor * FungalMoistureWeight + nutrientFactor * FungalNutrientWeight)
            / (FungalMoistureWeight + FungalNutrientWeight);

        float spreadChance = Mathf.Clamp(FungalBaseSpreadChance * weight, 0f, 1f);
        if (_fungalRng.Randf() < spreadChance)
        {
            _cellsBuffer[neighborIndex].FungalPresence = true;
        }
    }

    // Rows 0..SurfaceRow-1 start as dry Solid earth; rows SurfaceRow..
    // Height-1 start as empty Air (sky). Water/Carbon/Phosphorus start at
    // zero everywhere -- InitializeSoilNutrients lays down the ecological
    // strata immediately after this, and the interactive brushes exist
    // precisely so a developer can rain water into a clean, otherwise-inert
    // world and watch it percolate/pool/seep.
    private void PopulateBaseline()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                var cell = new VoxelCell
                {
                    Type = y >= SurfaceRow ? CellType.Air : CellType.Solid,
                    Water = 0f,
                    Carbon = 0f,
                    Phosphorus = 0f,
                    FungalPresence = false
                };

                _cells[GetIndex(x, y)] = cell;
            }
        }
    }

    // Sculpts Carbon/Phosphorus into three ecological bands across the
    // Solid earth (Air rows are left untouched at zero): a rich organic
    // layer just under the surface, mineral-rich pockets down at bedrock,
    // and a lean, neutral layer in between.
    private void InitializeSoilNutrients()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        for (int x = 0; x < Width; x++)
        {
            // Top 3 Solid rows: rich decomposing-organic-matter carbon blanket.
            for (int y = SurfaceRow - 3; y < SurfaceRow; y++)
            {
                int index = GetIndex(x, y);
                var cell = _cells[index];
                cell.Carbon = rng.RandfRange(60f, 80f);
                _cells[index] = cell;
            }

            // Middle Solid rows: lean, neutral zone -- no organic or
            // mineral content until the phosphorus pockets below are
            // stamped in.
            for (int y = 6; y < SurfaceRow - 3; y++)
            {
                int index = GetIndex(x, y);
                var cell = _cells[index];
                cell.Carbon = 0f;
                cell.Phosphorus = 0f;
                _cells[index] = cell;
            }
        }

        ScatterPhosphorusPockets(rng);
    }

    // The bottom 6 Solid rows (y = 0..5) start Phosphorus-neutral, then a
    // handful of randomly centered clusters are stamped over them -- each a
    // small radius-limited footprint with a randomized intensity and radial
    // falloff, blended via Max where clusters overlap -- so phosphorus
    // reads as scattered mineral deposits rather than a uniform layer.
    private void ScatterPhosphorusPockets(RandomNumberGenerator rng)
    {
        const int LowerRowStart = 0;
        const int LowerRowEnd = 5;
        const int ClusterCount = 6;
        const float ClusterRadius = 6.0f;

        for (int y = LowerRowStart; y <= LowerRowEnd; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, y);
                var cell = _cells[index];
                cell.Phosphorus = 0f;
                _cells[index] = cell;
            }
        }

        for (int c = 0; c < ClusterCount; c++)
        {
            int centerX = rng.RandiRange(0, Width - 1);
            int centerY = rng.RandiRange(LowerRowStart, LowerRowEnd);

            for (int y = LowerRowStart; y <= LowerRowEnd; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float distance = new Vector2(x - centerX, y - centerY).Length();
                    if (distance > ClusterRadius)
                        continue;

                    float falloff = 1.0f - (distance / ClusterRadius);
                    float deposit = rng.RandfRange(60f, 100f) * falloff;

                    int index = GetIndex(x, y);
                    var cell = _cells[index];
                    cell.Phosphorus = Mathf.Max(cell.Phosphorus, deposit);
                    _cells[index] = cell;
                }
            }
        }
    }

    // Configures the referenced MultiMeshInstance2D's Multimesh to draw the
    // whole Width x Height grid (one instance per cell) in a single draw
    // call. Per-cell transforms never change after spawn (grid geometry is
    // static), so they're written once here; only per-instance color is
    // touched on the simulation tick's visual update. 2D CanvasItem
    // rendering alpha-blends vertex colors by default, so unlike the old
    // 3D path this needs no explicit material override for transparency.
    private void SpawnDebugVisualization()
    {
        var quadMesh = new QuadMesh { Size = Vector2.One * TileSize * 0.95f };

        _renderer.Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            Mesh = quadMesh,
            InstanceCount = _cells.Length
        };

        // QuadMesh is centered on its own local origin, but VoxelInteractor's
        // picking treats cell (x, y) as occupying the whole TileSize bucket
        // starting at that grid line -- so each instance needs a half-tile
        // offset on both axes to land its visual center in the middle of
        // the bucket it's picked from, not on the bucket's corner.
        float halfTile = TileSize * 0.5f;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, y);
                var position = new Vector2(x * TileSize + halfTile, RowToScreenY(y, TileSize) + halfTile);
                var transform = new Transform2D(0f, position);
                _renderer.Multimesh.SetInstanceTransform2D(index, transform);
            }
        }

        UpdateDebugVisualization();
    }

    private void UpdateDebugVisualization()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, y);
                _renderer.Multimesh.SetInstanceColor(index, ComputeInstanceColor(_cells[index], y));
            }
        }
    }

    // Desaturated brown earth, fully opaque -- Solid ground is now the
    // structural terrain of the cross-section, not a translucent debug
    // ghost, so it reads as solid dirt at every water level.
    private static readonly Color BaselineSoilColor = new Color(0.36f, 0.27f, 0.17f, 1.0f);

    // Damp-soil endpoint for the Water ColorMode's Solid gradient -- a
    // deep, near-black wet earth brown. A Solid cell darkens continuously
    // from BaselineSoilColor toward this as its Water climbs toward
    // MaxWater, so groundwater saturation reads at a glance without a
    // separate blue "wet" overlay (that overlay is reserved for open Air
    // pools -- see WaterPoolColor below).
    private static readonly Color DampSoilColor = new Color(0.16f, 0.10f, 0.05f, 1.0f);

    // Faint atmosphere tint for empty Air cells within the original 5-row
    // sky band, so the top of the canvas still reads as "outside" instead
    // of pure void. Any other empty Air cell (a carved tunnel) renders
    // fully transparent instead, since it's a cavity within the earth, not
    // sky.
    private static readonly Color SkyColor = new Color(0.65f, 0.85f, 1.0f, 0.15f);
    private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);

    // Open Fluid Pooling endpoints -- a crisp, opaque solid blue that
    // deepens as an Air cell's pool fills toward MaxWater, with alpha
    // ramping alongside it to additionally read a shallow puddle as more
    // translucent than a brimming pool.
    private static readonly Color WaterPoolColor = new Color(0.1f, 0.45f, 0.95f, 1.0f);
    private static readonly Color DeepWaterPoolColor = new Color(0.05f, 0.2f, 0.6f, 1.0f);

    // Colors a single instance according to the active diagnostic
    // ColorMode, dispatching on CellType so Solid earth and open Air pools
    // never get rendered with the other's visual language.
    private Color ComputeInstanceColor(VoxelCell cell, int y)
    {
        switch (_colorMode)
        {
            case ColorMode.Nutrient:
            {
                if (cell.Type != CellType.Solid)
                    return GetInactiveColor(cell, y);

                float density = Mathf.Clamp((cell.Carbon + cell.Phosphorus) / (2f * MaxNutrient), 0f, 1f);
                if (density <= 0f)
                    return BaselineSoilColor;

                Color blended = new Color(1.0f, 0.55f, 0.0f).Lerp(new Color(1.0f, 0.0f, 0.0f), density);
                return new Color(blended.R, blended.G, blended.B, 1.0f);
            }

            case ColorMode.Fungus:
                return cell.FungalPresence
                    ? new Color(0.1f, 1.0f, 0.2f, 1.0f)
                    : GetInactiveColor(cell, y);

            case ColorMode.Water:
            default:
                return ComputeWaterModeColor(cell, y);
        }
    }

    // Shared fallback for a cell that has nothing to show in the active
    // mode: Solid ground always falls back to the baseline ghost-soil
    // brown, while Air falls back to a faint sky tint in the original sky
    // band or full transparency anywhere else (a carved-out cavity).
    private Color GetInactiveColor(VoxelCell cell, int y)
    {
        if (cell.Type == CellType.Solid)
            return BaselineSoilColor;

        return y >= SurfaceRow ? SkyColor : Transparent;
    }

    // ColorMode.Water: Solid cells render the damp-darkening gradient at
    // all times (there's always a saturation value to show); Air cells
    // render nothing until they hold water, then switch to a crisp opaque
    // blue pool whose depth color and alpha both scale with fill fraction
    // to communicate partial pool volume.
    private Color ComputeWaterModeColor(VoxelCell cell, int y)
    {
        if (cell.Type == CellType.Solid)
        {
            float saturation = Mathf.Clamp(cell.Water / MaxWater, 0f, 1f);
            Color blended = BaselineSoilColor.Lerp(DampSoilColor, saturation);
            return new Color(blended.R, blended.G, blended.B, 1.0f);
        }

        if (cell.Water <= 0f)
            return GetInactiveColor(cell, y);

        float fill = Mathf.Clamp(cell.Water / MaxWater, 0f, 1f);
        Color pool = WaterPoolColor.Lerp(DeepWaterPoolColor, fill);
        float alpha = Mathf.Lerp(0.55f, 1.0f, fill);
        return new Color(pool.R, pool.G, pool.B, alpha);
    }
}
