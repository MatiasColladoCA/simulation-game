using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class PoiSystem : IDisposable
{
	// --- DEPENDENCIAS ---
	private RenderingDevice _rd;
	private PoiPainter _painter;
	private PackedScene _visualScene;

	// --- RECURSOS GPU ---
	private Rid _paramsBufferRid;
	private Rid _poiBufferRid;
	private Rid _influenceTextureRid;

	// --- TRACKING ---
	private List<Node3D> _spawnedVisuals = new List<Node3D>();

	// Constructor
	public PoiSystem(RenderingDevice rd, PoiPainter painter, PackedScene visualScene)
	{
		_rd = rd;
		_painter = painter;
		_visualScene = visualScene;
	}

	public Rid GetInfluenceTexture() => _influenceTextureRid;
	public Rid GetPoiBuffer() => _poiBufferRid;

	// --- LÓGICA PRINCIPAL ---
	public void GeneratePois(PlanetParamsData config, Node3D parentPlanet)
	{
		// 1. Limpieza previa (si regeneramos en runtime)
		CleanupVisuals();

		// 2. Crear Textura de Influencia (Cubemap Array 6 caras)
		CreateInfluenceTexture((uint)config.ResolutionF);

		// 3. Generar Datos
		var gpuData = new List<Vector4>();
		
		// Usamos la semilla del offset para que los POIs sean deterministas con el planeta
		int seed = (int)(config.NoiseOffset.X + config.NoiseOffset.Y + config.NoiseOffset.Z);
		var rng = new Random(seed);

		int poiCount = 50; // Podrías pasarlo en config si quisieras

		for (int i = 0; i < poiCount; i++)
		{
			// A. Calcular posición lógica
			Vector3 dir = GetRandomDirection(rng);
			float terrainHeight = TerrainNoise.GetTerrainHeight(dir, config);
			Vector3 surfacePos = dir * (config.Radius + terrainHeight);

			// B. Instanciar Visual (CPU)
			if (_visualScene != null)
			{
				var instance = _visualScene.Instantiate<Node3D>();
				parentPlanet.AddChild(instance); // El planeta es el dueño
				
				instance.Position = surfacePos; // Posición local relativa al planeta
				LookAtOutwards(instance, surfacePos);
				
				_spawnedVisuals.Add(instance);
			}

			// C. Datos para GPU (Influence Map)
			// W = Radio de influencia normalizado (Ej: 30m / 1000m = 0.03)
			float influenceRadiusNorm = 30.0f / config.Radius;
			gpuData.Add(new Vector4(dir.X, dir.Y, dir.Z, influenceRadiusNorm));
		}

		// 4. Subir Buffers y Despachar
		UploadBuffers(gpuData, config);
		
		_painter.PaintInfluence(
			_influenceTextureRid, 
			_poiBufferRid, 
			_paramsBufferRid, 
			(float)config.ResolutionF
		);
	}

	// --- GESTIÓN DE RECURSOS GPU ---

	private void CreateInfluenceTexture(uint resolution)
	{
		if (_influenceTextureRid.IsValid) _rd.FreeRid(_influenceTextureRid);

		var fmt = new RDTextureFormat
		{
			TextureType = RenderingDevice.TextureType.Cube, // CRÍTICO
			Width = resolution,
			Height = resolution,
			Depth = 1,
			ArrayLayers = 6, // CRÍTICO PARA CUBEMAPS
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit |
						RenderingDevice.TextureUsageBits.CanCopyFromBit // Útil para debug
		};
		
		_influenceTextureRid = _rd.TextureCreate(fmt, new RDTextureView());
	}

	private void UploadBuffers(List<Vector4> poiList, PlanetParamsData config)
	{
		// 1. Buffer de Puntos (Storage Buffer)
		// Convertimos la lista a bytes crudos
		byte[] poiBytes = Vector4ArrayToBytes(poiList.ToArray());
		
		if (_poiBufferRid.IsValid) _rd.FreeRid(_poiBufferRid);
		_poiBufferRid = _rd.StorageBufferCreate((uint)poiBytes.Length, poiBytes);

		// 2. Buffer de Configuración (Uniform Buffer)
		// Reutilizamos el struct del planeta para que el PoiPainter sepa resolución, radio, etc.
		byte[] paramBytes = StructureToBytes(config);

		if (_paramsBufferRid.IsValid) _rd.FreeRid(_paramsBufferRid);
		_paramsBufferRid = _rd.UniformBufferCreate((uint)paramBytes.Length, paramBytes);
	}

	// --- FUNCIONES MATEMÁTICAS SOLICITADAS ---

	private Vector3 GetRandomDirection(Random rng)
	{
		// Distribución uniforme sobre una esfera
		// Método de coordenadas esféricas ajustado para área uniforme
		double u = rng.NextDouble();
		double v = rng.NextDouble();

		double theta = 2.0 * Math.PI * u;
		double phi = Math.Acos(2.0 * v - 1.0);

		float x = (float)(Math.Sin(phi) * Math.Cos(theta));
		float y = (float)(Math.Sin(phi) * Math.Sin(theta));
		float z = (float)(Math.Cos(phi));

		return new Vector3(x, y, z);
	}

	private void LookAtOutwards(Node3D node, Vector3 position)
	{
		// El objeto está en 'position' (local space del planeta).
		// Queremos que su eje -Z (forward) o Y (up) apunte hacia afuera, 
		// o que se alinee con la superficie.
		
		// Asumimos que el modelo POI tiene el eje Y apuntando "arriba" (hacia el cielo).
		// Calculamos un punto "target" que está más lejos del centro en la misma dirección.
		Vector3 target = position * 2.0f;
		Vector3 upVector = Vector3.Up;

		// Evitar el bloqueo de cardán (Gimbal Lock) si estamos en los polos
		// Si la posición es casi paralela a Vector3.Up, cambiamos el vector Up temporal.
		if (Mathf.Abs(position.Normalized().Dot(Vector3.Up)) > 0.95f)
		{
			upVector = Vector3.Right;
		}

		node.LookAt(target, upVector);
		
		// OPCIONAL: Si tu modelo está rotado (ej. su "arriba" es Z), ajusta rotación local aquí.
		// node.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(90));
	}

	// --- FUNCIONES DE MEMORIA SOLICITADAS ---

	private byte[] Vector4ArrayToBytes(Vector4[] arr)
	{
		// Forma moderna y segura (Zero-Copy interno)
		ReadOnlySpan<Vector4> span = new ReadOnlySpan<Vector4>(arr);
		return MemoryMarshal.AsBytes(span).ToArray();
	}

	private byte[] StructureToBytes<T>(T str) where T : struct
	{
		byte[] arr = new byte[Marshal.SizeOf<T>()];
		MemoryMarshal.Write(arr, ref str);
		return arr;
	}

	// --- LIMPIEZA ---

	private void CleanupVisuals()
	{
		foreach (var node in _spawnedVisuals)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				node.QueueFree();
			}
		}
		_spawnedVisuals.Clear();
	}

	public void Dispose()
	{
		// Liberar RIDs de Vulkan
		if (_influenceTextureRid.IsValid) _rd.FreeRid(_influenceTextureRid);
		if (_poiBufferRid.IsValid) _rd.FreeRid(_poiBufferRid);
		if (_paramsBufferRid.IsValid) _rd.FreeRid(_paramsBufferRid);

		// Liberar Nodos visuales
		CleanupVisuals();
	}
}
