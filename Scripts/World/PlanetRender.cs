using Godot;

public partial class PlanetRender : Node3D
{
	[Export] public int BaseResolution = 256;
	
	// AHORA ESTO VIVE AQUÍ (Autoridad del Entorno)
	[Export(PropertyHint.Range, "0.0, 1.0")] 
	public float WaterLevel = 0.45f; 

	private MeshInstance3D _terrainMesh;
	private MeshInstance3D _waterMesh; // Referencia a la esfera de agua
	private ShaderMaterial _terrainMaterial;

	// Variables de estado del mundo (para que otros las lean si hace falta)
	public float CurrentRadius { get; private set; }
	public float CurrentNoiseHeight { get; private set; }

	public void Initialize(Rid heightMapRid, Rid vectorMapRid, float radius, float heightMultiplier)
	{
		CurrentRadius = radius;
		CurrentNoiseHeight = heightMultiplier;

		// 1. Configurar Material Terreno
		if (_terrainMaterial == null)
		{
			var shader = GD.Load<Shader>("res://Shaders/Visual/planet_terrain.gdshader");
			_terrainMaterial = new ShaderMaterial();
			_terrainMaterial.Shader = shader;
		}

		var hMap = new TextureCubemapRD { TextureRdRid = heightMapRid };
		_terrainMaterial.SetShaderParameter("height_map_gpu", hMap);
		_terrainMaterial.SetShaderParameter("planet_radius", radius);
		_terrainMaterial.SetShaderParameter("noise_amplitude", heightMultiplier);
		
		// Usamos NUESTRA variable WaterLevel
		_terrainMaterial.SetShaderParameter("water_level_norm", WaterLevel);

		// 2. Generar Terreno (Si no existe)
		if (_terrainMesh == null)
		{
			GenerateCubeSphere(radius);
		}
		else 
		{
			_terrainMesh.MaterialOverride = _terrainMaterial;
		}

		// 3. GENERAR/ACTUALIZAR AGUA (Lógica movida aquí)
		UpdateWaterSphere();
	}

	// Método para ser llamado si cambias el slider en tiempo real desde el editor
	public override void _Process(double delta)
	{
		// En modo editor o debug, actualizamos el shader si el slider cambia
		if (_terrainMaterial != null)
		{
			 _terrainMaterial.SetShaderParameter("water_level_norm", WaterLevel);
		}
		
		// Actualizar geometría del agua si cambia el nivel
		if (_waterMesh != null)
		{
			 float waterRadius = CurrentRadius + (CurrentNoiseHeight * WaterLevel);
			 if (_waterMesh.Mesh is SphereMesh sphere && Mathf.Abs(sphere.Radius - waterRadius) > 0.01f)
			 {
				 sphere.Radius = waterRadius;
				 sphere.Height = waterRadius * 2.0f;
			 }
		}
	}

	private void UpdateWaterSphere()
	{
		if (_waterMesh == null)
		{
			_waterMesh = new MeshInstance3D { Name = "WaterSphere" };
			AddChild(_waterMesh);

			// Material de agua
			var waterMat = new StandardMaterial3D();
			waterMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			waterMat.AlbedoColor = new Color(0.0f, 0.2f, 0.8f, 0.6f); 
			waterMat.Roughness = 0.1f; 
			waterMat.Metallic = 0.5f;  
			waterMat.EmissionEnabled = true;
			waterMat.Emission = new Color(0.0f, 0.1f, 0.3f); 
			waterMat.EmissionEnergyMultiplier = 0.5f;
			_waterMesh.MaterialOverride = waterMat;
		}

		float waterRadius = CurrentRadius + (CurrentNoiseHeight * WaterLevel);
		_waterMesh.Mesh = new SphereMesh { Radius = waterRadius, Height = waterRadius * 2.0f };
	}



	private void GenerateCubeSphere(float radius)
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		// Vectores y lógica matemática (EXACTAMENTE IGUAL QUE ANTES)
		Vector3[] directions = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back };
		Vector3[] axisA = { Vector3.Right, Vector3.Right, Vector3.Forward, Vector3.Back, Vector3.Right, Vector3.Left };
		Vector3[] axisB = { Vector3.Back, Vector3.Forward, Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };

		for (int i = 0; i < 6; i++)
		{
			Vector3 localUp = directions[i];
			Vector3 localRight = axisA[i];
			Vector3 localBottom = axisB[i];

			for (int y = 0; y < BaseResolution; y++)
			{
				for (int x = 0; x < BaseResolution; x++)
				{
					Vector3 p1 = ComputeSpherePoint(x, y, localUp, localRight, localBottom);
					Vector3 p2 = ComputeSpherePoint(x + 1, y, localUp, localRight, localBottom);
					Vector3 p3 = ComputeSpherePoint(x, y + 1, localUp, localRight, localBottom);
					Vector3 p4 = ComputeSpherePoint(x + 1, y + 1, localUp, localRight, localBottom);

					st.SetNormal(p1); st.SetUV(new Vector2(0, 0)); st.AddVertex(p1 * radius);
					st.SetNormal(p2); st.SetUV(new Vector2(1, 0)); st.AddVertex(p2 * radius);
					st.SetNormal(p3); st.SetUV(new Vector2(0, 1)); st.AddVertex(p3 * radius);
					st.SetNormal(p2); st.SetUV(new Vector2(1, 0)); st.AddVertex(p2 * radius);
					st.SetNormal(p4); st.SetUV(new Vector2(1, 1)); st.AddVertex(p4 * radius);
					st.SetNormal(p3); st.SetUV(new Vector2(0, 1)); st.AddVertex(p3 * radius);
				}
			}
		}

		st.Index();
		st.GenerateNormals(); 
		st.GenerateTangents();
		var mesh = st.Commit();

		// --- AQUÍ ESTÁ EL CAMBIO ---
		// Usamos '_terrainMesh' en lugar de '_meshInstance'
		// Usamos '_terrainMaterial' en lugar de '_materialOverride'
		
		_terrainMesh = new MeshInstance3D();
		_terrainMesh.Name = "ProceduralPlanet";
		_terrainMesh.Mesh = mesh;
		_terrainMesh.MaterialOverride = _terrainMaterial; 
		
		_terrainMesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		
		AddChild(_terrainMesh);
	}



	private Vector3 ComputeSpherePoint(int x, int y, Vector3 up, Vector3 axisA, Vector3 axisB)
	{
		Vector2 pct = new Vector2(x, y) / (float)BaseResolution;
		Vector3 pointOnCube = up + (pct.X - 0.5f) * 2.0f * axisA + (pct.Y - 0.5f) * 2.0f * axisB;
		return pointOnCube.Normalized(); 
	}
}
