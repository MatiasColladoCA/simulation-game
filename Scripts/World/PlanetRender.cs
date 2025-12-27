using Godot;
using System.Collections.Generic;

public partial class PlanetRender : Node3D
{
	// Resolución de la malla base (Topología).
	// 64 es suficiente si usas Tesselation en shader, 
	// 256 da muy buen detalle por vértice directo.
	[Export] public int BaseResolution = 256; 
	
	private MeshInstance3D _meshInstance;
	private ShaderMaterial _materialOverride;

	// Método llamado por AgentSimulationSphere (el Orquestador)
	public void Initialize(Rid heightMapRid, Rid vectorMapRid, float radius, float heightMultiplier)
	{
		// 1. Asegurar Material
		if (_materialOverride == null)
		{
			// Cargamos el shader visual que creamos antes
			var shader = GD.Load<Shader>("res://Shaders/Visual/planet_terrain.gdshader");
			_materialOverride = new ShaderMaterial();
			_materialOverride.Shader = shader;
		}

		// 2. Inyectar Texturas (Zero-Copy)
		// Usamos la API de TextureCubemapRD para conectar el RID de Vulkan con el Shader de Godot
		var hMap = new TextureCubemapRD { TextureRdRid = heightMapRid };
		
		// Asignamos parámetros al shader
		_materialOverride.SetShaderParameter("height_map_gpu", hMap);
		_materialOverride.SetShaderParameter("planet_radius", radius);
		_materialOverride.SetShaderParameter("noise_amplitude", heightMultiplier);

		// 3. Generar la Geometría Física (Solo si no existe)
		if (_meshInstance == null)
		{
			GenerateCubeSphere(radius);
		}
	}

	private void GenerateCubeSphere(float radius)
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		// Vectores base para construir un cubo
		Vector3[] directions = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back };
		
		// Ejes tangentes para iterar la superficie de cada cara
		// (Truco matemático para orientar los quads correctamente)
		Vector3[] axisA = { Vector3.Right, Vector3.Right, Vector3.Forward, Vector3.Back, Vector3.Right, Vector3.Left };
		Vector3[] axisB = { Vector3.Back, Vector3.Forward, Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };

		// Iteramos las 6 caras
		for (int i = 0; i < 6; i++)
		{
			Vector3 localUp = directions[i];
			Vector3 localRight = axisA[i];
			Vector3 localBottom = axisB[i];

			for (int y = 0; y < BaseResolution; y++)
			{
				for (int x = 0; x < BaseResolution; x++)
				{
					// Calculamos 4 puntos de un quad en la cara del cubo
					// Y los normalizamos inmediatamente para "inflarlos" a una esfera
					Vector3 p1 = ComputeSpherePoint(x, y, localUp, localRight, localBottom);
					Vector3 p2 = ComputeSpherePoint(x + 1, y, localUp, localRight, localBottom);
					Vector3 p3 = ComputeSpherePoint(x, y + 1, localUp, localRight, localBottom);
					Vector3 p4 = ComputeSpherePoint(x + 1, y + 1, localUp, localRight, localBottom);

					// Triángulo 1
					st.SetNormal(p1); st.SetUV(new Vector2(0, 0)); st.AddVertex(p1 * radius);
					st.SetNormal(p2); st.SetUV(new Vector2(1, 0)); st.AddVertex(p2 * radius);
					st.SetNormal(p3); st.SetUV(new Vector2(0, 1)); st.AddVertex(p3 * radius);

					// Triángulo 2
					st.SetNormal(p2); st.SetUV(new Vector2(1, 0)); st.AddVertex(p2 * radius);
					st.SetNormal(p4); st.SetUV(new Vector2(1, 1)); st.AddVertex(p4 * radius);
					st.SetNormal(p3); st.SetUV(new Vector2(0, 1)); st.AddVertex(p3 * radius);
				}
			}
		}

		// Generar Mesh y Optimizar
		st.Index();
		st.GenerateNormals(); 
		st.GenerateTangents();
		var mesh = st.Commit();

		// Instanciar en escena
		_meshInstance = new MeshInstance3D();
		_meshInstance.Name = "ProceduralPlanet";
		_meshInstance.Mesh = mesh;
		_meshInstance.MaterialOverride = _materialOverride; // Aquí ocurre la magia visual
		
		// Sombras
		_meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		
		AddChild(_meshInstance);
	}

	private Vector3 ComputeSpherePoint(int x, int y, Vector3 up, Vector3 axisA, Vector3 axisB)
	{
		// Convertir coordenadas de grilla (x,y) a porcentaje (0.0 a 1.0)
		Vector2 pct = new Vector2(x, y) / (float)BaseResolution;
		
		// Mapear al plano del cubo: Centro + (Offset X) + (Offset Y)
		// (pct - 0.5) * 2 centra el rango en -1 a 1
		Vector3 pointOnCube = up + (pct.X - 0.5f) * 2.0f * axisA + (pct.Y - 0.5f) * 2.0f * axisB;
		
		// Normalizar para convertir cubo -> esfera
		return pointOnCube.Normalized(); 
	}
}
