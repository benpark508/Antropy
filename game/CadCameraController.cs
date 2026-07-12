using Godot;

public partial class CadCameraController : Camera3D
{
    private enum SnapView
    {
        Front,
        Top,
        Side
    }

    [Export] public Vector3 GridDimensions = new Vector3(20.0f, 20.0f, 20.0f);
    [Export] public float OrbitSensitivity = 0.3f;
    [Export] public float PanSensitivity = 0.0025f;
    [Export] public float ZoomStep = 1.5f;
    [Export] public float MinDistance = 2.0f;
    [Export] public float MaxDistance = 100.0f;

    private const float ReferenceDistance = 35.0f;
    private const float TopViewPitchDegrees = 89.9f;

    private Vector3 _target;
    private float _yaw = -45.0f;
    private float _pitch = -30.0f;
    private float _distance = ReferenceDistance;

    private bool _isOrbiting;
    private bool _isPanning;

    public override void _Ready()
    {
        _target = GridDimensions * 0.5f;
        // Always Perspective: view snaps only change yaw/pitch, never the
        // projection mode, so resuming orbit from a snapped view never
        // alters the render output.
        Projection = ProjectionType.Perspective;
        UpdateTransform();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            HandleMouseButton(mouseButton);
        }
        else if (@event is InputEventMouseMotion mouseMotion)
        {
            HandleMouseMotion(mouseMotion);
        }
        else if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            HandleKeySnap(keyEvent);
        }
    }

    private void HandleMouseButton(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Right:
                _isOrbiting = mouseButton.Pressed;
                GetViewport().SetInputAsHandled();
                break;

            case MouseButton.Middle:
                _isPanning = mouseButton.Pressed;
                GetViewport().SetInputAsHandled();
                break;

            case MouseButton.WheelUp when mouseButton.Pressed:
                Zoom(-ZoomStep);
                GetViewport().SetInputAsHandled();
                break;

            case MouseButton.WheelDown when mouseButton.Pressed:
                Zoom(ZoomStep);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion mouseMotion)
    {
        if (_isOrbiting)
        {
            _yaw -= mouseMotion.Relative.X * OrbitSensitivity;
            _pitch = Mathf.Clamp(_pitch + mouseMotion.Relative.Y * OrbitSensitivity, -88.5f, 88.5f);
            UpdateTransform();
        }
        else if (_isPanning)
        {
            Vector3 right = Basis.X;
            Vector3 up = Basis.Y;
            _target += (-right * mouseMotion.Relative.X + up * mouseMotion.Relative.Y)
                * PanSensitivity * _distance;
            UpdateTransform();
        }
    }

    private void Zoom(float amount)
    {
        _distance = Mathf.Clamp(_distance + amount, MinDistance, MaxDistance);
        UpdateTransform();
    }

    private void HandleKeySnap(InputEventKey keyEvent)
    {
        switch (keyEvent.Keycode)
        {
            case Key.Key1:
                SnapToView(SnapView.Front);
                GetViewport().SetInputAsHandled();
                break;

            case Key.Key2:
                SnapToView(SnapView.Top);
                GetViewport().SetInputAsHandled();
                break;

            case Key.Key3:
                SnapToView(SnapView.Side);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void SnapToView(SnapView view)
    {
        switch (view)
        {
            case SnapView.Front:
                _yaw = 0.0f;
                _pitch = 0.0f;
                break;

            case SnapView.Top:
                _yaw = 0.0f;
                _pitch = TopViewPitchDegrees;
                break;

            case SnapView.Side:
                _yaw = 90.0f;
                _pitch = 0.0f;
                break;
        }

        UpdateTransform();
    }

    private void UpdateTransform()
    {
        float yawRad = Mathf.DegToRad(_yaw);
        float pitchRad = Mathf.DegToRad(_pitch);

        Vector3 direction = new Vector3(
            Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
            Mathf.Sin(pitchRad),
            Mathf.Cos(pitchRad) * Mathf.Cos(yawRad)
        );

        Position = _target + direction * _distance;

        // Near-vertical pitch (top view) makes the default Up vector nearly
        // parallel to the look direction, which destabilizes LookAt. Swap
        // to a Forward-aligned up vector whenever we're close to that pole.
        Vector3 up = Mathf.Abs(_pitch) > 89.0f ? Vector3.Forward : Vector3.Up;
        LookAt(_target, up);
    }
}
