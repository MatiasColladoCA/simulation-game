using Godot;
using System;
using System.Collections.Generic;

public partial class Main : Node
{
	// --- DEPENDENCIAS EXTERNAS ---
	[ExportGroup("Sistemas Globales")]
	[Export] public AgentSystem AgentCompute;
	[Export] public AgentRender AgentRenderer; // Corregido nombre (camelCase a PascalCase por convención C#)
	[Export] public SimulationUI UI;
	
	// --- DEPENDENCIAS DE FÁBRICA ---
	[ExportGroup("Recursos de Fábrica")]
	[Export] public PackedScene PlanetPrefab; 
	[Export] public PackedScene PoiMeshPrefab; // Se pasará al Planet
	[Export] public PackedScene PoiVisualPrefab; // Si tienes uno visual distinto

	// --- CONFIGURACIÓN DE SIMULACIÓN (INPUTS) ---
	[ExportGroup("Simulation Settings")]
	[Export] public int WorldSeed = 1111;
	[Export] public bool RandomizeSeed = true;

	[ExportGroup("Simulation DNA")]
	[Export(PropertyHint.Range, "0,1")] public float GlobalHumidity = 0.5f;
	[Export(PropertyHint.Range, "0,1")] public float GlobalTemperature = 0.5f;

	[ExportGroup("Noise Fine Tuning")]
	[Export] public float WarpStrength = 0.15f;
	[Export] public float DetailFrequency = 4.0f;
	[Export] public float RidgeSharpness = 2.5f;
	[Export(PropertyHint.Range, "0,1")] public float MaskStart = 0.6f;
	[Export(PropertyHint.Range, "0,1")] public float MaskEnd = 0.75f;

	// --- VARIABLES INTERNAS ---
	private RenderingDevice _rd;
	private PoiPainter _sharedPoiPainter;
	private Planet _activePlanet;

	private EnvironmentManager Env;
	private bool _isRunning = false;
	private int _planetIndex = 0;
	
	// UI Helpers
	private LineEdit _consoleInput;
	private int _nextSpawnIndex = 0;

	public override void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();

		// 1. Verificación de Seguridad
		if (_rd == null)
		{
			GD.PrintErr("[Main] FATAL: RenderingDevice es null. Cambia a Forward+.");
			GetTree().Quit();
			return;
		}
		
		// 2. Init Herramientas Compartidas (Flyweight Pattern)
		// Cargamos el shader una sola vez para todo el juego
		// var shaderFile = GD.Load<RDShaderFile>("res://Shaders/Compute/poi_painter.glsl");
		// _sharedPoiPainter = new PoiPainter(_rd, shaderFile);

		// 3. Crear el mundo inicial
		SpawnWorld();
	}

	private void SpawnWorld()
	{
		_isRunning = false; // Pausa simulación mientras carga

		// A. Limpieza anterior si existe
		if (_activePlanet != null)
		{
			_activePlanet.QueueFree();
			// Esperar un frame si fuera necesario, pero QueueFree lo maneja
		}

		// B. Configuración
		if (RandomizeSeed) WorldSeed = (int)DateTime.Now.Ticks;
		int planetSeed = HashCode.Combine(WorldSeed, _planetIndex++);
		
		var planetConfig = GeneratePlanetConfig(planetSeed);

		// C. Instanciar Planeta
		var planetNode = PlanetPrefab.Instantiate<Planet>();
		AddChild(planetNode);
		_activePlanet = planetNode;

		// Inyectar dependencias visuales al planeta antes de inicializar
		planetNode.PoiVisualScene = PoiVisualPrefab ?? PoiMeshPrefab;

		// D. INICIALIZAR PLANETA (Aquí ocurre la magia interna)
		bool success = planetNode.Initialize(_rd, planetConfig, _sharedPoiPainter);
		
		if (!success)
		{
			GD.PrintErr("[Main] Falló la inicialización del planeta. Abortando.");
			return;
		}

		// E. CONECTAR CON SISTEMA DE AGENTES
		// El planeta ya tiene los datos, ahora se los pasamos a los agentes
		SetupAgents(planetNode);

		_isRunning = true;
		GD.Print("[Main] Mundo AAA Inicializado.");
	}

	private void SetupAgents(Planet planet)
	{
		// 1. Extraer RIDs del planeta (Propiedad del Planeta -> Consumo de Agentes)
		Rid heightMap = planet.GetHeightMapRid();
		Rid influenceTex = planet.GetInfluenceTextureRid();
		Rid poiBuffer = planet.GetPoiBufferRid();
		Rid vectorField = planet.GetVectorFieldRid();
		PlanetParamsData paramsData = planet.GetParams();

		// Validación de seguridad
		if (!heightMap.IsValid || !influenceTex.IsValid || !poiBuffer.IsValid)
		{
			GD.PrintErr("[Main] Faltan recursos GPU del planeta para iniciar agentes.");
			return;
		}

		// 2. Inicializar entorno Legacy (Si EnvManager sigue existiendo como estático)
		// Asumiendo que has adaptado EnvManager para no depender de PlanetRender
		Env.Initialize(_rd, heightMap, vectorField, paramsData);
		Env.SetInfluenceTexture(influenceTex);
		Env.SetupPoiBuffer();

		// 3. Inicializar Compute de Agentes
		AgentCompute.Initialize(_rd, Env, paramsData);

		// 4. Inicializar Render de Agentes
		var posRid = AgentCompute.GetPosTextureRid();
		var colRid = AgentCompute.GetColorTextureRid();
		AgentRenderer.Initialize(posRid, colRid, AgentCompute.AgentCount);
		
		GD.Print("[Main] Agentes conectados al terreno.");
	}

	private PlanetParamsData GeneratePlanetConfig(int seed)
	{
		// Construir struct basado en los sliders del Inspector
		var rng = new RandomNumberGenerator();
		rng.Seed = (ulong)seed;

		return new PlanetParamsData
		{
			PlanetSeed = seed,
			Radius = 100.0f, // O usa un export
			ResolutionF = 1024.0f,
			
			// Ruido
			NoiseScale = 1.5f,
			NoiseHeight = 70.0f,
			WarpStrength = this.WarpStrength,
			MountainRoughness = 2.0f,
			
			// Curva
			OceanFloorLevel = 0.0f,
			WeightMultiplier = 2.5f,
			// Amplitude = 100.0f, // Altura máxima
			
			// Offset Aleatorio
			NoiseOffset = new Vector3(rng.Randf(), rng.Randf(), rng.Randf()) * 10000.0f,
			
			// Detalle
			DetailFrequency = this.DetailFrequency,
			RidgeSharpness = this.RidgeSharpness,
			MaskStart = this.MaskStart,
			MaskEnd = this.MaskEnd,
			GroundDetailFreq = 4.0f
		};
	}

	public override void _Process(double delta)
	{
		if (!_isRunning) return;
		
		// Loop de simulación
		double time = Time.GetTicksMsec() / 1000.0;
		
		if (AgentCompute != null)
		{
			// AgentCompute.UpdateSimulation(delta, time);
			
			// Actualizar UI
			// if (UI != null) UI.UpdateStats(delta, (int)AgentCompute.ActiveAgentCount);
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event.IsActionPressed("toggle_console")) ToggleConsole();

		if (_activePlanet != null && @event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
		{
			if (Input.IsKeyPressed(Key.Ctrl))
			{
				SpawnAgentAtMouse(mb.Position);
			}
		}
		
		// Reset rápido para debug
		if (@event is InputEventKey k && k.Pressed && k.Keycode == Key.R)
		{
			SpawnWorld();
		}
	}

	private void SpawnAgentAtMouse(Vector2 mousePos)
	{
		if (_activePlanet == null) return;

		var camera = GetViewport().GetCamera3D();
		Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
		Vector3 rayDir = camera.ProjectRayNormal(mousePos);

		// Delegamos la matemática al Planeta. Main no calcula esferas.
		if (_activePlanet.RaycastHit(rayOrigin, rayDir, out Vector3 hitPoint))
		{
			// AgentCompute.SpawnAgent(hitPoint, _nextSpawnIndex);
			// _nextSpawnIndex = (_nextSpawnIndex + 1) % AgentCompute.AgentCount;
			GD.Print($"[Main] Spawn intento en: {hitPoint}");
		}
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
	
	// public override void _ExitTree()
	// {
	//     _sharedPoiPainter?.Dispose();
	// }













// --- LÓGICA DE INPUT (Consola y Debug) ---

	private void OnConsoleInputSubmitted(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;

		string[] args = text.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);    
		
		// Validación: Comando "agents" + parámetro numérico
		if (args.Length >= 2 && args[0] == "agents")
		{
			if (int.TryParse(args[1], out int count) && AgentCompute != null)
			{
				// Ejecución en el sistema de agentes
				AgentCompute.SpawnRandomAgents(count);
				GD.Print($"[Console] Ejecutando spawn masivo: {count} agentes.");
			}
			else
			{
				GD.PrintErr($"[Console] Error: '{args[1]}' no es un número válido o el sistema de agentes no está listo.");
			}
		}
		else
		{
			GD.Print($"[Console] Comando desconocido: {args[0]}");
		}
		
		// Al enviar, siempre cerramos la consola
		ToggleConsole();
	}

	// --- TECLAS Y MOUSE ---

	// public override void _UnhandledInput(InputEvent @event)
	// {
	//     // Toggle de Consola
	//     if (@event.IsActionPressed("toggle_console"))
	//     {
	//         ToggleConsole();
	//         GetViewport().SetInputAsHandled();
	//         return;
	//     }

	//     // Hotkeys de Debug
	//     if (@event is InputEventKey keyEvent && keyEvent.Pressed)
	//     {
	//         // Debug de texturas visuales (Opcional, si PlanetRender soporta debug view)
	//         if (keyEvent.Keycode == Key.F)
	//         {
	//             // Ejemplo: _activePlanet?.ToggleVectorFieldDebug();
	//             GD.Print("[Debug] Vector Field Toggle (Not implemented yet)");
	//         }
	//         else if (keyEvent.Keycode == Key.R)
	//         {
	//             GD.Print("[Main] Regenerando mundo...");
	//             SpawnWorld();
	//         }
	//     }

	//     // Spawn de Agentes con Mouse
	//     if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
	//     {
	//         if (Input.IsKeyPressed(Key.Ctrl))
	//         {
	//             SpawnAgentAtMouse(mouseEvent.Position);
	//         }
	//     }
	// }

	// private void SpawnAgentAtMouse(Vector2 mousePos)
	// {
	//     if (_activePlanet == null) return;

	//     var camera = GetViewport().GetCamera3D();
	//     Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
	//     Vector3 rayDir = camera.ProjectRayNormal(mousePos);

	//     // Delegamos la matemática al Planeta.
	//     if (_activePlanet.RaycastHit(rayOrigin, rayDir, out Vector3 hitPoint))
	//     {
	//         // Inyectar en el sistema de agentes
	//         if (AgentCompute != null)
	//         {
	//             AgentCompute.SpawnAgent(hitPoint, _nextSpawnIndex);
	//             _nextSpawnIndex = (_nextSpawnIndex + 1) % 1000; // Límite arbitrario para índices cíclicos
	//             GD.Print($"[Main] Agente spawneado en: {hitPoint}");
	//         }
			
	//         // Debug visual temporal
	//         // var debugSphere = new MeshInstance3D { Mesh = new SphereMesh { Radius = 2f } };
	//         // AddChild(debugSphere);
	//         // debugSphere.GlobalPosition = hitPoint;
	//     }
	// }

	// private void ToggleConsole()
	// {
	//     // Asumiendo que UI tiene un método o propiedad para la consola
	//     if (_consoleInput == null) 
	//     {
	//         // Busca el nodo si no está asignado (o usa una referencia exportada en UI)
	//         _consoleInput = UI?.GetNodeOrNull<LineEdit>("ConsoleInput"); // Ajusta la ruta según tu escena UI
	//     }

	//     if (_consoleInput == null) return;

	//     bool isVisible = !_consoleInput.Visible;
	//     _consoleInput.Visible = isVisible;

	//     if (isVisible)
	//     {
	//         _consoleInput.GrabFocus();
	//         Input.MouseMode = Input.MouseModeEnum.Visible;
	//     }
	//     else
	//     {
	//         _consoleInput.ReleaseFocus();
	//         _consoleInput.Text = "";
	//         Input.MouseMode = Input.MouseModeEnum.Captured;
	//     }
	// }

	// --- CONFIGURACIÓN DE PLANETA (No duplicar si ya está arriba) ---
	// NOTA: Si ya incluiste GeneratePlanetConfig en la primera parte, BORRA este bloque.
	/*
	private PlanetParamsData GeneratePlanetConfig(int planetSeed)
	{
		var rng = new RandomNumberGenerator();
		rng.Seed = (ulong)planetSeed;
		
		Vector3 seedOffset = new Vector3(
			rng.RandfRange(-10000.0f, 10000.0f),
			rng.RandfRange(-10000.0f, 10000.0f),
			rng.RandfRange(-10000.0f, 10000.0f)
		);

		return new PlanetParamsData { 
			Radius = 100.0f,
			ResolutionF = 1024.0f,
			NoiseOffset = seedOffset,
			PlanetSeed = planetSeed,
			
			NoiseScale = 0.6f,
			NoiseHeight = 70.0f,
			OceanFloorLevel = 0.45f,
			WeightMultiplier = 3.5f,
			WarpStrength = this.WarpStrength,
			DetailFrequency = this.DetailFrequency,
			RidgeSharpness = this.RidgeSharpness,
			GroundDetailFreq = 5.5f,
			MaskStart = this.MaskStart,
			MaskEnd = this.MaskEnd,
			MountainRoughness = 2.0f,
		};
	}
	*/
	
	// --- LIMPIEZA FINAL ---
	public override void _ExitTree()
	{
		_sharedPoiPainter?.Dispose();
	}
}
