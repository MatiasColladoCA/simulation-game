using Godot;
using System; // Necesario para Buffer.BlockCopy
using System.Runtime.InteropServices;
// using Scripts.Contracts;

public partial class Main : Node
{
	// --- DEPENDENCIAS DE ESCENA ---
	[Export] public PlanetRender PlanetRender;
	[Export] public PlanetBaker Baker;

	[Export] public AgentSystem AgentCompute;

	[Export] public AgentRender agentRender;
	
	[Export] public SimulationUI UI;
	[Export] public EnvironmentManager Environment;
	[Export] public RDShaderFile PoiPainterShader;

	// --- CONFIGURACIÓN DE SEMILLA ---
	[ExportGroup("Simulation Settings")]
	[Export] public int WorldSeed = 1111;
	[Export] public bool RandomizeSeed = true;

	// --- BIOMA (VISUAL) ---
	[ExportGroup("Visuals DNA")]
	[Export] public PlanetBiomeData SpecificBiome; 
	[Export] public bool RandomizeBiomeOnStart = true; 

	// --- ADN DE LA SIMULACIÓN (NIVELES) ---
	[ExportGroup("Simulation DNA")]
	[Export(PropertyHint.Range, "0,1")] public float GlobalHumidity = 0.5f;   // Controla nivel del mar
	[Export(PropertyHint.Range, "0,1")] public float GlobalTemperature = 0.5f; // Controla nieve/hielo


	[ExportGroup("Noise Fine Tuning")]
	[Export] public float WarpStrength = 0.15f;      // Antes hardcoded 0.15
	[Export] public float DetailFrequency = 4.0f;    // Antes hardcoded 4.0
	[Export] public float RidgeSharpness = 2.5f;     // Antes hardcoded 2.5
	
	[Export(PropertyHint.Range, "0,1")] public float MaskStart = 0.6f; // Antes 0.6
	[Export(PropertyHint.Range, "0,1")] public float MaskEnd = 0.75f;  // Antes 0.75


	// --- VARIABLES INTERNAS ---
	private PoiPainter _poiPainter;
	private RenderingDevice _rd;
	private bool _isRunning = false;
	private bool _isViewPoiField = false;
	private bool _isViewVectorField = false;
	private LineEdit _consoleInput;


	
	// Configuración actual de generación
	private PlanetParamsData _currentConfig;

	// Buffer en GPU para los parámetros (Binding 4)
	private Rid _paramsBuffer; 

	


	// Propiedades públicas
	public float CurrentWaterLevel { get; private set; }
	public float CurrentSnowLevel { get; private set; }

	public override async void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();
		
		if (Baker == null || AgentCompute == null || PlanetRender == null || UI == null || Environment == null || PoiPainterShader == null)
		{
			GD.PrintErr("[SimController] Faltan dependencias en el Inspector.");
			return;
		}

		_poiPainter = new PoiPainter(_rd, PoiPainterShader);

		// ---------------------------------------------------------
		// 0. GENERACIÓN DE SEED Y OFFSET
		// ---------------------------------------------------------
		if (RandomizeSeed)
		{
			WorldSeed = (int)DateTime.Now.Ticks;
			GD.Print($"[Sim] Random Seed Generated: {WorldSeed}");
		}

		var rng = new RandomNumberGenerator();
		rng.Seed = (ulong)WorldSeed;
		
		// Offset grande para evitar simetría en el origen
		Vector3 seedOffset = new Vector3(
			rng.RandfRange(-10000.0f, 10000.0f),
			rng.RandfRange(-10000.0f, 10000.0f),
			rng.RandfRange(-10000.0f, 10000.0f)
		);

		// ---------------------------------------------------------
		// 1. CONFIGURACIÓN DEL PLANETA (ADN FÍSICO)
		// ---------------------------------------------------------
		// Aquí definimos los parámetros para el estilo "Lague"
		_currentConfig = new PlanetParamsData {
			Radius = 100.0f,
			ResolutionF = 1024.0f, // Usa 256 si va lento
			NoiseOffset = seedOffset,

			// A. RUIDO BASE (Forma de continentes)
			NoiseScale = 0.6f,       // Bajo = Continentes grandes. Alto = Islas pequeñas.
			NoiseHeight = 70.0f,     // Amplitud máxima de montañas
			
			// B. PARÁMETROS DE ESCULPIDO (Computer Shader)
			OceanFloorLevel = 0.45f, //0.45f,   // 0.0 a 1.0. Cuanto más bajo, más tierra.
			WeightMultiplier = 3.5f,   // Fuerza de las montañas
			
			// AAA Tuning (Valores "Roca Orgánica")
			WarpStrength = 0.15f,      // Sutil, solo para romper simetría
			DetailFrequency = 7.0f,    // Detalle rocoso estándar
			RidgeSharpness = 2.5f,     // Picos definidos
			
			GroundDetailFreq = 5.5f,   // <-- ¡CLAVE! Muy bajo para un suelo liso y orgánico.
			// --- SUELO (Baja frecuencia para llanuras suaves) ---

			
			MaskStart = 0.6f,          // Montañas solo en zonas altas
			MaskEnd = 0.75f,            // Transición suave
			
			MountainRoughness = 2.0f  // Controla la lacunaridad o detalle fino
		
		};


		void ValidatePlanetConfig(PlanetParamsData c)
		{
			c.WarpStrength = c.WarpStrength <= 0.001f ? 0.15f : c.WarpStrength;
			c.DetailFrequency = c.DetailFrequency <= 0.001f ? 4.0f : c.DetailFrequency;
			c.RidgeSharpness = c.RidgeSharpness <= 0.001f ? 2.5f : c.RidgeSharpness;
			c.MaskEnd = c.MaskEnd <= c.MaskStart ? c.MaskStart + 0.1f : c.MaskEnd;
		}

		ValidatePlanetConfig(_currentConfig);


		// ---------------------------------------------------------
		// 2. ENVIAR DATOS A LA GPU (Binding 4)
		// ---------------------------------------------------------
		// Inicializamos el buffer si el Baker no lo ha hecho, o usamos el del Baker.
		// Asumo aquí que Main gestiona la actualización.
		UpdateShaderBuffer();

		// Pasamos la config (metadata) al Baker por si la necesita en C#
		Baker.SetParams(_currentConfig);


		// ---------------------------------------------------------
		// 3. BAKE DEL TERRENO (GPU EXECUTION)
		// ---------------------------------------------------------
		// Asegúrate de que Baker use el buffer que acabamos de actualizar (Binding 4)
		var bakeResult = Baker.Bake(_rd); 
		
		if (!bakeResult.Success) 
		{
			GD.PrintErr("[SimController] Falló el Bake del terreno.");
			return;
		}

		// --- DEBUG VITAL ---
		GD.Print($"[DEBUG BAKE] Min: {bakeResult.MinHeight}, Max: {bakeResult.MaxHeight}");
		
		if (Mathf.IsEqualApprox(bakeResult.MaxHeight, 0) && Mathf.IsEqualApprox(bakeResult.MinHeight, 0))
		{
			 GD.PrintErr("[ALERTA] El planeta es plano. Verifica que UpdateShaderBuffer() se esté llamando antes del Dispatch.");
		}


		// 2. Preparar Datos Visuales (RenderData)
		// Aquí defines colores, etc. que NO afectan la geometría
		// var renderData = new PlanetRenderInitData {



		// ---------------------------------------------------------
		// 4. LÓGICA DE NIVELES (CPU)
		// ---------------------------------------------------------
		float realMin = bakeResult.MinHeight;
		float realMax = bakeResult.MaxHeight;
		float realRange = bakeResult.HeightRange;

		// Calcular niveles reales basados en sliders globales
		float waterPercent = Mathf.Ease(GlobalHumidity, 0.4f); 
		CurrentWaterLevel = realMin + (realRange * waterPercent);

		float snowPercent = Mathf.Lerp(0.95f, 0.05f, GlobalTemperature);
		CurrentSnowLevel = realMin + (realRange * snowPercent);

		// Evitar que la nieve esté bajo el agua
		if (CurrentSnowLevel < CurrentWaterLevel) CurrentSnowLevel = CurrentWaterLevel + 1.0f;

		GD.Print($"[Sim] Logic: Water@{CurrentWaterLevel:F2} | Snow@{CurrentSnowLevel:F2}");

		// ---------------------------------------------------------
		// 5. SELECCIÓN DE ADN VISUAL (BIOMA)
		// ---------------------------------------------------------
		PlanetBiomeData currentBiomeData;

		if (RandomizeBiomeOnStart)
		{
			currentBiomeData = PlanetBiomeData.GenerateRandom();
			GD.Print("[Sim] Generado Bioma Procedural.");
		}
		else if (SpecificBiome != null)
		{
			currentBiomeData = SpecificBiome;
		}
		else
		{
			currentBiomeData = new PlanetBiomeData(); // Default
		}

		// ---------------------------------------------------------
		// 6. INICIALIZAR SUBSISTEMAS
		// ---------------------------------------------------------
		Environment.Initialize(
			_rd,
			bakeResult.HeightMapRid,
			bakeResult.VectorFieldRid,
			_currentConfig);

		// Esperar un frame para asegurar texturas
		await ToSignal(GetTree(), "process_frame");

		// Crear Textura de Influencia (POI System)
		var fmt = new RDTextureFormat
		{
			TextureType = RenderingDevice.TextureType.Cube,
			Width = (uint)_currentConfig.ResolutionF,
			Height = (uint)_currentConfig.ResolutionF,
			Depth = 1,
			ArrayLayers = 6,
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit | 
						RenderingDevice.TextureUsageBits.CanCopyFromBit |
						RenderingDevice.TextureUsageBits.CanCopyToBit
		};
		
		// Crear textura vacía
		var view = new RDTextureView { FormatOverride = RenderingDevice.DataFormat.R16G16B16A16Sfloat };
		Rid influenceTex = _rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]>());

		if (_rd.TextureIsValid(influenceTex))
		{
			 _rd.TextureClear(influenceTex, new Color(0, 0, 0, 0), 0, 1, 0, 6);
			 Environment.SetInfluenceTexture(influenceTex);
		}

		PlanetRender.Initialize(
			bakeResult.HeightMapRid,   // Rid
			bakeResult.VectorFieldRid, // Rid
			bakeResult.NormalMapRid,   // Rid
			_currentConfig.Radius,     // float
			bakeResult.MinHeight,      // float (Calculado por GPU)
			bakeResult.MaxHeight      // float (Calculado por GPU)
			// renderData                 // Struct Visual		);


		);
		// ---------------------------------------------------------
		// 7. INICIALIZAR AGENTES Y RENDERER
		// ---------------------------------------------------------

		AgentCompute.Initialize(
			_rd,
			Environment,
			_currentConfig); 

		// 2. Inicializar Render (Visual)
		// Extraemos los RIDs que generó el sistema de agentes
		var posRid = AgentCompute.GetPosTextureRid(); // Necesitas exponer esto en AgentSystem
		var colRid = AgentCompute.GetColorTextureRid(); // Y esto

		agentRender.Initialize(posRid, colRid, AgentCompute.AgentCount);
		// Pintar Influencia Inicial
		// NOTA: Asegúrate de que Baker exponga su ParamsBuffer si lo necesitamos aquí, 
		// o usa el _paramsBuffer local si es el mismo binding.
		_poiPainter.PaintInfluence(influenceTex, Environment.POIBuffer, GetParamsBufferRid(), (float)_currentConfig.ResolutionF);
		
		await ToSignal(GetTree(), "process_frame");



		

		

		PlanetRender.SetInfluenceMap(influenceTex);
		PlanetRender.UpdateEnvironmentLevels(CurrentWaterLevel, CurrentSnowLevel, realMin, realMax);
		PlanetRender.ApplyBiomeData(currentBiomeData);
		PlanetRender.SetViewPoiField(_isViewPoiField);

		_isRunning = true;
		GD.Print("[Main] Initialized AAA Pipeline.");
	}

	public override void _Process(double delta)
	{
		if (!_isRunning) return;
		
		double time = Time.GetTicksMsec() / 1000.0;
		AgentCompute.UpdateSimulation(delta, time);

		// Asumo que UI maneja sus inputs internamente, o agregamos lógica aquí
		UI.UpdateStats(delta, (int)AgentCompute.ActiveAgentCount);
	}

	// --- MÉTODOS DE COMUNICACIÓN CON GPU ---

	/// <summary>
	/// Convierte la configuración C# a bytes y actualiza el Uniform Buffer de la GPU.
	/// Esto es lo que "envía" los datos al Compute Shader.
	/// </summary>
	private void UpdateShaderBuffer()
	{
		// 1. Construir el array plano de floats siguiendo la estructura std140 del shader
		// layout(set = 0, binding = 4) uniform BakeParams
		float[] rawFloats = new float[] 
		{
			// vec4 noise_settings (x: scale, y: persistence, z: lacunarity, w: octaves)
			_currentConfig.NoiseScale, 
			0.5f,  // Persistence (Valor estándar para look orgánico)
			2.0f,  // Lacunarity (Valor estándar)
			_currentConfig.WarpStrength,  // Octaves (Detalle)

			// vec4 curve_params (x: ocean, y: weight, z: height, w: radius)
			_currentConfig.OceanFloorLevel,
			_currentConfig.WeightMultiplier,
			_currentConfig.NoiseHeight,
			_currentConfig.GroundDetailFreq,

			// vec4 global_offset (xyz: seed)
			_currentConfig.NoiseOffset.X, 
			_currentConfig.NoiseOffset.Y, 
			_currentConfig.NoiseOffset.Z, 
			0.0f, // Padding

			// vec4 detail_params (ANTES ERA pad_center)
			// AQUI METEMOS LAS NUEVAS PERILLAS:
			_currentConfig.DetailFrequency,  // x
			_currentConfig.RidgeSharpness,   // y
			_currentConfig.MaskStart,        // z
			_currentConfig.MaskEnd,

			// vec4 res_offset (x: resolution)
			(float)_currentConfig.ResolutionF, 0,0,0,

			// vec4 pad_uv (unused)
			0,0,0,0
		};

		// 2. Convertir a Bytes
		byte[] paramsBytes = new byte[rawFloats.Length * sizeof(float)];
		Buffer.BlockCopy(rawFloats, 0, paramsBytes, 0, paramsBytes.Length);

		// 3. Crear o Actualizar el Buffer
		if (!_paramsBuffer.IsValid)
		{
			_paramsBuffer = _rd.UniformBufferCreate((uint)paramsBytes.Length, paramsBytes);
		}
		else
		{
			_rd.BufferUpdate(_paramsBuffer, 0, (uint)paramsBytes.Length, paramsBytes);
		}
	}

	/// <summary>
	/// Helper para obtener el RID del buffer y pasárselo a otros sistemas (como el Baker o Painter)
	/// </summary>
	public Rid GetParamsBufferRid()
	{
		if (!_paramsBuffer.IsValid) UpdateShaderBuffer();
		return _paramsBuffer;
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
				PlanetRender.SetViewVectorField(_isViewVectorField);
				GD.Print($"[Visuals] Vector Field Debug: {_isViewVectorField}");
			}
			else if (keyEvent.Keycode == Key.G)
			{
				_isViewPoiField = !_isViewPoiField;
				PlanetRender.SetViewPoiField(_isViewPoiField);
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
				AgentCompute.SpawnAgent(hitPoint, _nextSpawnIndex);
				
				// Ciclar índice para no sobreescribir siempre el mismo
				_nextSpawnIndex = (_nextSpawnIndex + 1) % AgentCompute.AgentCount;
				
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
				AgentCompute.SpawnRandomAgents(count);
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


	private byte[] GetBytesFromParams(PlanetParamsData config, Vector3 offset)
	{
		// Construimos el array lineal de floats (padding necesario para std140/vec4)
		// 6 vec4s = 24 floats en total
		float[] rawFloats = new float[] 
		{
			// vec4 noise_settings (x: Scale, y: Persist, z: Lacun, w: Octaves)
			config.NoiseScale, 
			0.5f,  // Persistence (Valor fijo sugerido o añádelo a tu struct)
			2.0f,  // Lacunarity (Valor fijo sugerido)
			6.0f,  // Octaves

			// vec4 curve_params (x: Ocean, y: Weight, z: Amp, w: Radius)
			config.OceanFloorLevel,
			config.WeightMultiplier,
			config.NoiseHeight,
			config.Radius,

			// vec4 global_offset (xyz: Seed)
			offset.X, offset.Y, offset.Z, 0.0f,

			// vec4 pad_center (Relleno, no usado en shader actual)
			0, 0, 0, 0,

			// vec4 res_offset (x: Resolution)
			(float)config.ResolutionF, 0, 0, 0,

			// vec4 pad_uv (Relleno final)
			0, 0, 0, 0 
		};

		// Convertir float[] a byte[] para Godot
		byte[] bytes = new byte[rawFloats.Length * sizeof(float)];
		Buffer.BlockCopy(rawFloats, 0, bytes, 0, bytes.Length);
		return bytes;
	}


}
