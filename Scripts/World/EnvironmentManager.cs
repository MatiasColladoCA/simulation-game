using Godot;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public partial class EnvironmentManager : Node
{
	[Export] public PackedScene PoiMeshScene; 
	private List<Node3D> _poiVisuals = new();

	public Rid HeightMap { get; private set; }
	public Rid VectorField { get; private set; }
	
	public Rid POIBuffer { get; private set; }
	public Rid InfluenceTexture { get; private set; }

	private RenderingDevice _rd;
	// Buffer local de datos (XYZ=Dir, W=Radio Normalizado)
	private Vector4[] _poisData = new Vector4[16];

	private PlanetParamsData _config;

	public void Initialize(RenderingDevice rd, Rid heightMap, Rid vectorField, PlanetParamsData config)
	{
		_rd = rd;
		HeightMap = heightMap;
		VectorField = vectorField;
		_config = config;
		
		SetupPOIBuffer();
		CreateVisualPOIs();
	}

	private void SetupPOIBuffer()
	{
		Random rnd = new Random();

		for (int i = 0; i < _poisData.Length; i++)
		{
			// 1. Generar dirección aleatoria
			float theta = (float)(rnd.NextDouble() * 2.0 * Math.PI);
			float phi = (float)(Math.Acos(2.0 * rnd.NextDouble() - 1.0));
			
			float x = (float)(Math.Sin(phi) * Math.Cos(theta));
			float y = (float)(Math.Sin(phi) * Math.Sin(theta));
			float z = (float)(Math.Cos(phi));

			Vector3 direction = new Vector3(x, y, z).Normalized();
			
			// Radio de influencia deseado en metros (ej. 40m)
			float physicalRadius = 30.0f; 

			// --- CORRECCIÓN CRÍTICA ---
			// Para el Shader: Convertimos radio físico a "espacio unitario".
			// Si el planeta mide 100 y el POI 40, la influencia es 0.4.
			float normalizedInfluence = physicalRadius / _config.Radius;

			// Guardamos: Dirección (XYZ) normalizada y Radio (W) normalizado
			_poisData[i] = new Vector4(direction.X, direction.Y, direction.Z, normalizedInfluence);
		}

		byte[] data = MemoryMarshal.AsBytes(_poisData.AsSpan()).ToArray();
		POIBuffer = _rd.StorageBufferCreate((uint)data.Length, data);
	}

// En EnvironmentManager.cs

	private void CreateVisualPOIs()
	{
		if (PoiMeshScene == null) return;

		foreach (var v in _poiVisuals) v.QueueFree();
		_poiVisuals.Clear();

		for (int i = 0; i < _poisData.Length; i++)
		{
			if (_poisData[i].W <= 0.0001f) continue;
			
			var instance = PoiMeshScene.Instantiate<Node3D>();
			AddChild(instance);

			Vector3 direction = new Vector3(_poisData[i].X, _poisData[i].Y, _poisData[i].Z).Normalized();

			// 1. CALCULO DE ALTURA
			// Asegúrate de que _config tenga los mismos valores que usó el Baker.
			// Si cambiaste valores en el Inspector sin reiniciar, esto fallará.
			float finalRadius = TerrainNoise.GetTerrainAAA(direction, _config);

			// 2. POSICIONAMIENTO
			instance.GlobalPosition = direction * finalRadius;

			// 3. ORIENTACIÓN (LookAt)
			// Hacemos que el objeto "mire" hacia afuera del planeta para alinearse con la superficie
			// (Asumiendo que el eje Y del modelo apunta arriba)
			Vector3 upVector = direction; 
			
			// Truco: Mirar desde el centro (0,0,0) hacia la posición del objeto
			// Esto alinea el eje -Z del objeto con la normal de la esfera
			if (Mathf.Abs(direction.Dot(Vector3.Up)) > 0.99f)
				instance.LookAt(instance.GlobalPosition * 2.0f, Vector3.Right);
			else
				instance.LookAt(instance.GlobalPosition * 2.0f, Vector3.Up);

			_poiVisuals.Add(instance);
		}
	}





	public void ToggleVisuals(bool visible)
	{
		foreach (var v in _poiVisuals) v.Visible = visible;
	}

	public void UpdatePOIs(Vector4[] newPois)
	{
		_poisData = newPois;
		
		// Asegurarse de que newPois venga con W normalizado desde el controlador
		// O renormalizar aquí si fuera necesario. Asumimos que Main ya manda datos correctos.
		
		byte[] data = MemoryMarshal.AsBytes(_poisData.AsSpan()).ToArray();
		_rd.BufferUpdate(POIBuffer, 0, (uint)data.Length, data);
		
		CreateVisualPOIs();
	}

	public void SetInfluenceTexture(Rid influenceTex)
	{
		InfluenceTexture = influenceTex;
	}
}
