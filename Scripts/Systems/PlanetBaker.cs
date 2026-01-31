using Godot;
using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Worker de vida corta. Se instancia, hornea y muere.
/// No guarda estado. No posee los recursos que crea.
/// </summary>
public partial class PlanetBaker : RefCounted // Usamos RefCounted, no Node (Más ligero)
{
	private const float FIXED_POINT_SCALE = 100000.0f;

	public struct BakeResult
	{
		public bool Success;
		public Rid HeightMapRid;
		public Rid NormalMapRid;
		public Rid VectorFieldRid;
		public byte[] HeightMapRawBytes; // Datos crudos para CPU Cache
		public int Resolution;
		public float MinHeight;
		public float MaxHeight;
	}

	/// <summary>
	/// Ejecuta el pipeline de generación de terreno en GPU.
	/// </summary>
	public BakeResult Bake(RenderingDevice rd, Rid shaderRid, PlanetParamsData config)
	{
		GD.Print("[PlanetBaker] Iniciando bake...");
		
		// 1. CONFIGURACIÓN
		int resolution = (int)config.TextureResolution;
		if (resolution <= 0) resolution = 1024;

		// 2. CREAR TEXTURAS (CUBEMAPS)
		// HeightMap: R32F (Alta precisión para física)
		var fmtHeight = new RDTextureFormat
		{
			TextureType = RenderingDevice.TextureType.Cube,
			Width = (uint)resolution, Height = (uint)resolution, 
			Depth = 1, ArrayLayers = 6,
			Format = RenderingDevice.DataFormat.R32Sfloat, // 4 bytes por pixel
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit | 
						RenderingDevice.TextureUsageBits.CanCopyFromBit // Crucial para CPU Readback
		};

		// NormalMap/VectorField: RGBA16F (Vectores)
		var fmtVector = new RDTextureFormat
		{
			TextureType = RenderingDevice.TextureType.Cube,
			Width = (uint)resolution, Height = (uint)resolution, 
			Depth = 1, ArrayLayers = 6,
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit
		};

		Rid hMap = rd.TextureCreate(fmtHeight, new RDTextureView());
		Rid nMap = rd.TextureCreate(fmtVector, new RDTextureView());
		Rid vMap = rd.TextureCreate(fmtVector, new RDTextureView()); 

		// 3. BUFFERS (Params & Stats)
		
		// Params (Uniform Buffer std140 - 96 bytes total)
		byte[] paramBytes = GenerateParamBytes(config);
		Rid paramsBuffer = rd.UniformBufferCreate((uint)paramBytes.Length, paramBytes);

		// Stats (Storage Buffer - Atomic Min/Max)
		int[] initialStats = { int.MaxValue, int.MinValue }; // Min, Max (Int escalado)
		byte[] statsBytes = new byte[8];
		Buffer.BlockCopy(initialStats, 0, statsBytes, 0, 8);
		Rid statsBuffer = rd.StorageBufferCreate((uint)statsBytes.Length, statsBytes);

		// 4. PIPELINE Y UNIFORMS
		if (!shaderRid.IsValid)
		{
			GD.PrintErr("[Baker] Shader RID inválido.");
			return new BakeResult { Success = false };
		}
		Rid pipeline = rd.ComputePipelineCreate(shaderRid);

		// Uniforms (Orden estricto del GLSL)
		// Binding 0: HeightMap (Image)
		// Binding 1: VectorMap (Image)
		// Binding 2: NormalMap (Image)
		// Binding 3: Stats (Storage)
		// Binding 4: Params (Uniform)
		var uHeight = CreateImageUniform(hMap, 0);
		var uVec    = CreateImageUniform(vMap, 1);
		var uNorm   = CreateImageUniform(nMap, 2);
		var uStats  = CreateBufferUniform(statsBuffer, 3, RenderingDevice.UniformType.StorageBuffer);
		var uParams = CreateBufferUniform(paramsBuffer, 4, RenderingDevice.UniformType.UniformBuffer);

		Rid uniformSet = rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform> { uHeight, uVec, uNorm, uStats, uParams }, 
			shaderRid, 0
		);

		// 5. EJECUCIÓN (DISPATCH)
		long computeList = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(computeList, pipeline);
		rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
		
		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f);
		rd.ComputeListDispatch(computeList, groups, groups, 6); // 6 caras
		rd.ComputeListEnd();

		// 6. LECTURA DE RESULTADOS (Readback)

		// A. Stats (Min/Max Height)
		byte[] outStats = rd.BufferGetData(statsBuffer);
		float realMin = BitConverter.ToInt32(outStats, 0) / FIXED_POINT_SCALE;
		float realMax = BitConverter.ToInt32(outStats, 4) / FIXED_POINT_SCALE;
		
		// Validación de seguridad (evitar NaN o inf)
		if (float.IsNaN(realMin) || float.IsInfinity(realMin)) realMin = 0;
		if (float.IsNaN(realMax) || float.IsInfinity(realMax)) realMax = 1;
		if (realMax < realMin) { realMin = 0; realMax = 1; }

		// B. HeightMap Raw Bytes - Descargar las 6 caras
		int bytesPerPixel = 4; // R32F = 4 bytes
		int bytesPerFace = resolution * resolution * bytesPerPixel;
		byte[] fullHeightData = new byte[bytesPerFace * 6];

		for (uint i = 0; i < 6; i++)
		{
			byte[] layerData = rd.TextureGetData(hMap, i);
			if (layerData.Length < bytesPerFace)
			{
				GD.PrintErr($"[Baker] Error: Capa {i} devolvió menos bytes ({layerData.Length} vs {bytesPerFace}).");
				Array.Clear(fullHeightData, (int)i * bytesPerFace, bytesPerFace);
			}
			else
			{
				Array.Copy(layerData, 0, fullHeightData, i * bytesPerFace, bytesPerFace);
			}
		}

		// 7. LIMPIEZA DE RECURSOS TEMPORALES
		rd.FreeRid(pipeline);
		rd.FreeRid(uniformSet);
		rd.FreeRid(paramsBuffer);
		rd.FreeRid(statsBuffer);

		GD.Print($"[Baker] Bake Terminado. Rango: {realMin:F1}m a {realMax:F1}m");

		return new BakeResult
		{
			Success = true,
			HeightMapRid = hMap,
			HeightMapRawBytes = fullHeightData,
			VectorFieldRid = vMap,
			NormalMapRid = nMap,

			MinHeight = realMin,
			MaxHeight = realMax,
			Resolution = resolution
		};
	}

	/// <summary>
	/// Genera bytes para Uniform Buffer respetando alineación std140.
	/// Shader espera: noise_settings, curve_params, global_offset, detail_params, res_offset, pad_uv
	/// Total: 6 vec4 = 96 bytes
	/// </summary>
	private byte[] GenerateParamBytes(PlanetParamsData p)
	{
		using (var ms = new MemoryStream())
		using (var bw = new BinaryWriter(ms))
		{
			// vec4 noise_settings (16 bytes)
			bw.Write(p.NoiseScale);
			bw.Write(p.NoiseHeight);
			bw.Write(0.0f);
			bw.Write(p.WarpStrength);
			
			// vec4 curve_params (16 bytes)
			bw.Write(p.OceanFloorLevel);
			bw.Write(p.WeightMultiplier);
			bw.Write(p.GroundDetailFreq);  // curve_params.z - AMPLITUD DEL RELIEVE
			bw.Write(p._padding2);
			
			// vec4 global_offset (16 bytes) - vec3 + padding
			bw.Write(p.NoiseOffset.X);
			bw.Write(p.NoiseOffset.Y);
			bw.Write(p.NoiseOffset.Z);
			bw.Write(0.0f); // padding
			
			// vec4 detail_params (16 bytes)
			bw.Write(p.DetailFrequency);
			bw.Write(p.RidgeSharpness);
			bw.Write(p.MaskStart);
			bw.Write(p.MaskEnd);
			
			// vec4 res_offset (16 bytes)
			bw.Write(p.TextureResolution);
			bw.Write(p.Radius);
			bw.Write(p.LogicResolution);
			bw.Write(0.0f); // padding
			
			// vec4 pad_uv (16 bytes)
			bw.Write(p._padding6);
			bw.Write(p._padding7);
			bw.Write(p._padding8);
			bw.Write(p._padding9);
			
			return ms.ToArray();
		}
	}

	// --- HELPERS ---

	private RDUniform CreateImageUniform(Rid textureRid, int binding)
	{
		var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
		u.AddId(textureRid);
		return u;
	}

	private RDUniform CreateBufferUniform(Rid bufferRid, int binding, RenderingDevice.UniformType type)
	{
		var u = new RDUniform { UniformType = type, Binding = binding };
		u.AddId(bufferRid);
		return u;
	}
}
