


using Godot;
using System;

public partial class Planet : Node3D
{
	// --- ESTADO DEL MUNDO ---
	private PlanetParamsData _params;
	private RenderingDevice _rd;

	private PoiPainter _sharedPainter; // Guardamos referencia
	private int _gridResolution;
	private bool _isConfigured = false; // Flag de seguridad
	
	// RIDs que el planeta "posee" y expone al exterior
	public Rid _heightMapRid;
	private Rid _normalMapRid;

	private Rid _vectorFieldRid;
	// Cache de límites físicos para colisiones/gameplay
	private float _minHeightCache;
	private float _maxHeightCache;

	// --- WORKERS (Subsistemas) ---
	private PlanetBaker _terrainBaker; 
	private PoiSystem _poiSystem; 

	// --- REFERENCIAS DE EDITOR ---
	[ExportGroup("Componentes Visuales")]
	// [Export] public Node3D TerrainVisualRoot; 
	[Export] public Node3D PoiContainer; // Nodo vacío hijo donde se instanciarán los POIs
	
	[ExportGroup("Recursos & Assets")]
	[Export] public RDShaderFile BakerShader; 
	[Export] public PackedScene PoiVisualScene;

	[Export] public PlanetRender Renderer;
	[Export] public ShaderMaterial _planetMaterial;

	// --- PROPIEDADES PÚBLICAS (API) ---
	public float Radius => _params.Radius;

	// Getters para Main/Agentes: El Planeta actúa como puente hacia sus sistemas internos
	public PlanetParamsData GetParams() => _params;
	public Rid GetHeightMapRid() => _heightMapRid;
	public Rid GetVectorFieldRid() => _vectorFieldRid;
	
	// Aquí delegamos la consulta al PoiSystem. Si no está listo, devolvemos vacío.
	public Rid GetInfluenceTextureRid() => _poiSystem?.GetInfluenceTexture() ?? new Rid();
	public Rid GetPoiBufferRid() => _poiSystem?.GetPoiBuffer() ?? new Rid();

	[Export] public EnvironmentManager Env;
	// public EnvironmentManager Env;

	// Almacén en RAM de la altura (Solo lectura rápida)
	private Image[] _cpuHeightMapFaces;
	private int _heightMapSize;



	

	// --- PUNTO DE ENTRADA ---
	public bool Initialize(RenderingDevice rd, PlanetParamsData config, PoiPainter sharedPainter, int gridResolution)
	{
		_rd = rd;
		_params = ValidateConfig(config);
		_sharedPainter = sharedPainter;
		_gridResolution = gridResolution;
		
		_isConfigured = true;

		return true;

		// // --- CORRECCIÓN: Asegurar que el Baker existe AQUÍ y AHORA ---
		// if (_terrainBaker == null)
		// {
		// 	_terrainBaker = new PlanetBaker();
		// 	_terrainBaker.Name = "Internal_Baker";
		// 	// Es seguro hacer AddChild aunque el planeta no esté en la escena principal aún.
		// 	// Simplemente se agrega a la jerarquía local del planeta.
		// 	AddChild(_terrainBaker); 
		// }
		
		// // 1. VALIDACIÓN
		// _params = ValidateConfig(config);

		// // 2. GENERACIÓN DEL TERRENO (BAKE)
		// // Configuramos al obrero
		// _terrainBaker.BakerShaderFile = this.BakerShader;
		// _terrainBaker.SetParams(_params);
		
		// // ¡A trabajar!
		// var bakeResult = _terrainBaker.Bake(_rd);

		// // if (!bakeResult.Success)
		// // {
		// // 	GD.PrintErr($"[Planet] Falló la generación del terreno.");
		// // 	return false;
		// // }
		// // else
		// // {
		// // 	GD.Print($"[Planet] Generación del terreno exitosa.");
		// // }

		// // 3. CAPTURA DE RESULTADOS
		// // El planeta toma posesión de las texturas generadas
		// _heightMapRid = bakeResult.HeightMapRid;
		// _normalMapRid = bakeResult.NormalMapRid;
		// _vectorFieldRid = bakeResult.VectorFieldRid;

		// _minHeightCache = bakeResult.MinHeight;
		// _maxHeightCache = bakeResult.MaxHeight;

		// // // 4. ACTUALIZAR VISUALES (Shader de Superficie)
		// // UpdateTerrainVisuals();
		// if (_planetMaterial == null)
		// {
		// 	// _planetMaterial = new ShaderMaterial();
		// 	// _planetMaterial.Shader = GD.Load<Shader>("res://Shaders/Visual/planet_terrain.gdshader");
		// 	GD.PrintErr("[Planet] Falta terrain material");

		// }else
		// {
		// 	GD.Print("[planet] El material se está asignando.");

		// 	Renderer._planetMaterial = _planetMaterial;
		// }

		// if (Renderer != null)
		// {
		// 	Renderer.Initialize(
		// 		_heightMapRid,
		// 		_vectorFieldRid,
		// 		_normalMapRid,
		// 		_params.Radius,
		// 		_minHeightCache,
		// 		_maxHeightCache
		// 	);
		// }
		// else
		// {
		// 	GD.PrintErr("[Planet] Falta asignar el nodo 'Renderer' en el Inspector.");
		// }

		// // Aquí es donde "rellenamos" el Env con los datos que generó el Baker
		// if (Env != null)
		// {
		// 	GD.Print("[Planet] Inicializando Environment System...");
			
		// 	// Le pasamos al Environment los mapas que acabamos de cocinar
		// 	Env.Initialize(
		// 		_rd, 
		// 		_heightMapRid,   // Para que sepa la altura del terreno
		// 		_vectorFieldRid, // Para que sepa las corrientes de viento/flujo
		// 		_params,         // Radio, semilla, etc.
		// 		gridResolution   // Resolución de la grilla 3D
		// 	);
			
		// 	// Opcional: Si quieres generar los POIs inmediatamente
		// 	// Env.SetupPoiBuffer(); 
		// 	// Env.CreateVisualPOIs();
		// }
		// else
		// {
		// 	GD.PrintErr("[Planet] CRÍTICO: La variable 'Env' es null. Asigna el nodo EnvironmentManager en el Inspector de Planet.tscn");
		// 	// Nota: No retornamos false aquí para permitir debugging visual del terreno, 
		// 	// pero los agentes fallarán.
		// }

		// // 5. GENERAR POIS
		// // Aquí ocurre la magia de la delegación.
		// // No creamos buffers aquí. Solo damos la orden.
		// // InitializePois(sharedPainter);

		// return true;
	}


	public override void _Ready()
	{
		if (!_isConfigured)
		{
			// Si alguien pone el planeta en la escena sin usar el Builder
			GD.PrintErr("[Planet] Error: Planeta no inicializado vía WorldBuilder.");
			return;
		}

		// 1. Setup de Workers
		SetupWorkers();

		// 2. Heavy Lifting (GPU Bake)
		GenerateTerrain();

		// 3. Visual Setup (Renderer)
		SetupRenderer();

		// 4. Environment & POIs (Ahora es seguro usar LookAt/GlobalPos)
		SetupEnvironment();

		GD.Print("[Planet] Planeta listo y renderizado.");
	}



	// --- SUB-RUTINAS ---

	private void SetupWorkers()
	{
		// Instanciamos el Baker si no existe
		if (_terrainBaker == null)
		{
			_terrainBaker = new PlanetBaker();
			_terrainBaker.Name = "Internal_Baker";
			AddChild(_terrainBaker); 
		}
		
		// Configuramos Baker
		_terrainBaker.BakerShaderFile = this.BakerShader;
		_terrainBaker.SetParams(_params);
	}

	private void GenerateTerrain()
	{
		// Ejecutar Compute Shader
		var bakeResult = _terrainBaker.Bake(_rd);

		// Guardar resultados
		_heightMapRid = bakeResult.HeightMapRid;
		_normalMapRid = bakeResult.NormalMapRid;
		_vectorFieldRid = bakeResult.VectorFieldRid;
		_minHeightCache = bakeResult.MinHeight;
		_maxHeightCache = bakeResult.MaxHeight;
	}

	private void SetupRenderer()
	{
		if (_planetMaterial == null)
		{
			GD.PrintErr("[Planet] Falta terrain material");
		}
		else
		{
			Renderer._planetMaterial = _planetMaterial;
		}

		if (Renderer != null)
		{
			Renderer.Initialize(
				_heightMapRid,
				_vectorFieldRid,
				_normalMapRid,
				_params.Radius,
				_minHeightCache,
				_maxHeightCache
			);
		}
	}

	private void SetupEnvironment()
	{
		if (Env != null)
		{
			// Ahora esto es SEGURO porque estamos dentro de _Ready
			Env.Initialize(
				_rd, 
				_heightMapRid, 
				_vectorFieldRid, 
				_params, 
				_gridResolution
			);
			
			// Si EnvironmentManager hace spawn visual dentro de Initialize,
			// ahora funcionará porque el Planeta ya tiene posición en el mundo.
			// Env.CreateVisualPOIs(); // Asegúrate de llamar a esto si lo habías sacado.
		}
		else
		{
			GD.PrintErr("[Planet] Env es null.");
		}

		// Si usas el PoiSystem interno:
		// InitializePois(_sharedPainter);
	}

	// --- MÉTODOS PRIVADOS ---


	private void UpdateVisuals()
	{
		if (Renderer == null)
		{
			GD.PrintErr("[Planet] No se asignó PlanetRender.");
			return;
		}

		// Delegamos la complejidad visual al componente especializado
		Renderer.Initialize(
			_heightMapRid,
			_vectorFieldRid, // Asegúrate de tener este RID
			_normalMapRid,
			_params.Radius,
			_minHeightCache,
			_maxHeightCache
		);
		
		// Si tienes biomas
		// Renderer.ApplyBiomeData(_currentBiome);
	}

	private void InitializePois(PoiPainter painter)
	{
		// Lazy Init
		if (_poiSystem == null) 
			_poiSystem = new PoiSystem(_rd, painter, PoiVisualScene);

		// ORDEN: "PoiSystem, genera puntos en MI superficie (PoiContainer) usando MIS datos (_params)"
		// PoiSystem se encarga de crear su propio Buffer y Textura internamente.
		_poiSystem.GeneratePois(_params, PoiContainer ?? this);
	}

	

	// private void UpdateTerrainVisuals()
	// {
	// 	if (TerrainVisualRoot == null) return;
		
	// 	// Obtener MeshInstance de forma segura
	// 	var meshInstance = TerrainVisualRoot as MeshInstance3D ?? TerrainVisualRoot.GetChildOrNull<MeshInstance3D>(0);
		
	// 	if (meshInstance == null)
	// 	{
	// 		GD.PrintErr("[Planet] No se encontró MeshInstance3D para aplicar texturas.");
	// 		return;
	// 	}

	// 	var mat = meshInstance.MaterialOverride as ShaderMaterial;
	// 	if (mat == null) mat = meshInstance.Mesh?.SurfaceGetMaterial(0) as ShaderMaterial;

	// 	if (mat != null)
	// 	{
	// 		// Pasamos las texturas vivas al material
	// 		mat.SetShaderParameter("height_map", TextureFromRid(_heightMapRid));
	// 		mat.SetShaderParameter("normal_map", TextureFromRid(_normalMapRid));
	// 		mat.SetShaderParameter("min_height", _minHeightCache);
	// 		mat.SetShaderParameter("max_height", _maxHeightCache);
	// 		mat.SetShaderParameter("radius", _params.Radius);
	// 	}
	// }




	private PlanetParamsData ValidateConfig(PlanetParamsData c)
	{
		c.WarpStrength = Mathf.Max(0.001f, c.WarpStrength);
		c.DetailFrequency = Mathf.Max(0.001f, c.DetailFrequency);
		// ... otras validaciones ...
		return c;
	}

	// --- HELPERS ---

	private Texture TextureFromRid(Rid rid)
	{
		// Helper para Godot 4.2+
		var tex = new TextureCubemapRD();
		tex.TextureRdRid = rid;
		return tex;
	}


	public bool RaycastHit(Vector3 globalOrigin, Vector3 globalDir, out Vector3 hitPoint)
	{
		hitPoint = Vector3.Zero;

		// 1. Convertir Rayo a Espacio Local
		// Esto permite que el planeta esté rotado o trasladado y el raycast siga funcionando.
		Vector3 localOrigin = ToLocal(globalOrigin);
		
		// Convertimos la dirección rotándola con la inversa del transform del planeta
		Vector3 localDir = (ToLocal(globalOrigin + globalDir) - localOrigin).Normalized();

		// 2. Intersección Analítica Rayo-Esfera (Bound Sphere Check)
		// Usamos el Radio Base como aproximación inicial.
		// Ecuación: ||O + tD||^2 = R^2  ->  t^2 + 2(O.D)t + (O.O - R^2) = 0
		float r = _params.Radius;
		
		float b = localOrigin.Dot(localDir);
		float c = localOrigin.Dot(localOrigin) - (r * r);
		float discriminant = (b * b) - c;

		// Si es negativo, el rayo pasó de largo
		if (discriminant < 0.0f) return false;

		// 3. Resolver distancia (t)
		// Usamos la resta (-sqrt) para obtener el punto de impacto más cercano a la cámara
		float t = -b - Mathf.Sqrt(discriminant);

		// Si t > 0, colisionamos frente a la cámara
		if (t > 0.0f)
		{
			// Punto de impacto en la esfera perfecta (Radio Base)
			Vector3 sphereHit = localOrigin + (localDir * t);
			
			// 4. REFINAMIENTO AAA (Height Correction)
			// Ahora consultamos al ruido: "¿Qué altura real tiene el terreno aquí?"
			Vector3 dirFromCenter = sphereHit.Normalized();
			
			// Usamos la misma matemática que el Baker para obtener la altura exacta
			float terrainHeight = GetHeightAtDirection(dirFromCenter);
			
			// Recalculamos la posición exacta sobre la superficie
			Vector3 terrainHit = dirFromCenter * (_params.Radius + terrainHeight);

			// 5. Convertir a Global y retornar
			hitPoint = ToGlobal(terrainHit);
			return true;
		}

		return false;
	}

	// Llama a esto AL FINAL de tu proceso de generación (SpawnWorld/Bake)
// En Planet.cs

	public void CacheHeightMapToCPU()
	{
		// Array de 6 imágenes (una por cara del cubo)
		_cpuHeightMapFaces = new Image[6];

		for (int i = 0; i < 6; i++)
		{
			// "i" es el índice de la cara (0 a 5)
			// Esto descarga la imagen de la VRAM a la RAM
			_cpuHeightMapFaces[i] = RenderingServer.Texture2DLayerGet(_heightMapRid, i);
		}
		
		if (_cpuHeightMapFaces[0] != null)
		{
			_heightMapSize = _cpuHeightMapFaces[0].GetWidth();
			GD.Print($"[Planet] HeightMap cacheado en CPU ({_heightMapSize}x{_heightMapSize} x 6 caras).");
		}
	}




	public float GetHeightAtDirection(Vector3 dir)
	{
		if (_cpuHeightMapFaces == null) return 0.0f;

		// 1. Determinar cara y UV (Matemática de Cubemap estándar)
		Vector3 absDir = dir.Abs();
		int faceIndex = 0;
		Vector2 uv = Vector2.Zero;
		float ma = 0; // Major Axis

		if (absDir.Z >= absDir.X && absDir.Z >= absDir.Y)
		{
			faceIndex = (dir.Z < 0) ? 5 : 4; // Back : Front (En Godot: 4=Front, 5=Back, orden estándar OpenGL varía)
			ma = 0.5f / absDir.Z;
			uv = new Vector2((dir.Z < 0 ? -dir.X : dir.X), -dir.Y);
		}
		else if (absDir.Y >= absDir.X)
		{
			faceIndex = (dir.Y < 0) ? 3 : 2; // Bottom : Top
			ma = 0.5f / absDir.Y;
			uv = new Vector2(dir.X, (dir.Y < 0 ? -dir.Z : dir.Z));
		}
		else
		{
			faceIndex = (dir.X < 0) ? 1 : 0; // Left : Right
			ma = 0.5f / absDir.X;
			uv = new Vector2((dir.X < 0 ? dir.Z : -dir.Z), -dir.Y);
		}

		// Convertir a coordenadas 0..1
		// Nota: El mapeo exacto de caras depende de cómo tu shader escribe el cubemap.
		// Si ves errores, rota las caras o invierte ejes aquí.
		uv = uv * ma + new Vector2(0.5f, 0.5f);

		// 2. Leer Píxel
		int x = (int)(uv.X * (_heightMapSize - 1));
		int y = (int)(uv.Y * (_heightMapSize - 1));
		
		// Clamp por seguridad
		x = Mathf.Clamp(x, 0, _heightMapSize - 1);
		y = Mathf.Clamp(y, 0, _heightMapSize - 1);

		// Leer el canal ROJO (R32Float)
		Color pixel = _cpuHeightMapFaces[faceIndex].GetPixel(x, y);
		float height01 = pixel.R; // O pixel.r dependiendo de la versión de Godot

		// 3. Descomprimir altura
		// El shader guarda altura normalizada o cruda? 
		// Si guardaste altura absoluta en R32F, úsala directo.
		// Si normalizaste (0..1), multiplica por NoiseHeight.
		return height01 * _params.NoiseHeight; 
	}


	public override void _ExitTree()
	{
		// Limpieza ordenada
		_poiSystem?.Dispose();
		
		// Liberamos las texturas del terreno que poseemos
		if (_heightMapRid.IsValid) _rd?.FreeRid(_heightMapRid);
		if (_normalMapRid.IsValid) _rd?.FreeRid(_normalMapRid);
		if (_vectorFieldRid.IsValid) _rd?.FreeRid(_vectorFieldRid);
		
		base._ExitTree();
	}
}
