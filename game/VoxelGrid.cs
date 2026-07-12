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

    [Export] public float CellSize = 1.0f;
    [Export] public int MoistureVisibilityThreshold = 40;

    private VoxelCell[] _cells;

    public override void _Ready()
    {
        _cells = new VoxelCell[Width * Height * Depth];
        PopulateBaseline();
        SpawnDebugVisualization();
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

        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var cell = _cells[GetIndex(x, y, z)];
                    if (cell.Moisture < MoistureVisibilityThreshold)
                        continue;

                    var meshInstance = new MeshInstance3D
                    {
                        Mesh = boxMesh,
                        Position = new Vector3(x, y, z) * CellSize,
                        MaterialOverride = new StandardMaterial3D
                        {
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                            AlbedoColor = new Color(0.15f, 0.45f, 0.95f, cell.Moisture / 100.0f)
                        }
                    };

                    debugRoot.AddChild(meshInstance);
                }
            }
        }
    }
}
