using Godot;
using System;
using System.Threading.Tasks;

public partial class TerrainBaker : Node
{
	// CAMBIO: Se usa la propiedad backing field implícita o se asigna a la propiedad con set privado.
	public Rid ParamsBuffer { get; private set; } 

	[ExportGroup("Baking Settings")]
	[Export] public RDShaderFile BakerShaderFile;
	[Export] public float PlanetRadius = 100.0f;
	[Export] public float NoiseScale = 2.0f;
	[Export] public float NoiseHeight = 10.0f;
	[Export] public int CubemapResolution = 1024;

	private PlanetParams _params;

	public void SetParams(PlanetParams config)
	{
		_params = config;
		PlanetRadius = _params.Radius;
		NoiseScale = _params.NoiseScale;
		NoiseHeight = _params.NoiseHeight;
		CubemapResolution = (int)_params.Resolution;
	}

	public struct BakeResult
	{
		public Rid HeightMapRid;
		public Rid VectorFieldRid;
		public Rid NormalMapRid; // NUEVO
		public bool Success;
	}

	public BakeResult Bake(RenderingDevice rd)
	{
		if (BakerShaderFile == null || rd == null) return new BakeResult { Success = false };

		// --- CORRECCIÓN: Renombrado de stream/writer para evitar conflicto de nombres ---
		// ANTES: var stream = new System.IO.MemoryStream();
		// ANTES: using (var writer = new System.IO.BinaryWriter(stream))
		var bufferStream = new System.IO.MemoryStream();
		using (var bufferWriter = new System.IO.BinaryWriter(bufferStream))
		{
			bufferWriter.Write((float)PlanetRadius);
			bufferWriter.Write((float)CubemapResolution);
			bufferWriter.Write((float)NoiseScale);
			bufferWriter.Write((float)NoiseHeight);
		}
		byte[] paramBytes = bufferStream.ToArray();
		// -------------------------------------------------------------------------------

		// --- CORRECCIÓN: Uso de propiedad IsValid sin paréntesis ---
		// ANTES: if (ParamsBuffer.IsValid) (Esto estaba bien, pero aseguramos consistencia)
		if (ParamsBuffer.IsValid) 
		{
			rd.BufferUpdate(ParamsBuffer, 0, (uint)paramBytes.Length, paramBytes);
		}
		else
		{
			ParamsBuffer = rd.StorageBufferCreate((uint)paramBytes.Length, paramBytes);
		}

		// 1. Crear Texturas
		var fmtHeight = new RDTextureFormat {
			Width = (uint)CubemapResolution, Height = (uint)CubemapResolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		var heightMapRid = rd.TextureCreate(fmtHeight, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		var fmtVectors = new RDTextureFormat {
			Width = (uint)CubemapResolution, Height = (uint)CubemapResolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		var vectorFieldRid = rd.TextureCreate(fmtVectors, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		// Textura de normales (RGB = normal en espacio mundo, A libre)
		var fmtNormals = new RDTextureFormat {
			Width = (uint)CubemapResolution,
			Height = (uint)CubemapResolution,
			Depth = 1,
			ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube,
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit
					| RenderingDevice.TextureUsageBits.SamplingBit
					| RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		var normalMapRid = rd.TextureCreate(fmtNormals, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		// 2. Pipeline
		var bakerSpirv = BakerShaderFile.GetSpirV();
		var bakerShaderRid = rd.ShaderCreateFromSpirV(bakerSpirv);
		var bakerPipeline = rd.ComputePipelineCreate(bakerShaderRid);

		// 3. Uniforms
		var uOutHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOutHeight.AddId(heightMapRid);
		var uOutVectors = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 }; uOutVectors.AddId(vectorFieldRid);
		// NUEVO: normal map
		var uOutNormals = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; 
		uOutNormals.AddId(normalMapRid);
		var bakerSet = rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform> { uOutHeight, uOutVectors, uOutNormals },
			bakerShaderRid,  // ← Shader YA compilado
			0
		);
		// 4. Push Constants
		// --- CORRECCIÓN: Renombrado de variables locales duplicadas ---
		// ANTES: var stream = new System.IO.MemoryStream();
		// ANTES: var writer = new System.IO.BinaryWriter(stream);
		var pushStream = new System.IO.MemoryStream();
		var pushWriter = new System.IO.BinaryWriter(pushStream);
		
		pushWriter.Write((float)PlanetRadius);
		pushWriter.Write((float)NoiseScale);
		pushWriter.Write((float)NoiseHeight);
		pushWriter.Write((uint)CubemapResolution);
		byte[] pushBytes = pushStream.ToArray();
		// --------------------------------------------------------------

		// 5. Dispatch
		var computeList = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(computeList, bakerPipeline);
		rd.ComputeListBindUniformSet(computeList, bakerSet, 0);
		rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
		
		uint groups = (uint)Mathf.CeilToInt(CubemapResolution / 32.0f);
		rd.ComputeListDispatch(computeList, groups, groups, 6);
		rd.ComputeListEnd();

		// Limpieza
		rd.FreeRid(bakerPipeline);
		rd.FreeRid(bakerShaderRid); 
		// No liberamos bakerSet aquí explícitamente para evitar race conditions si no hay sync, 
		// pero idealmente deberíamos traquearlo.

		GD.Print($"[TerrainBaker] Baked {CubemapResolution}x{CubemapResolution}");

		// --- CORRECCIÓN: Referencias a variables existentes ---
		// ANTES: return new BakeResult { Success = true, HeightMapRid = h, VectorFieldRid = v };
		return new BakeResult {
			Success = true,
			HeightMapRid = heightMapRid,
			VectorFieldRid = vectorFieldRid,
			// NUEVO: normal map
			NormalMapRid = normalMapRid
		};
		// ------------------------------------------------------
	}

	protected override void Dispose(bool disposing)
	{
		// --- CORRECCIÓN: Sintaxis IsValid sin paréntesis ---
		// ANTES: if (_paramsBuffer.IsValid())
		if (ParamsBuffer.IsValid) 
		{
			var rd = RenderingServer.GetRenderingDevice();
			// Usamos ParamsBuffer (propiedad) para liberar
			if (rd != null) rd.FreeRid(ParamsBuffer);
		}
		base.Dispose(disposing);
	}
}
