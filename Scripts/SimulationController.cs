using Godot;
using System.Threading.Tasks;
using System;
using System.Threading.Tasks;

public partial class SimulationController : Node
{
	// Dependencias de Escena (Arrastrar en Editor)
	[Export] public TerrainBaker Baker;
	[Export] public AgentSystem AgentSys;
	[Export] public PlanetRender PlanetRenderer; // Tu script existente
	[Export] public SimulationUI UI;

	private RenderingDevice _rd;
	private bool _isRunning = false;


private LineEdit _consoleInput;

// --- REEMPLAZAR EL MÉTODO _Ready COMPLETO ---
	public override async void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();
		
		// 1. Validar dependencias
		if (Baker == null || AgentSys == null || PlanetRenderer == null || UI == null)
		{
			GD.PrintErr("SimulationController: Faltan nodos asignados en el Inspector.");
			return;
		}

		// Cache de consola y conexión de señal
		// NOTA: Si da error aquí, ajusta la ruta "ConsoleInput" a la real (ej: "Panel/ConsoleInput")
		_consoleInput = UI.GetNode<LineEdit>("ConsoleInput");
		if (_consoleInput != null)
		{
			_consoleInput.TextSubmitted += OnConsoleInputSubmitted;
			GD.Print("[SimulationController] Señal de consola conectada.");
		}

		GD.Print("Iniciando Staggered Loading...");

		// 2. BAKE (Frame 1)
		var bakeResult = Baker.Bake(_rd);
		if (!bakeResult.Success) return;

		await ToSignal(GetTree(), "process_frame");
		await ToSignal(GetTree(), "process_frame"); 

		// 3. AGENTES (Frame 3)
		AgentSys.Initialize(_rd, bakeResult.HeightMapRid, bakeResult.VectorFieldRid, Baker.PlanetRadius, Baker.NoiseScale, Baker.NoiseHeight);

		await ToSignal(GetTree(), "process_frame");

		// 4. PLANETA (Frame 4)
		PlanetRenderer.Initialize(bakeResult.HeightMapRid, bakeResult.VectorFieldRid, Baker.PlanetRadius, Baker.NoiseHeight);

		_isRunning = true;
		GD.Print("Simulation Loop Started.");
	}

	public override void _Process(double delta)
	{
		if (!_isRunning) return;

		double time = Time.GetTicksMsec() / 1000.0;
		AgentSys.UpdateSimulation(delta, time);

		// Gestión de apertura de consola
		// if (Input.IsActionJustPressed("toggle_console"))
		// {
		// 	ToggleConsole();
		// }

		UI.UpdateStats(delta, (int)AgentSys.ActiveAgentCount);
	}
	

	private void ToggleConsole()
{
	if (_consoleInput == null) return;

	bool isVisible = !_consoleInput.Visible;
	_consoleInput.Visible = isVisible;

	if (isVisible)
	{
		_consoleInput.GrabFocus();
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}
	else
	{
		_consoleInput.ReleaseFocus();
		_consoleInput.Text = "";
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}
}



	// --- DENTRO DE SimulationController.cs ---

	private int _nextSpawnIndex = 0;

	public override void _UnhandledInput(InputEvent @event)
	{


		// Toggle de Consola: Solo se dispara si el LineEdit NO tiene el foco
		if (@event.IsActionPressed("toggle_console"))
		{
			ToggleConsole();
			GetViewport().SetInputAsHandled(); // Evita que el evento siga propagándose
			return;
		}


		if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
		{
			// Detectar Ctrl + Click
			if (Input.IsKeyPressed(Key.Ctrl))
			{
				SpawnAgentAtMouse(mouseEvent.Position);
			}
		}
	}

	private void SpawnAgentAtMouse(Vector2 mousePos)
	{
		var camera = GetViewport().GetCamera3D();
		Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
		Vector3 rayDir = camera.ProjectRayNormal(mousePos);

		// Intersección Rayo-Esfera (Radio Baker.PlanetRadius, Centro 0,0,0)
		// Formula: ||P + tD||^2 = R^2
		float r = Baker.PlanetRadius;
		float b = 2.0f * rayOrigin.Dot(rayDir);
		float c = rayOrigin.Dot(rayOrigin) - (r * r);
		float delta = (b * b) - (4.0f * c);

		if (delta >= 0)
		{
			float t = (-b - Mathf.Sqrt(delta)) / 2.0f;
			if (t > 0)
			{
				Vector3 hitPoint = rayOrigin + (rayDir * t);
				
				// Inyectar en el sistema de agentes
				AgentSys.SpawnAgent(hitPoint, _nextSpawnIndex);
				
				// Ciclar índice para no sobreescribir siempre el mismo
				_nextSpawnIndex = (_nextSpawnIndex + 1) % AgentSys.AgentCount;
				
				GD.Print($"Agent Spawned at: {hitPoint} (Index: {_nextSpawnIndex})");
			}
		}
	}






private void OnConsoleInputSubmitted(string text)
{
	if (string.IsNullOrWhiteSpace(text)) return;

	string[] args = text.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);	
	
	
	// Validación: Comando "agents" + parámetro numérico
	if (args.Length >= 2 && args[0] == "agents")
	{
		if (int.TryParse(args[1], out int count))
		{
			// Ejecución en el sistema de agentes
			AgentSys.SpawnRandomAgents(count);
			GD.Print($"[Console] Ejecutando spawn masivo: {count} agentes.");
		}
		else
		{
			GD.PrintErr($"[Console] Error: '{args[1]}' no es un número válido.");
		}
	}
	else
	{
		GD.Print($"[Console] Comando desconocido: {args[0]}");
	}
	
	// Al enviar, siempre cerramos la consola
	ToggleConsole();
}

}
