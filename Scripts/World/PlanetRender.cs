using Godot;
using System.Collections.Generic;

public partial class PlanetRender : Node3D
{
	// LOD Settings
	[Export] public int PatchResolution = 32; // Resolución de cada parche (16x16 o 32x32)
	[Export(PropertyHint.Range, "0.0, 1.0")] public float WaterLevel = 0.45f; 

	private Mesh _patchMesh; // Malla base reutilizable
	private ShaderMaterial _planetMaterial;
	private PlanetChunk[] _rootChunks; // Las 6 caras del cubo
	private MeshInstance3D _waterMesh;

	// Estado
	private float _currentRadius;
	private float _currentNoiseHeight;
	private float _lastWaterLevel = -1;

	private bool _isViewVectorField = false;
	private bool _isViewPoiField = false;

	private Rid _vectorFieldRid;
	private Rid _normalMapRid;


	// --- REEMPLAZAR FIRMA Y ASIGNACIONES EN PlanetRender.cs ---
	// public void Initialize(Rid heightMapRid, Rid vectorMapRid, float radius, float heightMultiplier)
	public void Initialize(Rid heightMapRid, Rid vectorMapRid, Rid normalMapRid, PlanetParamsData config)
	{
		// _currentRadius = radius;
		// _currentNoiseHeight = heightMultiplier;
		_currentRadius = config.Radius;
		_currentNoiseHeight = config.NoiseHeight;

		// 1. Configurar Material
		// 1. Configurar Material
		if (_planetMaterial == null)
		{
			_planetMaterial = new ShaderMaterial();
			_planetMaterial.Shader = GD.Load<Shader>("res://Shaders/Visual/planet_render.gdshader");
		}
		
		// 2. TEXTURAS (GPU)
		// Nota: TextureCubemapRD es vital para que Godot entienda RIDs de Vulkan
		var hMap = new TextureCubemapRD { TextureRdRid = heightMapRid };
		_planetMaterial.SetShaderParameter("height_map_gpu", hMap);

		var vMap = new TextureCubemapRD { TextureRdRid = vectorMapRid };
		_planetMaterial.SetShaderParameter("vector_field_gpu", vMap);

		var nMap = new TextureCubemapRD { TextureRdRid = normalMapRid };
		_planetMaterial.SetShaderParameter("normal_map_gpu", nMap);

		// 3. PARÁMETROS FÍSICOS CONSTANTES
		_planetMaterial.SetShaderParameter("planet_radius", config.Radius);
		
		// ELIMINADO: "noise_amplitude" (El shader ya lee altura real en metros del canal R)
		// ELIMINADO: "water_level_norm" (El shader espera metros absolutos)
		
		// Inicialización segura de valores absolutos (se sobreescribirán en SetBiomeLevels)
		_planetMaterial.SetShaderParameter("min_height_absolute", 0.0f);
		_planetMaterial.SetShaderParameter("max_height_absolute", config.NoiseHeight);

		// Uniforms Globales
		// var hMap = new TextureCubemapRD { TextureRdRid = heightMapRid };
		// _planetMaterial.SetShaderParameter("height_map_gpu", hMap);
		
		// _planetMaterial.SetShaderParameter("planet_radius", radius);
		// _planetMaterial.SetShaderParameter("noise_amplitude", heightMultiplier);
		// _planetMaterial.SetShaderParameter("planet_radius", config.Radius);
		// _planetMaterial.SetShaderParameter("noise_amplitude", config.NoiseHeight);
		
		// _planetMaterial.SetShaderParameter("water_level_norm", WaterLevel);

		// _vectorFieldRid = vectorMapRid;
		// var vMap = new TextureCubemapRD { TextureRdRid = vectorMapRid };
		// _planetMaterial.SetShaderParameter("vector_field_gpu", vMap);

		// _normalMapRid = normalMapRid;
		// var nMap = new TextureCubemapRD { TextureRdRid = normalMapRid };
		// _planetMaterial.SetShaderParameter("normal_map_gpu", nMap);



		// 2. Generar Mesh Base (Una sola vez)
		if (_patchMesh == null) GeneratePatchMesh();

		// 3. Inicializar Sistema Quadtree
		// InitializeQuadtree(radius);
		InitializeQuadtree(config.Radius);

		// 4. Agua
		UpdateWaterVisuals(true);

		// VERIFICACIÓN CRÍTICA:
		var meshInstance = GetNodeOrNull<MeshInstance3D>("PlanetMesh");
		if (meshInstance != null) {
			meshInstance.MaterialOverride = _planetMaterial;
		}
	}


	private void GeneratePatchMesh()
	{
		// Crea un plano simple XY de 1x1 subdividido
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		
		int res = PatchResolution;
		float step = 1.0f / res;
		
		for (int y = 0; y <= res; y++)
		{
			for (int x = 0; x <= res; x++)
			{
				st.SetUV(new Vector2(x * step, y * step));
				// Vértice plano en rango 0..1 (se ajustará en shader)
				st.AddVertex(new Vector3(x * step - 0.5f, y * step - 0.5f, 0));
			}
		}

		// Indices
		for (int y = 0; y < res; y++)
		{
			for (int x = 0; x < res; x++)
			{
				int i = y * (res + 1) + x;
				st.AddIndex(i);
				st.AddIndex(i + 1);
				st.AddIndex(i + res + 1);
				
				st.AddIndex(i + 1);
				st.AddIndex(i + res + 2);
				st.AddIndex(i + res + 1);
			}
		}
		st.GenerateNormals();

		
		_patchMesh = st.Commit();
	}

	private void InitializeQuadtree(float radius)
	{
		// Limpiar árbol anterior si existe
		if (_rootChunks != null)
		{
			foreach (var c in _rootChunks) c.FreeRecursively();
		}

		// Configurar estáticos de la clase Chunk
		PlanetChunk.BaseMesh = _patchMesh;
		PlanetChunk.ChunkMaterial = _planetMaterial;
		PlanetChunk.Radius = radius;
		PlanetChunk.RootNode = this;

		// Crear las 6 caras raíz
		_rootChunks = new PlanetChunk[6];
		Vector3[] dirs = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back };
		Vector3[] axA = { Vector3.Right, Vector3.Right, Vector3.Forward, Vector3.Back, Vector3.Right, Vector3.Left };
		Vector3[] axB = { Vector3.Back, Vector3.Forward, Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };

		for (int i = 0; i < 6; i++)
		{
			_rootChunks[i] = new PlanetChunk(null, dirs[i], axA[i], axB[i], 2.0f, 0);
		}
	}

	public override void _Process(double delta)
	{
		// Actualizar Agua (Dirty flag)
		if (!Mathf.IsEqualApprox(WaterLevel, _lastWaterLevel)) UpdateWaterVisuals(false);

		// --- ACTUALIZACIÓN DE QUADTREE ---
		if (_rootChunks == null) return;

		// 1. Obtener cámara
		var cam = GetViewport().GetCamera3D();
		if (cam == null) return;

		// 2. Pasar datos al sistema de chunks
		PlanetChunk.CameraPos = cam.GlobalPosition;

		// 3. Actualizar árbol (Split/Merge)
		foreach (var chunk in _rootChunks) chunk.Update();	
	}


	public void SetViewVectorField(bool visible)
	{
		_isViewVectorField = visible;
		if (_planetMaterial != null)
		{
			_planetMaterial.SetShaderParameter("view_vector_field", _isViewVectorField);
		}
	}


	// // --- NUEVO MÉTODO PARA RECIBIR LA TEXTURA ---
	// public void SetInfluenceMap(Rid influenceMapRid)
	// {
	// 	if (_planetMaterial == null) return;

	// 	// IMPORTANTE: TextureCubemapRD para que el shader entienda que es un Cubemap
	// 	var texWrapper = new TextureCubemapRD();
	// 	texWrapper.TextureRdRid = influenceMapRid;

	// 	_planetMaterial.SetShaderParameter("influence_texture", texWrapper);
	// }

	// Este método lo llama SimulationController.cs en el paso 5 del _Ready
	public void SetBiomeLevels(float waterLevelAbs, float snowLevelAbs, float minHeight, float maxHeight)
	{
		if (_planetMaterial == null) return;

		_planetMaterial.SetShaderParameter("water_level", waterLevelAbs);
		_planetMaterial.SetShaderParameter("snow_level", snowLevelAbs);
		_planetMaterial.SetShaderParameter("min_height_absolute", minHeight);
		_planetMaterial.SetShaderParameter("max_height_absolute", maxHeight);
	}

	public void SetInfluenceMap(Rid influenceRid)
	{
		if (_planetMaterial == null) return;
		var iMap = new TextureCubemapRD { TextureRdRid = influenceRid };
		_planetMaterial.SetShaderParameter("influence_texture", iMap);
	}

	public void SetViewPoiField(bool visible)
	{
		_isViewPoiField = visible;
		if (_planetMaterial != null)
		{
			_planetMaterial.SetShaderParameter("view_poi_field", _isViewPoiField);
		}
		// Ya no intentamos crear la textura aquí, se usa la que se pasó en SetInfluenceMap
		GD.Print($"[PlanetRender] Debug POI: {visible}");
	}




	private void UpdateWaterVisuals(bool force)
	{
		_lastWaterLevel = WaterLevel;
		if (_planetMaterial != null) _planetMaterial.SetShaderParameter("water_level_norm", WaterLevel);

		float r = _currentRadius + (_currentNoiseHeight * WaterLevel);
		
		if (_waterMesh == null) {
			_waterMesh = new MeshInstance3D { Name = "WaterSphere" };
			AddChild(_waterMesh);
			var mat = new StandardMaterial3D();
			mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			mat.AlbedoColor = new Color(0.0f, 0.2f, 0.8f, 0.6f);
			mat.Roughness = 0.1f; mat.Metallic = 0.5f;
			mat.EmissionEnabled = true; mat.Emission = new Color(0.0f, 0.1f, 0.3f);
			mat.EmissionEnergyMultiplier = 0.5f;
			_waterMesh.MaterialOverride = mat;
			_waterMesh.Mesh = new SphereMesh();
		}

		if (_waterMesh.Mesh is SphereMesh s) {
			s.Radius = r; s.Height = r * 2;
		}
	}


	// Añadir dentro de PlanetRender.cs
	public void ApplyBiomeData(PlanetBiomeData biome)
	{
		if (_planetMaterial == null) return;

		// Inyectar Paleta de Colores
		_planetMaterial.SetShaderParameter("color_deep_sea", biome.DeepSeaColor);
		_planetMaterial.SetShaderParameter("color_shallow", biome.ShallowColor);
		_planetMaterial.SetShaderParameter("color_beach", biome.BeachColor);
		_planetMaterial.SetShaderParameter("color_grass", biome.GrassColor);
		_planetMaterial.SetShaderParameter("color_rock", biome.RockColor);
		_planetMaterial.SetShaderParameter("color_snow", biome.SnowColor);

		// Inyectar Configuración Visual
		_planetMaterial.SetShaderParameter("ocean_depth_color_range", biome.OceanDepthMultipler);
		// Si añades roughness al shader como uniform, asígnalo aquí también
	}
	
}
