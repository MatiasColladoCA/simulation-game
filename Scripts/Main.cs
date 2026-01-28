using Godot;
using System;
using System.Collections.Generic;

public partial class Main : Node
{
	// --- DEPENDENCIAS EXTERNAS ---
	[ExportGroup("Sistemas Globales")]
	// [Export] public AgentSystem AgentCompute;
	// [Export] public AgentRender AgentRenderer; // Corregido nombre (camelCase a PascalCase por convención C#)
	
	[Export] public SimulationUI UI;


	private InputManager _inputManager;

	[ExportGroup("Architecture")]
	[Export] public SimulationConfig Config;
	[Export] public AgentDirector AgentDirector;
	[Export] public WorldBuilder WorldFactory;







	// --- VARIABLES INTERNAS ---
	private RenderingDevice _rd;
	private PoiPainter _sharedPoiPainter;
	private Planet _activePlanet;

	// private EnvironmentManager Env;
	private bool _isRunning = false;
	private int _planetIndex = 0;
	
	// UI Helpers
	private LineEdit _consoleInput;
	// private int _nextSpawnIndex = 0;

	private int _nextSpawnIndex = 40000;

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

		// SETUP INPUT
		_inputManager = new InputManager();
		AddChild(_inputManager); // Lo agregamos al árbol para que reciba inputs
		
		// SUSCRIPCIONES (Wiring)
		_inputManager.OnToggleConsole += ToggleConsole;
		_inputManager.OnResetSimulation += SpawnWorld;
		// Delegamos el input de Spawn directamente al Director
		_inputManager.OnSpawnAgentRequest += (mousePos) => 
		{
			var cam = GetViewport().GetCamera3D();
			AgentDirector.TrySpawnAgent(cam, mousePos);
		};
		
		// 2. Init Herramientas Compartidas (Flyweight Pattern)
		// Cargamos el shader una sola vez para todo el juego
		// var shaderFile = GD.Load<RDShaderFile>("res://Shaders/Compute/poi_painter.glsl");
		// _sharedPoiPainter = new PoiPainter(_rd, shaderFile);

		// 3. Crear el mundo inicial
		SpawnWorld();
	}

	private void SpawnWorld()
	{
		_isRunning = false;
		// --- VALIDACIONES DE SEGURIDAD (NUEVO) ---
		if (WorldFactory == null)
		{
			GD.PrintErr("[Main] CRÍTICO: No has asignado el 'WorldFactory' en el Inspector de Main.");
			return;
		}
		if (Config == null)
		{
			GD.PrintErr("[Main] CRÍTICO: No has asignado el 'Config' (SimulationConfig) en el Inspector de Main.");
			return;
		}

		_isRunning = false; // Pausa simulación mientras carga

		// A. Limpieza anterior si existe
		if (_activePlanet != null)
		{
			_activePlanet.QueueFree();
			_activePlanet = null;
			// Esperar un frame si fuera necesario, pero QueueFree lo maneja
		}

		// B. Configuración
		if (Config.RandomizeSeed) Config.WorldSeed = (int)DateTime.Now.Ticks;

		// --- FASE 3: Instanciación del Mundo ---
		// REEMPLAZADO: planetNode = SetupPlanet(WorldSeed); 
		// REEMPLAZADO: if (!success) { ... }
		// var newPlanet = SetupPlanet(Config.WorldSeed);
		var newPlanet = WorldFactory.BuildWorld(_rd, Config, _sharedPoiPainter);

		if (newPlanet == null)
		{
			GD.PrintErr("[Main] Falló la inicialización del planeta. Abortando.");
			return;
		}

		_activePlanet = newPlanet; // Asignación de estado
		AddChild(_activePlanet);

		// --- FASE 4: Inyección a Sistemas ---
		// 3. Inicialización de Agentes (AgentDirector)
		// Main solo dice: "Director, aquí está el nuevo mundo y la config. Haz tu trabajo."
		AgentDirector.OnWorldCreated(_rd, _activePlanet, Config.GridResolution);
		// SetupAgents(_activePlanet);

		AgentDirector.SpawnInitialPopulation(10000, _activePlanet.Radius);

		_isRunning = true;
		GD.Print("[Main] Mundo Inicializado y Simulación Activa.");
	}


	// CAMBIO DE FIRMA: private void SetupPlanet(int WorldSeed)
	private Planet SetupPlanet(int currentSeed)
	{
		int planetSeed = HashCode.Combine(currentSeed, _planetIndex++);
		
		// Asumo que este método existe o es generado localmente
		var planetConfig = WorldFactory.GeneratePlanetConfig(Config, planetSeed); 

		// 1. Instanciar
		var planetNode = WorldFactory.PlanetPrefab.Instantiate<Planet>();
		AddChild(planetNode);
		
		// 2. Inyectar dependencias visuales
		planetNode.PoiVisualScene = WorldFactory.PoiVisualPrefab ?? WorldFactory.PoiMeshPrefab;

		// 3. Inicializar Lógica
		// REEMPLAZADO: bool success = planetNode.Initialize(_rd, planetConfig, _sharedPoiPainter, GridResolution);
		// REEMPLAZADO: (No retornaba nada)
		bool success = planetNode.Initialize(_rd, planetConfig, _sharedPoiPainter, Config.GridResolution);

		if (!success)
		{
			planetNode.QueueFree();
			return null;
		}

		return planetNode;
	}




	// private void SetupAgents(Planet planet)
	// {

	// 	// --- VALIDACIÓN DEFENSIVA ---
	// 	var env = planet.Env;
	// 	if (env == null || !env.POIBuffer.IsValid || !env.InfluenceTexture.IsValid) {
	// 		GD.PrintErr("[Main] CRÍTICO: EnvironmentManager inválido o incompleto.");
	// 		return;
	// 	}
		
		
	// 	// --- INICIALIZACIÓN DE CÓMPUTO ---
	// 	AgentCompute.Initialize(_rd, planet, env, planet.GetParams(), Config.GridResolution);
		
	// 	if (!AgentCompute.IsInitialized) {
	// 		GD.PrintErr("[Main] CRÍTICO: AgentSystem falló al inicializar.");
	// 		return;
	// 	}
		
	// 	// --- CONFIGURACIÓN DE RENDER ---
	// 	var posRid = AgentCompute.GetPosTextureRid();
	// 	var colRid = AgentCompute.GetColorTextureRid();
		
	// 	if (!posRid.IsValid || !colRid.IsValid) {
	// 		GD.PrintErr("[Main] CRÍTICO: RIDs de texturas inválidos.");
	// 		return;
	// 	}

	// 	// 1. Desconectar AgentRenderer de Main (o de su padre actual)
	// 	// GetParent() nos devuelve Main. Lo removemos de su lista de hijos.
	// 	AgentRenderer.GetParent()?.RemoveChild(AgentRenderer);

	// 	// 2. Conectarlo al Planeta
	// 	// Ahora AgentRenderer viaja con el planeta.
	// 	planet.AddChild(AgentRenderer);

	// 	// 3. Resetear Transformaciones (CRÍTICO)
	// 	// Al cambiar de padre, queremos que esté en el centro exacto del nuevo padre.
	// 	AgentRenderer.Position = Vector3.Zero;
	// 	AgentRenderer.Rotation = Vector3.Zero;
	// 	AgentRenderer.Scale = Vector3.One;


		

	// 	AgentRenderer.GetParent()?.RemoveChild(AgentRenderer);
	// 	planet.AddChild(AgentRenderer);

	// 	// Resetear transform del render para que coincida con el centro del planeta
	// 	AgentRenderer.Position = Vector3.Zero;
	// 	AgentRenderer.Rotation = Vector3.Zero;

		
	// 	AgentRenderer.Initialize(posRid, colRid, AgentCompute.AgentCount);
		
	// 	GD.Print($"[Main] {AgentCompute.AgentCount} Agentes conectados al sistema.");
	// }
	

	public override void _Process(double delta)
	{
		if (!_isRunning) return;
		
		// Loop de simulación
		double time = Time.GetTicksMsec() / 1000.0;

		AgentDirector.OnProcess(delta, time);
		
		// if (AgentCompute != null)
		// {
		// 	AgentCompute.UpdateSimulation(delta, time);
			
		// 	// Actualizar UI
		// 	if (UI != null) UI.UpdateStats(delta, (int)AgentCompute.ActiveAgentCount);
		// }
	}


	// private void SpawnAgentAtMouse(Vector2 mousePos)
	// {
	// 	GD.Print("SpawnAgentsAtMouse");
	// 	if (_activePlanet == null) return;

	// 	var camera = GetViewport().GetCamera3D();
	// 	Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
	// 	Vector3 rayDir = camera.ProjectRayNormal(mousePos);

	// 	// Delegamos la matemática al Planeta. Main no calcula esferas.
	// 	if (_activePlanet.RaycastHit(rayOrigin, rayDir, out Vector3 hitPoint))
	// 	{
	// 		// --- BLOQUE DE DEBUG VISUAL ---
	// 		// var debugSphere = new MeshInstance3D();
	// 		// debugSphere.Mesh = new SphereMesh { Radius = 2.0f, Height = 4.0f }; // Tamaño visible
	// 		// debugSphere.MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Red, EmissionEnabled = true, Emission = Colors.Red, EmissionEnergyMultiplier = 2.0f };
	// 		// AddChild(debugSphere);
	// 		// debugSphere.GlobalPosition = hitPoint;
			
	// 		// // Auto-destruir en 2 segundos para no llenar la memoria
	// 		// GetTree().CreateTimer(2.0f).Connect("timeout", Callable.From(debugSphere.QueueFree));
	// 		// // -----------------------------

	// 		Vector3 localHit = _activePlanet.ToLocal(hitPoint);

		
	// 		// "Revivimos" un agente en esa posición
	// 		AgentCompute.SpawnAgent(localHit, _nextSpawnIndex);
			
	// 		// Ciclo circular para reutilizar agentes (Object Pooling en GPU)
	// 		_nextSpawnIndex = 4000 + ((_nextSpawnIndex - 4000 + 1) % 1000);
			
	// 		GD.Print($"[Main] Agente {_nextSpawnIndex} desplegado en: {hitPoint}");		}
	// }

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


	// 	private PlanetParamsData GeneratePlanetConfig(int seed)
	// {
	// 	// Construir struct basado en los sliders del Inspector
	// 	var rng = new RandomNumberGenerator();
	// 	rng.Seed = (ulong)seed;

	// 	return new PlanetParamsData
	// 	{
	// 		PlanetSeed = seed,
	// 		Radius = 1000.0f, // O usa un export
	// 		ResolutionF = 1024.0f,
			
	// 		// Ruido
	// 		NoiseScale = 1.5f,
	// 		NoiseHeight = 70.0f,
	// 		WarpStrength = Config.WarpStrength,
	// 		MountainRoughness = 2.0f,
			
	// 		// Curva
	// 		OceanFloorLevel = 0.0f,
	// 		WeightMultiplier = 2.5f,
	// 		// Amplitude = 100.0f, // Altura máxima
			
	// 		// Offset Aleatorio
	// 		NoiseOffset = new Vector3(rng.Randf(), rng.Randf(), rng.Randf()) * 10000.0f,
			
	// 		// Detalle
	// 		DetailFrequency = Config.DetailFrequency,
	// 		RidgeSharpness = Config.RidgeSharpness,
	// 		MaskStart = Config.MaskStart,
	// 		MaskEnd = Config.MaskEnd,
	// 		GroundDetailFreq = 4.0f
	// 	};
	// }




	
	// public override void _ExitTree()
	// {
	//     _sharedPoiPainter?.Dispose();
	// }













// // --- LÓGICA DE INPUT (Consola y Debug) ---

// 	private void OnConsoleInputSubmitted(string text)
// 	{
// 		if (string.IsNullOrWhiteSpace(text)) return;

// 		string[] args = text.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);    
		
// 		// Validación: Comando "agents" + parámetro numérico
// 		if (args.Length >= 2 && args[0] == "agents")
// 		{
// 			if (int.TryParse(args[1], out int count) && AgentCompute != null)
// 			{
// 				// Ejecución en el sistema de agentes
// 				AgentCompute.SpawnRandomAgents(count);
// 				GD.Print($"[Console] Ejecutando spawn masivo: {count} agentes.");
// 			}
// 			else
// 			{
// 				GD.PrintErr($"[Console] Error: '{args[1]}' no es un número válido o el sistema de agentes no está listo.");
// 			}
// 		}
// 		else
// 		{
// 			GD.Print($"[Console] Comando desconocido: {args[0]}");
// 		}
		
// 		// Al enviar, siempre cerramos la consola
// 		ToggleConsole();
// 	}








// 	private void SpawnAgentAtMouse(Vector2 mousePos)
// 	{
// 	    if (_activePlanet == null) return;

// 	    var camera = GetViewport().GetCamera3D();
// 	    Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
// 	    Vector3 rayDir = camera.ProjectRayNormal(mousePos);

// 	    // Delegamos la matemática al Planeta.
// 	    if (_activePlanet.RaycastHit(rayOrigin, rayDir, out Vector3 hitPoint))
// 	    {
// 	        // Inyectar en el sistema de agentes
// 	        if (AgentCompute != null)
// 	        {
// 	            AgentCompute.SpawnAgent(hitPoint, _nextSpawnIndex);
// 	            _nextSpawnIndex = (_nextSpawnIndex + 1) % 1000; // Límite arbitrario para índices cíclicos
// 	            GD.Print($"[Main] Agente spawneado en: {hitPoint}");
// 	        }
			
// 	        // Debug visual temporal
// 	        // var debugSphere = new MeshInstance3D { Mesh = new SphereMesh { Radius = 2f } };
// 	        // AddChild(debugSphere);
// 	        // debugSphere.GlobalPosition = hitPoint;
// 	    }
// 	}

// 	private void ToggleConsole()
// 	{
// 	    // Asumiendo que UI tiene un método o propiedad para la consola
// 	    if (_consoleInput == null) 
// 	    {
// 	        // Busca el nodo si no está asignado (o usa una referencia exportada en UI)
// 	        _consoleInput = UI?.GetNodeOrNull<LineEdit>("ConsoleInput"); // Ajusta la ruta según tu escena UI
// 	    }

// 	    if (_consoleInput == null) return;

// 	    bool isVisible = !_consoleInput.Visible;
// 	    _consoleInput.Visible = isVisible;

// 	    if (isVisible)
// 	    {
// 	        _consoleInput.GrabFocus();
// 	        Input.MouseMode = Input.MouseModeEnum.Visible;
// 	    }
// 	    else
// 	    {
// 	        _consoleInput.ReleaseFocus();
// 	        _consoleInput.Text = "";
// 	        Input.MouseMode = Input.MouseModeEnum.Captured;
// 	    }
// 	}

	













	// --- LIMPIEZA FINAL ---


	public override void _ExitTree()
	{
		_sharedPoiPainter?.Dispose();
	}
}
