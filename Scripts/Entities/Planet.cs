


using Godot;
using System;

public partial class Planet : Node3D
{
	// --- ESTADO DEL MUNDO ---
	private PlanetParamsData _params;
	private RenderingDevice _rd;

	// private PoiPainter _sharedPainter; // Guardamos referencia
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
	// private PoiSystem _poiSystem; 

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
	// public Rid GetInfluenceTextureRid() => _poiSystem?.GetInfluenceTexture() ?? new Rid();
	// public Rid GetPoiBufferRid() => _poiSystem?.GetPoiBuffer() ?? new Rid();

	// [Export] public EnvironmentManager Env;
	// public EnvironmentManager Env;

	// Almacén en RAM de la altura (Solo lectura rápida)
	private Image[] _cpuHeightMapFaces;
	private int _heightMapSize;

	private float _logicResolution;



	

	// --- PUNTO DE ENTRADA ---
	public bool Initialize(RenderingDevice rd, PlanetParamsData config, Rid bakerShaderRid)
	{
		_rd = rd;
		_params = config;
		
		// 1. HORNEADO (Baking)
		// Ocurre AQUÍ, antes de que el jugador vea nada.
		// Instanciamos el Baker temporalmente, lo usamos y lo tiramos.
		var baker = new PlanetBaker(); 
		
		var result = baker.Bake(_rd, bakerShaderRid, _params);

		if (!result.Success)
		{
			GD.PrintErr("[Planet] Falló el horneado del terreno.");
			return false;
		}

		// 2. GUARDAR RESULTADOS
		_heightMapRid = result.HeightMapRid;
		_normalMapRid = result.NormalMapRid;
		_vectorFieldRid = result.VectorFieldRid;
		_minHeightCache = result.MinHeight;
		_maxHeightCache = result.MaxHeight;

		// 3. CACHÉ EN RAM (Para colisiones/agentes)
		SaveHeightMapToCache(result.HeightMapRawBytes, (int)_params.TextureResolution);

		_isConfigured = true;
		return true;
	}


	public override void _Ready()
	{
		if (!_isConfigured)
		{
			GD.PrintErr("[Planet] ERROR: Planeta no inicializado vía WorldBuilder.");
			return;
		}

		// 1. INICIALIZAR VISUALES
		// Ahora que tenemos datos (HeightMapRid) y estamos en la escena, 
		// le decimos al Renderer que dibuje.
		if (Renderer != null)
		{
			Renderer.Initialize(
				_heightMapRid,
				_normalMapRid, // VectorFieldRid si lo usas
				_vectorFieldRid,    // Placeholder si no usas VectorField
				_params.Radius,
				_minHeightCache,
				_maxHeightCache
			);
		}
		else
		{
			GD.PrintErr("[Planet] Falta asignar 'Renderer' en el Inspector.");
		}

		// 2. (FUTURO) INICIALIZAR POIS / ENVIRONMENT
		/*
		if (Env != null) {
			Env.Initialize(_rd, this, _params);
			Env.CreateVisuals();
		}
		*/

		GD.Print("[Planet] Sistema Online. Listo para simulación.");
	}


	// --- SUB-RUTINAS ---

	// private void SetupWorkers()
	// {
	// 	// Instanciamos el Baker si no existe
	// 	if (_terrainBaker == null)
	// 	{
	// 		_terrainBaker = new PlanetBaker();
	// 		_terrainBaker.Name = "Internal_Baker";
	// 		AddChild(_terrainBaker); 
	// 	}
		
	// 	// Configuramos Baker
	// 	_terrainBaker.BakerShaderFile = this.BakerShader;
	// 	// _terrainBaker.SetParams(_params);
	// }

	// private void GenerateTerrain()
	// {
	// 	// Ejecutar Compute Shader
	// 	var bakeResult = _terrainBaker.Bake(_rd, bakerShaderRid, _params);

	// 	// Guardar resultados
	// 	_heightMapRid = bakeResult.HeightMapRid;
	// 	_normalMapRid = bakeResult.NormalMapRid;
	// 	_vectorFieldRid = bakeResult.VectorFieldRid;
	// 	_minHeightCache = bakeResult.MinHeight;
	// 	_maxHeightCache = bakeResult.MaxHeight;
	// }

	// private void SetupRenderer()
	// {
	// 	if (_planetMaterial == null)
	// 	{
	// 		GD.PrintErr("[Planet] Falta terrain material");
	// 	}
	// 	else
	// 	{
	// 		Renderer._planetMaterial = _planetMaterial;
	// 	}

	// 	if (Renderer != null)
	// 	{
	// 		Renderer.Initialize(
	// 			_heightMapRid,
	// 			_vectorFieldRid,
	// 			_normalMapRid,
	// 			_params.Radius,
	// 			_minHeightCache,
	// 			_maxHeightCache
	// 		);
	// 	}
	// }

	// private void SetupEnvironment()
	// {
	// 	if (Env != null)
	// 	{
	// 		// Ahora esto es SEGURO porque estamos dentro de _Ready
	// 		Env.Initialize(
	// 			_rd, 
	// 			_heightMapRid, 
	// 			_vectorFieldRid, 
	// 			_params, 
	// 			_gridResolution
	// 		);
			
	// 		// Si EnvironmentManager hace spawn visual dentro de Initialize,
	// 		// ahora funcionará porque el Planeta ya tiene posición en el mundo.
	// 		// Env.CreateVisualPOIs(); // Asegúrate de llamar a esto si lo habías sacado.
	// 	}
	// 	else
	// 	{
	// 		GD.PrintErr("[Planet] Env es null.");
	// 	}

	// 	// Si usas el PoiSystem interno:
	// 	// InitializePois(_sharedPainter);
	// }

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

	// private void InitializePois(PoiPainter painter)
	// {
	// 	// Lazy Init
	// 	if (_poiSystem == null) 
	// 		_poiSystem = new PoiSystem(_rd, painter, PoiVisualScene);

	// 	// ORDEN: "PoiSystem, genera puntos en MI superficie (PoiContainer) usando MIS datos (_params)"
	// 	// PoiSystem se encarga de crear su propio Buffer y Textura internamente.
	// 	_poiSystem.GeneratePois(_params, PoiContainer ?? this);
	// }

	

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
		c.Radius = Mathf.Max(100.0f, c.Radius);
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

		// 1. Transformar Rayo a Espacio Local (Maneja rotación del planeta)
		Vector3 localOrigin = ToLocal(globalOrigin);
		Vector3 localDir = (ToLocal(globalOrigin + globalDir) - localOrigin).Normalized();

		// 2. Intersección Esfera Analítica (Radio Base)
		float r = _params.Radius;
		float b = localOrigin.Dot(localDir);
		float c = localOrigin.Dot(localOrigin) - (r * r);
		float discriminant = (b * b) - c;

		if (discriminant < 0.0f) return false; // Rayo falló la esfera

		// Distancia a la esfera base
		float t = -b - Mathf.Sqrt(discriminant);

		if (t > 0.0f)
		{
			Vector3 sphereHit = localOrigin + (localDir * t);
			
			// 3. Refinamiento con Altura Real
			Vector3 dirFromCenter = sphereHit.Normalized();
			float terrainHeight = GetHeightAtDirection(dirFromCenter);
			
			// Recalcular posición final: Radio Base + Altura del Terreno
			Vector3 terrainHit = dirFromCenter * (_params.Radius + terrainHeight);

			hitPoint = ToGlobal(terrainHit);
			return true;
		}

		return false;
	}



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

		// 1. Matemática de Cubemap para hallar UV y Cara
		Vector3 absDir = dir.Abs();
		int faceIndex = 0;
		Vector2 uv = Vector2.Zero;
		float ma = 0; 

		if (absDir.Z >= absDir.X && absDir.Z >= absDir.Y)
		{
			faceIndex = (dir.Z < 0) ? 5 : 4; 
			ma = 0.5f / absDir.Z;
			uv = new Vector2((dir.Z < 0 ? -dir.X : dir.X), -dir.Y);
		}
		else if (absDir.Y >= absDir.X)
		{
			faceIndex = (dir.Y < 0) ? 3 : 2; 
			ma = 0.5f / absDir.Y;
			uv = new Vector2(dir.X, (dir.Y < 0 ? -dir.Z : dir.Z));
		}
		else
		{
			faceIndex = (dir.X < 0) ? 1 : 0; 
			ma = 0.5f / absDir.X;
			uv = new Vector2((dir.X < 0 ? dir.Z : -dir.Z), -dir.Y);
		}

		uv = uv * ma + new Vector2(0.5f, 0.5f);

		// 2. Leer Pixel
		int x = (int)(uv.X * (_heightMapSize - 1));
		int y = (int)(uv.Y * (_heightMapSize - 1));
		
		// Clamp simple
		if (x < 0) x = 0; if (x >= _heightMapSize) x = _heightMapSize - 1;
		if (y < 0) y = 0; if (y >= _heightMapSize) y = _heightMapSize - 1;

		Color pixel = _cpuHeightMapFaces[faceIndex].GetPixel(x, y);

		// NOTA CRÍTICA:
		// Si tu shader escribe altura ABSOLUTA (metros), usa pixel.R.
		// Si escribe NORMALIZADA (0..1), usa pixel.R * _params.NoiseHeight.
		// Asumiremos Absoluta (R32F) por ser arquitectura AAA.
		return pixel.R; 
	}
	

	private void SaveHeightMapToCache(byte[] rawData, int resolution)
	{
		if (rawData == null || rawData.Length == 0) return;

		_heightMapSize = resolution;
		_cpuHeightMapFaces = new Image[6];
		
		// R32F = 4 bytes por pixel (float)
		// R16F = 2 bytes (half) -> Ajusta si cambiaste el formato
		int bytesPerPixel = 4; 
		int bytesPerFace = resolution * resolution * bytesPerPixel; 

		for (int i = 0; i < 6; i++)
		{
			// Extraer slice del array gigante
			byte[] faceBytes = new byte[bytesPerFace];
			Array.Copy(rawData, i * bytesPerFace, faceBytes, 0, bytesPerFace);

			// Crear Imagen
			_cpuHeightMapFaces[i] = Image.CreateFromData(
				resolution, 
				resolution, 
				false, 
				Image.Format.Rf, // R Float 32-bit
				faceBytes
			);
		}
		
		GD.Print($"[Planet] HeightMap cacheado en CPU ({resolution}px).");
	}

	public override void _ExitTree()
	{
		// Limpieza ordenada
		// _poiSystem?.Dispose();
		
		// Liberamos las texturas del terreno que poseemos
		if (_heightMapRid.IsValid) _rd?.FreeRid(_heightMapRid);
		if (_normalMapRid.IsValid) _rd?.FreeRid(_normalMapRid);
		if (_vectorFieldRid.IsValid) _rd?.FreeRid(_vectorFieldRid);
		
		base._ExitTree();
	}
}
