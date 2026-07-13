using Godot;

// 2D side-scroller camera for the cross-section canvas. Drives a native
// Camera2D: Right-Click drag and WASD/Arrow keys both push a target
// position that Position smoothly chases every frame, and the mouse wheel
// pushes a target zoom that Zoom smoothly chases the same way.
public partial class FarmCameraController : Camera2D
{
    // World-space size (in pixels) of the farm this camera starts centered
    // on -- kept in sync with VoxelGrid.Width/Height * TileSize rather than
    // referencing VoxelGrid directly, so this script stays a standalone,
    // reusable camera rig.
    [Export] public Vector2 GridPixelSize = new Vector2(1600f, 800f);

    [Export] public float KeyboardPanSpeed = 800.0f;
    [Export] public float DragPanSensitivity = 1.0f;
    [Export] public float PanSmoothing = 10.0f;

    [Export] public float ZoomStep = 0.1f;
    [Export] public float MinZoom = 0.2f;
    [Export] public float MaxZoom = 8.0f;
    [Export] public float ZoomSmoothing = 10.0f;

    private Vector2 _targetPosition;
    private Vector2 _targetZoom;
    private bool _isDragPanning;

    public override void _Ready()
    {
        Position = GridPixelSize * 0.5f;
        _targetPosition = Position;
        _targetZoom = Zoom;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            HandleMouseButton(mouseButton);
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isDragPanning)
        {
            // Drag delta is divided by the current zoom so panning tracks
            // the cursor 1:1 in world space regardless of zoom level.
            _targetPosition -= mouseMotion.Relative * DragPanSensitivity / _targetZoom.X;
            GetViewport().SetInputAsHandled();
        }
    }

    private void HandleMouseButton(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Right:
                _isDragPanning = mouseButton.Pressed;
                GetViewport().SetInputAsHandled();
                break;

            case MouseButton.WheelUp when mouseButton.Pressed:
                ApplyZoomDelta(ZoomStep);
                GetViewport().SetInputAsHandled();
                break;

            case MouseButton.WheelDown when mouseButton.Pressed:
                ApplyZoomDelta(-ZoomStep);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    // Camera2D.Zoom > 1 magnifies (zoomed in, e.g. a single tile filling
    // the screen); < 1 shrinks (zoomed out, e.g. the whole 100x50 farm).
    // Uniform X/Y keeps tiles square at every zoom level.
    private void ApplyZoomDelta(float amount)
    {
        float clamped = Mathf.Clamp(_targetZoom.X + amount, MinZoom, MaxZoom);
        _targetZoom = new Vector2(clamped, clamped);
    }

    public override void _Process(double delta)
    {
        HandleKeyboardPan((float)delta);

        Position = Position.Lerp(_targetPosition, (float)delta * PanSmoothing);
        Zoom = Zoom.Lerp(_targetZoom, (float)delta * ZoomSmoothing);
    }

    private void HandleKeyboardPan(float delta)
    {
        Vector2 direction = Vector2.Zero;

        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))
            direction.Y -= 1f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))
            direction.Y += 1f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))
            direction.X -= 1f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right))
            direction.X += 1f;

        if (direction == Vector2.Zero)
            return;

        // Pan speed scales inversely with zoom so keyboard panning also
        // covers a consistent amount of *screen* space regardless of how
        // zoomed in the camera currently is.
        _targetPosition += direction.Normalized() * KeyboardPanSpeed * delta / _targetZoom.X;
    }
}
