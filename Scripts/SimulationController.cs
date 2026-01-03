using Godot;
using System.Threading.Tasks;
using System;

public partial class SimulationController : Node
{
	// Dependencias de Escena (Arrastrar en Editor)
	[Export] public TerrainBaker Baker;
	[Export] public AgentSystem AgentSys;
	[Export] public PlanetRender PlanetRenderer; // Tu script existente
	[Export] public SimulationUI UI;
	[Export] public EnvironmentManager Environment;

	[Export] public RDShaderFile PoiPainterShader;
	private PoiPainter _poiPainter;

	private RenderingDevice _rd;
	private bool _isRunning = false;

	// VIEWS
	private bool _isViewVectorField = false;
	private bool _isViewPoiField = false;


	private LineEdit _consoleInput;

	private PlanetParams _currentConfig = new PlanetParams {
		Radius = 100.0f,
		NoiseScale = 1.0f,
		NoiseHeight = 25.0f,
		Resolution = 512
	};

	// --- REEMPLAZAR EL MÉTODO _Ready COMPLETO ---
	public override async void _Ready()
	{
		// 1. Obtener RD Global
		_rd = RenderingServer.GetRenderingDevice();
		
		// Validar dependencias críticas
		if (Baker == null || AgentSys == null || PlanetRenderer == null || UI == null || Environment == null || PoiPainterShader == null)
		{
			GD.PrintErr("[SimController] Faltan nodos o recursos asignados en el Inspector.");
			return;
		}

		// Instanciar Painter
		_poiPainter = new PoiPainter(_rd, PoiPainterShader);

		// Configurar Baker
		Baker.SetParams(_currentConfig);

		// 2. BAKE DEL TERRENO (GPU)
		var bakeResult = Baker.Bake(_rd);
		if (!bakeResult.Success) 
		{
			GD.PrintErr("[SimController] Falló el Bake del terreno.");
			return;
		}

		// 3. INICIALIZAR ENTORNO (POIs y Visuales)
		Environment.Initialize(_rd, bakeResult.HeightMapRid, bakeResult.VectorFieldRid, _currentConfig);

		await ToSignal(GetTree(), "process_frame");

		// 4. CREAR TEXTURA DE INFLUENCIA (CORRECCIÓN CRÍTICA: Primero creamos, luego asignamos)
		
		var fmt = new RDTextureFormat
		{
			TextureType = RenderingDevice.TextureType.Cube, // <--- OBLIGATORIO: Tipo Cubo
			Width = (uint)_currentConfig.Resolution,
			Height = (uint)_currentConfig.Resolution,
			Depth = 1,
			ArrayLayers = 6, // <--- OBLIGATORIO: 6 Caras
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			// UsageBits: Storage (Compute), Sampling (Visual Shader), CanCopy (Debug/Internal)
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit | 
						RenderingDevice.TextureUsageBits.CanCopyFromBit |
						RenderingDevice.TextureUsageBits.CanCopyToBit
		};

		var view = new RDTextureView
		{
			FormatOverride = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			SwizzleR = RenderingDevice.TextureSwizzle.Identity,
			SwizzleG = RenderingDevice.TextureSwizzle.Identity,
			SwizzleB = RenderingDevice.TextureSwizzle.Identity,
			SwizzleA = RenderingDevice.TextureSwizzle.Identity
		};

		// Crear la textura
		Rid influenceTex = _rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]>());

		if (!_rd.TextureIsValid(influenceTex))
		{
			GD.PrintErr("[SimController] ERROR FATAL: No se pudo crear la textura de influencia.");
			return;
		}

		// LIMPIEZA INICIAL: Borrar basura de memoria (opcional pero recomendado)
		// Limpiamos con negro transparente o el color de fondo deseado
		_rd.TextureClear(influenceTex, new Color(0, 0, 0, 0), 0, 1, 0, 6);

		// 5. VINCULAR A SISTEMAS
		Environment.SetInfluenceTexture(influenceTex);
		
		// Inicializar Agentes (ahora que el entorno tiene la textura lista)
		AgentSys.Initialize(_rd, Environment, _currentConfig); 

		// 6. PINTAR INFLUENCIA INICIAL
		_poiPainter.PaintInfluence(
			influenceTex, 
			Environment.POIBuffer, 
			Baker.ParamsBuffer, 
			(uint)_currentConfig.Resolution // Casteo explícito
		);

		// Nota: No usamos Submit/Sync manual porque estamos en el RD Global.
		
		await ToSignal(GetTree(), "process_frame");

		// 7. INICIALIZAR RENDERER DEL PLANETA
		PlanetRenderer.Initialize(bakeResult.HeightMapRid, bakeResult.VectorFieldRid, _currentConfig);

		// CRÍTICO: Pasar la textura al Material del Renderer
		PlanetRenderer.SetInfluenceMap(influenceTex);
		
		// Configurar debug visual
		PlanetRenderer.SetViewPoiField(_isViewPoiField);

		_isRunning = true;
		GD.Print("[SimulationController] Initialized: TextureType.Cube OK.");
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
		// Toggle de Consola
		if (@event.IsActionPressed("toggle_console"))
		{
			ToggleConsole();
			GetViewport().SetInputAsHandled();
			return;
		}

		// Verificar que sea un evento de tecla
		if (@event is InputEventKey keyEvent && keyEvent.Pressed)
		{
			if (keyEvent.Keycode == Key.F)
			{
				_isViewVectorField = !_isViewVectorField;
				PlanetRenderer.SetViewVectorField(_isViewVectorField);
				GD.Print($"[Visuals] Vector Field Debug: {_isViewVectorField}");
			}
			else if (keyEvent.Keycode == Key.G)
			{
				_isViewPoiField = !_isViewPoiField;
				PlanetRenderer.SetViewPoiField(_isViewPoiField);
				GD.Print($"[Visuals] POI Field Debug: {_isViewPoiField}");
			}
		
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
