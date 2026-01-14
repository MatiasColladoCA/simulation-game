
using Godot;
using System;
using System.IO; 
using System.Runtime.InteropServices;

public partial class PlanetBaker : Node
{
	// --- CONFIGURACIÓN ---
	public RDShaderFile BakerShaderFile;

	// Estado interno para la generación
	private PlanetParamsData _cachedParams;
	private Rid _paramsBuffer; // Buffer reutilizable si hiciéramos rebake continuo
	
	// Constantes
	private const float FIXED_POINT_SCALE = 100000.0f;

	// DTO (Data Transfer Object) para devolver todo el paquete al Planet
	public struct BakeResult
	{
		public bool Success;
		public Rid HeightMapRid;
		public Rid VectorFieldRid;
		public Rid NormalMapRid;
		public float MinHeight;
		public float MaxHeight;
	}

	// Setter simple
	public void SetParams(PlanetParamsData config)
	{
		_cachedParams = config;
	}

	// --- FUNCIÓN PRINCIPAL DE GENERACIÓN ---
	public BakeResult Bake(RenderingDevice rd)
	{

		GD.Print("[Baker] --- Iniciando Proceso de Validación ---");

		// 1. VALIDACIÓN DE DEPENDENCIAS
		if (rd == null)
		{
			GD.PrintErr("[Baker] FATAL: RenderingDevice es nulo.");
			return new BakeResult { Success = false };
		}
		if (BakerShaderFile == null)
		{
			GD.PrintErr("[Baker] FATAL: No se asignó el archivo .glsl en el inspector.");
			return new BakeResult { Success = false };
		}

		// 2. VALIDACIÓN DE DATOS DE ENTRADA (PARAMS)
		if (_cachedParams.ResolutionF < 8) 
		{
			GD.PrintErr($"[Baker] ERROR: Resolución absurda ({_cachedParams.ResolutionF}). Mínimo requerido: 32.");
			return new BakeResult { Success = false };
		}
		if (_cachedParams.Radius <= 0)
		{
			GD.PrintErr($"[Baker] ERROR: Radio inválido ({_cachedParams.Radius}). El planeta debe tener volumen.");
			return new BakeResult { Success = false };
		}
		
		// Debug de lo que entra
		GD.Print($"[Baker] Configuración recibida: Res={_cachedParams.ResolutionF}, Radio={_cachedParams.Radius}, RuidoScale={_cachedParams.NoiseScale}");

		// TRUCO AAA: Crear un dispositivo local temporal para poder hacer Sync() sin errores
		// Esto permite bloquear el hilo hasta que termine el cálculo.
		var localRd = RenderingServer.CreateLocalRenderingDevice();

		// Usamos localRd para TODO dentro de esta función
		var _rd = localRd;

		

		// 2. PREPARAR DATOS
		float resolution = (float)_cachedParams.ResolutionF;
		
		// Crear/Actualizar Buffer de Parámetros
		// Usamos BinaryWriter para asegurar el layout std140 byte a byte
		byte[] paramBytes = GenerateParamBytes(resolution);
		
		if (_paramsBuffer.IsValid) rd.FreeRid(_paramsBuffer);
		_paramsBuffer = rd.UniformBufferCreate((uint)paramBytes.Length, paramBytes);

		// 3. CREAR TEXTURAS DE DESTINO (El producto final)
		// Nota: R32Sfloat para altura (precisión), R16g16... para normales/vectores
		var fmtHeight = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, 
			Format = RenderingDevice.DataFormat.R32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
		};
		
		// ¡OJO! En Godot 4 C#, los formatos suelen ser R16g16b16a16Sfloat (minúsculas)
		var fmtVector = new RDTextureFormat {
			Width = (uint)resolution, Height = (uint)resolution, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, 
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
		};

		// Creamos las texturas (Empty arrays porque las llenará la GPU)
		var hMap = rd.TextureCreate(fmtHeight, new RDTextureView());
		var vMap = rd.TextureCreate(fmtVector, new RDTextureView()); // Binding 1
		var nMap = rd.TextureCreate(fmtVector, new RDTextureView()); // Binding 2

		// 4. BUFFER DE ESTADÍSTICAS (Min/Max height)
		// Inicializamos con inversa (MaxInt, MinInt) para que el shader escriba valores reales
		int[] initialStats = { int.MaxValue, int.MinValue };
		byte[] statsBytes = new byte[8];
		Buffer.BlockCopy(initialStats, 0, statsBytes, 0, 8);
		var statsBuffer = rd.StorageBufferCreate((uint)statsBytes.Length, statsBytes);

		// 5. CONFIGURAR PIPELINE
		var shaderSpirv = BakerShaderFile.GetSpirV();
		var shaderRid = rd.ShaderCreateFromSpirV(shaderSpirv);
		
		if (!shaderRid.IsValid)
		{
			GD.PrintErr("[PlanetBaker] ERROR: El shader no compiló.");
			CleanupGenerators(rd, hMap, vMap, nMap, statsBuffer);
			return new BakeResult { Success = false };
		}

		var pipeline = rd.ComputePipelineCreate(shaderRid);
		
		// 6. UNIFORM SET (La conexión C# -> GLSL)
		// El orden debe coincidir estrictamente con los bindings del GLSL

		// Definimos cada uniform explícitamente y añadimos su ID
		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
		uHeight.AddId(hMap);

		var uVec = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
		uVec.AddId(vMap);

		var uNorm = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
		uNorm.AddId(nMap);

		var uStats = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
		uStats.AddId(statsBuffer);

		var uParams = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 };
		uParams.AddId(_paramsBuffer);


		// Creamos el set pasando la lista
		var uniformSet = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { 
			uHeight, uVec, uNorm, uStats, uParams 
		}, shaderRid, 0);

		if (!uniformSet.IsValid)
		{
			GD.PrintErr("[PlanetBaker] ERROR: UniformSet inválido. Revisa bindings y tipos.");
			CleanupGenerators(rd, hMap, vMap, nMap, statsBuffer, pipeline, shaderRid);
			return new BakeResult { Success = false };
		}

		// 7. EJECUCIÓN (DISPATCH)
		long computeList = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(computeList, pipeline);
		rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
		
		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f); 
		rd.ComputeListDispatch(computeList, groups, groups, 6); // 6 caras del cubo
		rd.ComputeListAddBarrier(computeList);
		rd.ComputeListEnd();

		// rd.Submit();
		// rd.Sync(); // Esperamos para leer las stats (Bloqueante, necesario para física inicial)

		// 8. LECTURA DE RESULTADOS
		
		byte[] outBytes = rd.BufferGetData(statsBuffer);
		float realMin = BitConverter.ToInt32(outBytes, 0) / FIXED_POINT_SCALE;
		float realMax = BitConverter.ToInt32(outBytes, 4) / FIXED_POINT_SCALE;

		// Validación de seguridad por si el shader falló silenciosamente
		if (realMax < realMin) { realMin = 0; realMax = 1; }

		// 9. LIMPIEZA DE HERRAMIENTAS (No borramos las texturas, esas se devuelven)
		rd.FreeRid(pipeline);
		rd.FreeRid(shaderRid);
		rd.FreeRid(statsBuffer);
		// El _paramsBuffer lo guardamos por si queremos reutilizarlo, se libera en Dispose

		GD.Print($"[Baker] Resultado GPU -> MinHeight: {realMin}, MaxHeight: {realMax}");

		// Verificación de integridad: Si el mapa es plano absoluto (0 a 0), algo falló en el shader.
		// Nota: A veces es válido ser plano, pero en tu generación procedural es casi imposible.
		if (Mathf.IsEqualApprox(realMin, 0) && Mathf.IsEqualApprox(realMax, 0))
		{
			GD.Print("[Baker] ALERTA: El terreno generado es totalmente plano (0m). ¿Falló el Compute Shader o los Uniforms?");
			// No retornamos false aquí porque quizás querías un planeta plano, pero avisamos.
		}

		return new BakeResult {
			Success = true,
			HeightMapRid = hMap,
			VectorFieldRid = vMap,
			NormalMapRid = nMap,
			MinHeight = realMin,
			MaxHeight = realMax
		};
	}

	// --- HELPERS INTERNOS ---

	private byte[] GenerateParamBytes(float resolution)
	{
		using (var ms = new MemoryStream())
		using (var bw = new BinaryWriter(ms))
		{
			// Block 0: Noise Settings
			bw.Write(_cachedParams.NoiseScale);
			bw.Write(0.5f); // Persistence
			bw.Write(2.0f); // Lacunarity
			bw.Write(_cachedParams.WarpStrength);

			// Block 1: Curve Params
			bw.Write(_cachedParams.OceanFloorLevel);
			bw.Write(_cachedParams.WeightMultiplier);
			bw.Write(_cachedParams.NoiseHeight);
			bw.Write(_cachedParams.GroundDetailFreq);

			// Block 2: Global Offset
			bw.Write(_cachedParams.NoiseOffset.X);
			bw.Write(_cachedParams.NoiseOffset.Y);
			bw.Write(_cachedParams.NoiseOffset.Z);
			bw.Write(0.0f); // Padding

			// Block 3: Detail Params
			bw.Write(_cachedParams.DetailFrequency);
			bw.Write(_cachedParams.RidgeSharpness);
			bw.Write(_cachedParams.MaskStart);
			bw.Write(_cachedParams.MaskEnd);

			// Block 4: Res & Radius
			bw.Write(resolution);
			bw.Write(_cachedParams.Radius);
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f);
			bw.Write(0.0f);

			return ms.ToArray();
		}
	}

	private void CleanupGenerators(RenderingDevice rd, params Rid[] rids)
	{
		foreach (var rid in rids) if (rid.IsValid) rd.FreeRid(rid);
	}

	// Limpieza final al destruir el nodo
	public override void _ExitTree()
	{
		if (_paramsBuffer.IsValid) 
			RenderingServer.GetRenderingDevice()?.FreeRid(_paramsBuffer);
		base._ExitTree();
	}
}

// Extensión para facilitar la creación de Uniforms
public static class UniformExtensions 
{
	// Solo un helper visual si quisieras limpiar el código de creación de Uniforms
	// pero el código inline arriba es suficientemente claro.
}
