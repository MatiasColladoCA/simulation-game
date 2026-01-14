using Godot;

public partial class OrbitalCamera : Camera3D
{
	// --- Configuración (Multiplicadores Relativos) ---
	// 1.3 = El radio + 30% de aire
	[Export] public float MinRadiusMultiplier = 0.03f; 
	// 5.0 = 5 veces el radio del planeta
	[Export] public float MaxRadiusMultiplier = 10.0f; 
	
	[Export] public float Sensitivity = 0.005f; 
	[Export] public float Smoothness = 10.0f;    

	// Estas ahora son privadas/internas, se calculan solas
	private float _minDistance; 
	private float _maxDistance; 
	private float _zoomStep; // ZoomSpeed dinámico

	// --- Estado Interno ---
	private float _targetDistance;
	private float _currentDistance;
	private Vector2 _targetRotation = Vector2.Zero; 
	private Vector2 _currentRotation = Vector2.Zero;
	private bool _isDragging = false;

	// Valor por defecto por si no se llama a Initialize (fallback a radio 100)
	public override void _Ready()
	{
		Initialize(100.0f); 
	}

	// --- NUEVO MÉTODO DE CONFIGURACIÓN ---
	public void Initialize(float planetRadius)
	{
		// 1. Calcular límites basados en el tamaño del planeta
		_minDistance = planetRadius * MinRadiusMultiplier;
		_maxDistance = planetRadius * MaxRadiusMultiplier;

		// 2. Ajustar la velocidad del zoom 
		// (Si el planeta es gigante, el zoom debe ser más rápido)
		_zoomStep = planetRadius * 0.1f; 

		// 3. Posición inicial (Un punto medio cómodo, ej: 1.5x)
		_targetDistance = planetRadius * 1.5f;
		_currentDistance = _targetDistance;

		// 4. Resetear rotación
		_targetRotation.Y = Mathf.DegToRad(45);
		_currentRotation = _targetRotation;
		
		UpdateCameraTransform();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				_isDragging = mb.Pressed;
				Input.MouseMode = _isDragging ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
			}

			if (mb.Pressed)
			{
				// Usamos el _zoomStep dinámico
				if (mb.ButtonIndex == MouseButton.WheelUp)
					_targetDistance -= _zoomStep;
				if (mb.ButtonIndex == MouseButton.WheelDown)
					_targetDistance += _zoomStep;
				
				// Usamos los límites dinámicos
				_targetDistance = Mathf.Clamp(_targetDistance, _minDistance, _maxDistance);
			}
		}

		if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_targetRotation.X -= mm.Relative.X * Sensitivity;
			_targetRotation.Y -= mm.Relative.Y * Sensitivity;
			_targetRotation.Y = Mathf.Clamp(_targetRotation.Y, -Mathf.Pi / 2 + 0.1f, Mathf.Pi / 2 - 0.1f);
		}
		
		// TOGGLE WIREFRAME (Tecla P)
		if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.P)
		{
			var vp = GetViewport();
			if (vp.DebugDraw == Viewport.DebugDrawEnum.Wireframe)
				vp.DebugDraw = Viewport.DebugDrawEnum.Disabled;
			else
				vp.DebugDraw = Viewport.DebugDrawEnum.Wireframe;
		}
	}

	public override void _Process(double delta)
	{
		float t = (float)delta * Smoothness;
		_currentDistance = Mathf.Lerp(_currentDistance, _targetDistance, t);
		_currentRotation.X = Mathf.Lerp(_currentRotation.X, _targetRotation.X, t);
		_currentRotation.Y = Mathf.Lerp(_currentRotation.Y, _targetRotation.Y, t);

		UpdateCameraTransform();
	}

	private void UpdateCameraTransform()
	{
		Vector3 position = new Vector3(0, 0, _currentDistance);
		position = position.Rotated(Vector3.Right, _currentRotation.Y);
		position = position.Rotated(Vector3.Up, _currentRotation.X);
		GlobalPosition = position;
		LookAt(Vector3.Zero, Vector3.Up);
	}
}
