using Godot;
using System.Collections.Generic;

public partial class PlanetRender : Node3D
{
	// LOD Settings
	[Export] public int PatchResolution = 16; // Resolución de cada parche (16x16 o 32x32)
	[Export(PropertyHint.Range, "0.0, 1.0")] public float WaterLevel = 0.45f; 

	private Mesh _patchMesh; // Malla base reutilizable
	private ShaderMaterial _terrainMaterial;
	private PlanetChunk[] _rootChunks; // Las 6 caras del cubo
	private MeshInstance3D _waterMesh;

	// Estado
	private float _currentRadius;
	private float _currentNoiseHeight;
	private float _lastWaterLevel = -1;

	public void Initialize(Rid heightMapRid, Rid vectorMapRid, float radius, float heightMultiplier)
	{
		_currentRadius = radius;
		_currentNoiseHeight = heightMultiplier;

		// 1. Configurar Material
		if (_terrainMaterial == null)
		{
			_terrainMaterial = new ShaderMaterial();
			_terrainMaterial.Shader = GD.Load<Shader>("res://Shaders/Visual/planet_terrain.gdshader");
		}
		
		// Uniforms Globales
		var hMap = new TextureCubemapRD { TextureRdRid = heightMapRid };
		_terrainMaterial.SetShaderParameter("height_map_gpu", hMap);
		_terrainMaterial.SetShaderParameter("planet_radius", radius);
		_terrainMaterial.SetShaderParameter("noise_amplitude", heightMultiplier);
		_terrainMaterial.SetShaderParameter("water_level_norm", WaterLevel);

		// 2. Generar Mesh Base (Una sola vez)
		if (_patchMesh == null) GeneratePatchMesh();

		// 3. Inicializar Sistema Quadtree
		InitializeQuadtree(radius);

		// 4. Agua
		UpdateWaterVisuals(true);
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
		PlanetChunk.ChunkMaterial = _terrainMaterial;
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

	private void UpdateWaterVisuals(bool force)
	{
		_lastWaterLevel = WaterLevel;
		if (_terrainMaterial != null) _terrainMaterial.SetShaderParameter("water_level_norm", WaterLevel);

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
}
