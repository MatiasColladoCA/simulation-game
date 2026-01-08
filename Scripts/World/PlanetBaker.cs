using Godot;
using System;
using System.Runtime.InteropServices;

public partial class PlanetBaker : Node
{
	// Propiedad pública para que el PoiPainter pueda leer los params
	public Rid ParamsBuffer { get; private set; } 
	
	public float PlanetRadius => _cachedParams.Radius;
	public float NoiseScale => _cachedParams.NoiseScale;
	public float NoiseHeight => _cachedParams.NoiseHeight;
	public int CubemapResolution => (int)_cachedParams.Resolution;
	
	// Configuración almacenada (Stateful) para compatibilidad
	private PlanetParamsData _cachedParams;

	// Struct de Stats para lectura
	[StructLayout(LayoutKind.Sequential)]
	private struct StatsBufferData { public int MinHeightFixed; public int MaxHeightFixed; }

	// Constante
	private const float FIXED_POINT_SCALE = 100000.0f;

	[StructLayout(LayoutKind.Sequential)]
	public struct BakeResult
	{
		public bool Success;
		public Rid HeightMapRid;
		public Rid VectorFieldRid;
		public Rid NormalMapRid;
		public float MinHeight;
		public float MaxHeight;
		public float HeightRange => MaxHeight - MinHeight;
	}

	[Export] public RDShaderFile BakerShaderFile;

	// --- CORRECCIÓN 1: Método SetParams para compatibilidad ---
	public void SetParams(PlanetParamsData config)
	{
		_cachedParams = config;
	}

	// --- CORRECCIÓN 2: Método Bake sin argumentos (usa _cachedParams) ---
	public BakeResult Bake(RenderingDevice rd)
	{
		if (BakerShaderFile == null || rd == null) return new BakeResult { Success = false };

		// Desempaquetar configuración desde caché
		float radius = _cachedParams.Radius;
		int resolution = (int)_cachedParams.Resolution;
		float noiseScale = _cachedParams.NoiseScale;
		float noiseHeight = _cachedParams.NoiseHeight;
		
		// --- 1. GESTIÓN DE PARAMS BUFFER (BINDING 4) ---
		var memStream = new System.IO.MemoryStream();
		using (var bw = new System.IO.BinaryWriter(memStream))
		{
			// vec4 noise_settings
			bw.Write(_cachedParams.NoiseScale);      
			bw.Write(0.5f);            
			bw.Write(_cachedParams.MountainRoughness);            
			bw.Write(4.0f);            

			// vec4 curve_params
			bw.Write(_cachedParams.WeightMultiplier); // .x (Antes era 1.5f a fuego)
			bw.Write(_cachedParams.OceanFloorLevel); // .y (Antes era 0.15f a fuego)
			bw.Write(_cachedParams.NoiseHeight);     // .z
			bw.Write(_cachedParams.Radius);          // .w
			// --- CORRECCIÓN: Offset de Semilla ---
			// vec3 global_offset (Antes face_up) + padding
			bw.Write(_cachedParams.NoiseOffset.X); 
			bw.Write(_cachedParams.NoiseOffset.Y); 
			bw.Write(_cachedParams.NoiseOffset.Z); 
			bw.Write(0.0f); // Padding align vec4
			// -------------------------------------

			// vec3 center_pos + padding
			bw.Write(0.0f); bw.Write(0.0f); bw.Write(0.0f);
			bw.Write(0.0f); 

			// vec2 resolution + vec2 offset (packed in vec4)
			bw.Write((float)resolution); bw.Write((float)resolution);
			bw.Write(0.0f); bw.Write(0.0f);

			// float uv_scale + padding vec3
			bw.Write(1.0f); 
			bw.Write(0.0f); bw.Write(0.0f); bw.Write(0.0f); 
		}
		byte[] paramBytes = memStream.ToArray();

		// Limpiar buffer anterior si existe
		if (ParamsBuffer.IsValid) rd.FreeRid(ParamsBuffer);

		// --- CORRECCIÓN CRÍTICA AQUÍ ---
		// Usamos UniformBufferCreate porque el shader define 'uniform BakeParams'
		// Antes usábamos StorageBufferCreate, lo cual es incompatible con UniformType.UniformBuffer
		ParamsBuffer = rd.UniformBufferCreate((uint)paramBytes.Length, paramBytes); 
		// --------------------------------

		// --- 2. CREAR TEXTURAS (BINDINGS 0, 1, 2) ---
		var fmtFloat = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		var hMap = rd.TextureCreate(fmtFloat, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		var fmtVector = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
		};
		var vMap = rd.TextureCreate(fmtVector, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		var fmtNormal = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
		};
		var nMap = rd.TextureCreate(fmtNormal, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		// --- 3. STATS BUFFER (BINDING 3) ---
		// Este SÍ es StorageBuffer porque el shader dice 'buffer StatsBuffer'
		int[] initialStats = { int.MaxValue, int.MinValue };
		byte[] statsBytes = new byte[8];
		Buffer.BlockCopy(initialStats, 0, statsBytes, 0, 8);
		var statsBuffer = rd.StorageBufferCreate((uint)statsBytes.Length, statsBytes);

		// --- 4. PREPARAR UNIFORMS ---
		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; 
		uHeight.AddId(hMap);
		
		var uNorm = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 }; 
		uNorm.AddId(nMap);
		
		var uVec = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; 
		uVec.AddId(vMap);
		
		var uStats = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; 
		uStats.AddId(statsBuffer);

		var uParams = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 };
		uParams.AddId(ParamsBuffer);

		// Pipeline
		var shaderSpirv = BakerShaderFile.GetSpirV();
		var shaderRid = rd.ShaderCreateFromSpirV(shaderSpirv);
		var pipeline = rd.ComputePipelineCreate(shaderRid);

		// Crear Set
		var uniformSet = rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform> { uHeight, uNorm, uVec, uStats, uParams }, 
			shaderRid, 0
		);
		
		if (!uniformSet.IsValid) 
		{
			GD.PrintErr("[PlanetBaker] Error al crear UniformSet. Verifica los Bindings.");
			return new BakeResult { Success = false };
		}

		// --- 5. DISPATCH ---
		long computeList = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(computeList, pipeline);
		rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
		
		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f); 
		rd.ComputeListDispatch(computeList, groups, groups, 6);
		rd.ComputeListEnd();

		// --- 6. LECTURA Y RETORNO ---
		// rd.Submit();
		// rd.Sync();

		byte[] outBytes = rd.BufferGetData(statsBuffer);
		int outMin = BitConverter.ToInt32(outBytes, 0);
		int outMax = BitConverter.ToInt32(outBytes, 4);

		// Limpieza de temporales
		rd.FreeRid(pipeline);
		rd.FreeRid(shaderRid);
		rd.FreeRid(statsBuffer); 

		float realMin = outMin / FIXED_POINT_SCALE;
		float realMax = outMax / FIXED_POINT_SCALE;
		
		if (realMin > realMax) { realMin = 0; realMax = 0; }

		return new BakeResult {
			Success = true, HeightMapRid = hMap, NormalMapRid = nMap, VectorFieldRid = vMap,
			MinHeight = realMin, MaxHeight = realMax
		};
	}

	// Limpieza al salir
	protected override void Dispose(bool disposing) {
		if (ParamsBuffer.IsValid) RenderingServer.GetRenderingDevice()?.FreeRid(ParamsBuffer);
		base.Dispose(disposing);
	}
}
