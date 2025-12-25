using Godot;

public partial class OrbitalCamera : Camera3D
{
	// --- Configuración ---
	[Export] public float MinDistance = 60.0f;  // No atravesar el suelo (50 radio + 10 margen)
	[Export] public float MaxDistance = 300.0f; // No perderse en el espacio
	[Export] public float Sensitivity = 0.005f; // Velocidad de giro
	[Export] public float ZoomSpeed = 10.0f;    // Velocidad de zoom
	[Export] public float Smoothness = 10.0f;   // Suavizado (Lerp)

	// --- Estado Interno ---
	private float _targetDistance = 150.0f;
	private float _currentDistance = 150.0f;
	
	// X = Ángulo Horizontal (Yaw), Y = Ángulo Vertical (Pitch)
	private Vector2 _targetRotation = Vector2.Zero; 
	private Vector2 _currentRotation = Vector2.Zero;
	
	private bool _isDragging = false;

	public override void _Ready()
	{
		// Inicializar mirando desde una posición cómoda
		_targetRotation.Y = Mathf.DegToRad(45); // Un poco elevado
		UpdateCameraTransform();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// 1. ROTACIÓN (Click Derecho + Arrastrar)
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				_isDragging = mb.Pressed;
				Input.MouseMode = _isDragging ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
			}

			// 2. ZOOM (Rueda del Ratón)
			if (mb.Pressed)
			{
				if (mb.ButtonIndex == MouseButton.WheelUp)
					_targetDistance -= ZoomSpeed;
				if (mb.ButtonIndex == MouseButton.WheelDown)
					_targetDistance += ZoomSpeed;
				
				// Clamp para no chocar ni irse lejos
				_targetDistance = Mathf.Clamp(_targetDistance, MinDistance, MaxDistance);
			}
		}

		// Movimiento del ratón
		if (@event is InputEventMouseMotion mm && _isDragging)
		{
			// Invertimos X para que se sienta natural (arrastrar el mundo)
			_targetRotation.X -= mm.Relative.X * Sensitivity;
			_targetRotation.Y -= mm.Relative.Y * Sensitivity;

			// Evitar dar la vuelta completa por arriba/abajo (Gimbal Lock)
			// Limitamos a casi -90 y 90 grados
			_targetRotation.Y = Mathf.Clamp(_targetRotation.Y, -Mathf.Pi / 2 + 0.1f, Mathf.Pi / 2 - 0.1f);
		}
	}

	public override void _Process(double delta)
	{
		// Interpolación (Suavizado)
		float t = (float)delta * Smoothness;
		
		_currentDistance = Mathf.Lerp(_currentDistance, _targetDistance, t);
		_currentRotation.X = Mathf.Lerp(_currentRotation.X, _targetRotation.X, t);
		_currentRotation.Y = Mathf.Lerp(_currentRotation.Y, _targetRotation.Y, t);

		UpdateCameraTransform();
	}

	private void UpdateCameraTransform()
	{
		// --- MATEMÁTICA ORBITAL ---
		// 1. Empezamos en el origen (0,0,distance)
		Vector3 position = new Vector3(0, 0, _currentDistance);
		
		// 2. Rotamos primero en el eje X (Elevación/Pitch)
		position = position.Rotated(Vector3.Right, _currentRotation.Y);
		
		// 3. Rotamos luego en el eje Y (Azimut/Yaw)
		// Nota: Usamos Vector3.Up global para que la rotación sea sobre el eje del planeta
		position = position.Rotated(Vector3.Up, _currentRotation.X);

		// 4. Aplicamos posición
		GlobalPosition = position;
		
		// 5. Miramos siempre al centro (0,0,0)
		LookAt(Vector3.Zero, Vector3.Up);
	}
}
