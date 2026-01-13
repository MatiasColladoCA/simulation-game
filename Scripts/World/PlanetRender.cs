using Godot;
using System.Collections.Generic;
// using Scripts.Contracts;

public partial class PlanetRender : Node3D
{
	// LOD Settings
	[Export] public int PatchResolution = 32; // Resolución de cada parche (16x16 o 32x32)
	// [Export(PropertyHint.Range, "0.0, 1.0")] public float WaterLevel = 0.45f; 

	private Mesh _patchMesh; // Malla base reutilizable
	private ShaderMaterial _planetMaterial;
	private PlanetChunk[] _rootChunks; // Las 6 caras del cubo
	private MeshInstance3D _waterMesh;

	// Estado
	private float _currentRadius;
	private float _currentNoiseHeight;
	// private float _lastWaterLevel = -1;

	private bool _isViewVectorField = false;
	private bool _isViewPoiField = false;

	private Rid _vectorFieldRid;
	private Rid _normalMapRid;

	// private float _waterLevel;
	// private float _snowLevel;

	private float _maxHeight;



	// --- REEMPLAZAR FIRMA Y ASIGNACIONES EN PlanetRender.cs ---
	public void Initialize(
		Rid heightMapRid, 
		Rid vectorFieldRid, 
		Rid normalMapRid, 
		float radius, 
		float minHeight, 
		float maxHeight
		// PlanetParamsData visualData // (Opcional) Colores, texturas de bioma
	)
	{
		_currentRadius = radius;
		_maxHeight = maxHeight;


		// 1. Configurar Material
		if (_planetMaterial == null)
		{
			_planetMaterial = new ShaderMaterial();
			_planetMaterial.Shader = GD.Load<Shader>("res://Shaders/Visual/planet_render.gdshader");
		}
		
		// 2. TEXTURAS (GPU)
		// Nota: TextureCubemapRD es vital para que Godot entienda RIDs de Vulkan

		
		// 2. VINCULAR TEXTURAS DE VULKAN (RD -> Godot)
		// TextureCubemapRD actúa como puente para usar RIDs de Compute en Shaders visuales
		var hTex = new TextureCubemapRD { TextureRdRid = heightMapRid };
		var vTex = new TextureCubemapRD { TextureRdRid = vectorFieldRid };
		var nTex = new TextureCubemapRD { TextureRdRid = normalMapRid };

		_planetMaterial.SetShaderParameter("height_map_gpu", hTex);
		_planetMaterial.SetShaderParameter("vector_field_gpu", vTex);
		_planetMaterial.SetShaderParameter("normal_map_gpu", nTex);

		// 3. PARÁMETROS FÍSICOS
		_planetMaterial.SetShaderParameter("planet_radius", radius);
		_planetMaterial.SetShaderParameter("min_height_absolute", minHeight);
		_planetMaterial.SetShaderParameter("max_height_absolute", maxHeight);


		// 2. Generar Mesh Base (Una sola vez)
		if (_patchMesh == null) GeneratePatchMesh();

		InitializeWater();


		// 4. Agua
		// UpdateWaterVisuals(true);

		// 5. ASIGNAR AL MESH
		var meshInstance = GetNodeOrNull<MeshInstance3D>("PlanetMesh");
		if (meshInstance == null)
		{
			// Si no existe, créalo o búscalo dinámicamente
			meshInstance = new MeshInstance3D();
			meshInstance.Name = "PlanetMesh";
			AddChild(meshInstance);
			// Aquí deberías generar tu QuadTree o CubeSphere mesh
			// GeneratePatchMesh(); 
		}
		meshInstance.MaterialOverride = _planetMaterial;


		// 3. Inicializar Sistema Quadtree
		// InitializeQuadtree(radius);
		InitializeQuadtree(radius);

	}


	// --- AÑADIR estos dos nuevos métodos en PlanetRender.cs ---

	// Crea la malla y el material del agua una sola vez.
	private void InitializeWater()
	{
		// Crear el MeshInstance3D si no existe
		if (_waterMesh == null)
		{
			_waterMesh = new MeshInstance3D { Name = "WaterSphere" };
			AddChild(_waterMesh);
			
			// Material básico para el agua (puedes reemplazarlo por un shader personalizado más tarde)
			var waterMaterial = new StandardMaterial3D();
			waterMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			waterMaterial.AlbedoColor = new Color(0.0f, 0.3f, 0.6f, 0.7f);
			waterMaterial.Roughness = 0.1f;
			waterMaterial.Metallic = 0.8f;
			// Un poco de emisión para que no sea completamente negro en la sombra
			waterMaterial.EmissionEnabled = true; 
			waterMaterial.Emission = new Color(0.0f, 0.1f, 0.2f);
			waterMaterial.EmissionEnergyMultiplier = 0.3f;

			_waterMesh.MaterialOverride = waterMaterial;
			_waterMesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; // El agua no proyecta sombra
			_waterMesh.Mesh = new SphereMesh();
		}
	}

	// Actualiza el radio de la esfera del agua para que coincida con el nivel del mar.
	// <param name="waterLevelAbsolute">La altura absoluta del nivel del mar en unidades del mundo.</param>
	private void UpdateWaterRadius(float waterLevelAbsolute)
	{
		if (_waterMesh != null && _waterMesh.Mesh is SphereMesh sphere)
		{
			// El radio de la esfera de agua es simplemente la altura absoluta del nivel del mar.
			// float waterSphereRadius = (_currentRadius * 0.5f) + waterLevelAbsolute;
			sphere.Radius = waterLevelAbsolute;
			sphere.Height = waterLevelAbsolute * 2.0f;
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

	// --- REEMPLAZAR el método _Process ---
	public override void _Process(double delta)
	{
		// --- ACTUALIZACIÓN DE QUADTREE ---
		if (_rootChunks == null) return;

		var cam = GetViewport().GetCamera3D();
		if (cam == null) return;

		PlanetChunk.CameraPos = cam.GlobalPosition;

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


	// Este método lo llama Main.cs en el paso 5 del _Ready
	public void UpdateEnvironmentLevels(float waterLevelAbs, float snowLevelAbs, float minHeight, float maxHeight)
{
	// Actualizar el shader del terreno con los niveles
	if (_planetMaterial != null)
	{
		_planetMaterial.SetShaderParameter("water_level", waterLevelAbs);
		_planetMaterial.SetShaderParameter("snow_level", snowLevelAbs);
	}

	// <-- CAMBIO CLAVE: Actualizar la geometría del agua -->
	UpdateWaterRadius(waterLevelAbs);
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
