using Godot;
using System;
using System.Runtime.InteropServices;
using System.IO; // Para BinaryWriter

public partial class PlanetBaker : Node
{
	// Propiedad pública para que el PoiPainter pueda leer los params
	public Rid ParamsBuffer { get; private set; } 
	
	public float PlanetRadius => _cachedParams.Radius;
	public float NoiseScale => _cachedParams.NoiseScale;
	public float NoiseHeight => _cachedParams.NoiseHeight;

	public float CubemapResolution => _cachedParams.ResolutionF;
	
	private PlanetParamsData _cachedParams;
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

	public void SetParams(PlanetParamsData config)
	{
		_cachedParams = config;
	}

	public BakeResult Bake(RenderingDevice rd)
	{
		if (BakerShaderFile == null || rd == null) return new BakeResult { Success = false };

		float resolution = (float)_cachedParams.ResolutionF;
		
		// --- 1. GESTIÓN DE PARAMS BUFFER (BINDING 4) ---
		// Sincronizado con layout(std140) del shader AAA
		var memStream = new MemoryStream();
		using (var bw = new BinaryWriter(memStream))
		{
			// vec4 noise_settings 
			bw.Write(_cachedParams.NoiseScale);      // x: Scale
			bw.Write(0.5f);                          // y: Persistence (Fijo por ahora o añádelo a params)
			bw.Write(2.0f);                          // z: Lacunarity (Fijo por ahora o añádelo a params)
			bw.Write(_cachedParams.WarpStrength);    // w: Warp Strength (Variable nueva)

			// vec4 curve_params
			bw.Write(_cachedParams.OceanFloorLevel); // x
			bw.Write(_cachedParams.WeightMultiplier);// y
			bw.Write(_cachedParams.NoiseHeight);     // z
			bw.Write(_cachedParams.GroundDetailFreq);          // w

			// vec4 global_offset
			bw.Write(_cachedParams.NoiseOffset.X);   // x
			bw.Write(_cachedParams.NoiseOffset.Y);   // y
			bw.Write(_cachedParams.NoiseOffset.Z);   // z
			bw.Write(0.0f);                          // w (Padding)

			// vec4 detail_params (Variable nueva)
			bw.Write(_cachedParams.DetailFrequency); // x
			bw.Write(_cachedParams.RidgeSharpness);  // y
			bw.Write(_cachedParams.MaskStart);       // z
			bw.Write(_cachedParams.MaskEnd);         // w

			// vec4 res_offset
			bw.Write((float)resolution);             // x
			bw.Write((float)PlanetRadius);
			bw.Write(0.0f);
			bw.Write(0.0f); // Padding

			// vec4 pad_uv
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f); 
		}
		byte[] paramBytes = memStream.ToArray();

		// Actualizar buffer
		if (ParamsBuffer.IsValid) rd.FreeRid(ParamsBuffer);
		ParamsBuffer = rd.UniformBufferCreate((uint)paramBytes.Length, paramBytes); 

		// --- 2. CREAR TEXTURAS ---
		var fmtR32 = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		
		var fmtRgba16 = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
		};

		var hMap = rd.TextureCreate(fmtR32, new RDTextureView(), new Godot.Collections.Array<byte[]>());
		var vMap = rd.TextureCreate(fmtRgba16, new RDTextureView(), new Godot.Collections.Array<byte[]>()); // Vector Field (Binding 1)
		var nMap = rd.TextureCreate(fmtRgba16, new RDTextureView(), new Godot.Collections.Array<byte[]>()); // Normal Map (Binding 2)

		// --- 3. STATS BUFFER ---
		int[] initialStats = { int.MaxValue, int.MinValue };
		byte[] statsBytes = new byte[8];
		Buffer.BlockCopy(initialStats, 0, statsBytes, 0, 8);
		var statsBuffer = rd.StorageBufferCreate((uint)statsBytes.Length, statsBytes);

		// --- 4. PREPARAR UNIFORMS (Orden Crítico) ---
		// Binding 0: Height Map
		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; 
		uHeight.AddId(hMap);
		
		// Binding 1: Vector Field
		var uVec = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 }; 
		uVec.AddId(vMap);
		
		// Binding 2: Normal Map
		var uNorm = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; 
		uNorm.AddId(nMap);
		
		// Binding 3: Stats
		var uStats = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; 
		uStats.AddId(statsBuffer);

		// Binding 4: Params
		var uParams = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 };
		uParams.AddId(ParamsBuffer);

		// Pipeline
		var shaderSpirv = BakerShaderFile.GetSpirV();
		var shaderRid = rd.ShaderCreateFromSpirV(shaderSpirv);
		
		// --- CHECK DE ERROR DE SHADER ---
		if (!shaderRid.IsValid)
		{
			GD.PrintErr("SHADER ERROR: El shader no compiló. Revisa la consola de Output para errores GLSL.");
			// Return fail pero liberando lo creado
			rd.FreeRid(hMap); rd.FreeRid(vMap); rd.FreeRid(nMap); rd.FreeRid(statsBuffer);
			return new BakeResult { Success = false };
		}

		var pipeline = rd.ComputePipelineCreate(shaderRid);
		var uniformSet = rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform> { uHeight, uVec, uNorm, uStats, uParams }, 
			shaderRid, 0
		);

		if (!uniformSet.IsValid) 
		{
			GD.PrintErr("PIPELINE ERROR: Falló UniformSetCreate. Revisa que los tipos de Binding en C# coincidan con el GLSL.");
			return new BakeResult { Success = false };
		}

		// --- 5. DISPATCH ---
		long computeList = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(computeList, pipeline);
		rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
		
		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f); 
		rd.ComputeListDispatch(computeList, groups, groups, 6);
		rd.ComputeListEnd();

		// rd.Submit();
		// rd.Sync(); // Esperar resultado para leer stats

		// --- 6. LECTURA ---
		byte[] outBytes = rd.BufferGetData(statsBuffer);
		int outMin = BitConverter.ToInt32(outBytes, 0);
		int outMax = BitConverter.ToInt32(outBytes, 4);

		// Limpieza
		rd.FreeRid(pipeline);
		rd.FreeRid(shaderRid);
		rd.FreeRid(statsBuffer); 

		float realMin = outMin / FIXED_POINT_SCALE;
		float realMax = outMax / FIXED_POINT_SCALE;
		
		GD.Print($"Radio: {_cachedParams.Radius}");
		GD.Print($"realMin: {realMin}");
		GD.Print($"realMax: {realMax}");

		// Validación básica
		if (realMax < realMin) { realMin = 0; realMax = 1; }

		return new BakeResult {
			Success = true, 
			HeightMapRid = hMap, 
			VectorFieldRid = vMap, 
			NormalMapRid = nMap,
			MinHeight = realMin, 
			MaxHeight = realMax
		};
	}

	protected override void Dispose(bool disposing) {
		if (ParamsBuffer.IsValid) RenderingServer.GetRenderingDevice()?.FreeRid(ParamsBuffer);
		base.Dispose(disposing);
	}
}
