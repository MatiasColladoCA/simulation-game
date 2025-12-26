using Godot;

public partial class FreeLookCamera : Camera3D
{
	[Export] public float Sensitivity = 0.005f;
	[Export] public float ZoomStep = 5.0f; // Metros por "scroll"
	[Export] public float SmoothSpeed = 10.0f; // Suavizado de movimiento

	private Vector3 _targetPosition;
	private Vector3 _targetRotation;
	private bool _isDragging = false;

	public override void _Ready()
	{
		// Posición inicial segura: Lejos del planeta (Radio 50 + 100 margen)
		GlobalPosition = new Vector3(0, 0, 150);
		// Mirar hacia el planeta (0,0,0)
		LookAt(Vector3.Zero, Vector3.Up);

		_targetPosition = GlobalPosition;
		_targetRotation = Rotation;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// 1. ROTACIÓN (Click Derecho + Arrastrar)
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.Right)
			{
				_isDragging = mb.Pressed;
				if (_isDragging) Input.MouseMode = Input.MouseModeEnum.Captured;
				else Input.MouseMode = Input.MouseModeEnum.Visible;
			}

			// 2. ZOOM (Rueda del Ratón) - Se mueve hacia donde miras
			if (mb.Pressed)
			{
				if (mb.ButtonIndex == MouseButton.WheelUp)
					_targetPosition -= Transform.Basis.Z * ZoomStep; // Hacia adelante
				if (mb.ButtonIndex == MouseButton.WheelDown)
					_targetPosition += Transform.Basis.Z * ZoomStep; // Hacia atrás
			}
		}

		if (@event is InputEventMouseMotion mm && _isDragging)
		{
			// Rotación Euler (Yaw/Pitch)
			_targetRotation.Y -= mm.Relative.X * Sensitivity;
			_targetRotation.X -= mm.Relative.Y * Sensitivity;
			
			// Clamp para no dar la vuelta completa verticalmente
			_targetRotation.X = Mathf.Clamp(_targetRotation.X, -Mathf.Pi / 2, Mathf.Pi / 2);
		}
	}

	public override void _Process(double delta)
	{
		// Interpolación para suavidad (Lerp)
		float t = (float)delta * SmoothSpeed;
		
		// Movimiento suave
		GlobalPosition = GlobalPosition.Lerp(_targetPosition, t);
		
		// Rotación suave
		Vector3 currentRot = Rotation;
		currentRot.X = Mathf.LerpAngle(currentRot.X, _targetRotation.X, t);
		currentRot.Y = Mathf.LerpAngle(currentRot.Y, _targetRotation.Y, t);
		currentRot.Z = 0; // Mantener horizonte nivelado
		Rotation = currentRot;
	}
}
