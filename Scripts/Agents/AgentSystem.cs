using Godot;
using System;
using System.Runtime.InteropServices;

// Mantenemos el struct aquí
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentDataSphere
{
	public Vector4 Position;   // w: state_timer
	public Vector4 Velocity;   // w: current_state
	public Vector4 GroupData;  // x: group_id, y: density_time
	public Vector4 Color;      // rgba
}

public partial class AgentSystem : Node3D
{
	[Export] public RDShaderFile ComputeShaderFile;
	[Export] public int AgentCount = 500_000;
	
	// Referencias internas
	// private MultiMeshInstance3D _visualizer;
	private RenderingDevice _rd;
	
	// RIDs
	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid, _gridBufferRid;
	private Rid _posTextureRid, _colorTextureRid, _densityTextureRid;
	
	
	private Rid _samplerRid, _counterBufferRid;
	public uint ActiveAgentCount { get; private set; }
	
	// Datos externos (Inyectados)
	private float _planetRadius;
	private float _noiseScale;
	private float _noiseHeight;

	// private Texture2Drd _posTextureRef, _colorTextureRef;
	private AgentDataSphere[] _cpuAgents;
	private byte[] _pushConstantBuffer = new byte[48];
	private bool _isInitialized = false;
	public bool IsInitialized => _isInitialized; // ← NUEVO: Getter público

	// Constantes
	private const int DATA_TEX_WIDTH = 2048; 
	
	// GRID_RES ahora se recibe como parámetro en Initialize()
	private int _gridResolution;
	private int GRID_RES => _gridResolution;
	private int GRID_TOTAL_CELLS => GRID_RES * GRID_RES * GRID_RES;


	private Rid _bakedHeightMap;
	private Rid _bakedVectorField;
	private Rid _poiBufferRid;

	private Rid _deadListBufferRid;

	private EnvironmentManager _env;




	// --- API PÚBLICA ---

	public void Initialize(RenderingDevice rd, Planet planet, EnvironmentManager env, PlanetParamsData config, int gridResolution)	{

		_rd = rd;
		_env = env;
		_gridResolution = gridResolution; // ← CRÍTICO: Asignar antes de usar GRID_TOTAL_CELLS

		// VALIDAR RECURSOS EXTERNOS PRIMERO
		if (!planet._heightMapRid.IsValid) {
			GD.PrintErr("[AgentSystem] ERROR: HeightMap inválido");
			return;
		}
		if (!env.VectorField.IsValid) {
			GD.PrintErr("[AgentSystem] ERROR: VectorField inválido");
			return;
		}
		if (!env.POIBuffer.IsValid) {
			GD.PrintErr("[AgentSystem] ERROR: POIBuffer inválido");
			return;
		}
		if (!env.InfluenceTexture.IsValid) {
			GD.PrintErr("[AgentSystem] ERROR: InfluenceTexture inválido");
			return;
		}

		// Asignación desde recursos externos
		_bakedHeightMap = planet._heightMapRid;
		_bakedVectorField = env.VectorField;
		_poiBufferRid = env.POIBuffer;
		_densityTextureRid = env.InfluenceTexture;
		
		_planetRadius = config.Radius;
		_noiseScale = config.NoiseScale;
		_noiseHeight = config.NoiseHeight;

		// ORDEN CORREGIDO: SetupData PRIMERO para que _cpuAgents tenga datos
		SetupData();
		
		// AHORA sí podemos crear recursos y subir datos a GPU
		CreateInternalResources();
		
		SetupCompute();
		// SetupVisuals();

		// Verificación final estricta
		if (_pipelineRid.IsValid && _uniformSetRid.IsValid && _bufferRid.IsValid)
		{
			_isInitialized = true;
			GD.Print("[AgentSystem] SISTEMA ONLINE. Buffer initialized with agents.");
			
			// Inicializar texturas con valores por defecto (evita que aparezcan como "muertos")
			InitializeTexturesWithDefaults();
		}
		else
		{
			_isInitialized = false;
			GD.PrintErr("[AgentSystem] Falló la inicialización final.");
		}
	}

	private void InitializeTexturesWithDefaults()
	{
		if (_rd == null || !_posTextureRid.IsValid) return;

		//  int requiredHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
		// RDTextureFormat currentFmt = _rd.TextureGetFormat(_posTextureRid);
		// if (currentFmt.Height != requiredHeight) {
		// 	// Libera texturas viejas
		// 	SafeFree(_posTextureRid);
		// 	SafeFree(_colorTextureRid);
		// 	// Recrea con nueva altura (copia el código existente de creación de texturas, cambiando Height)
		// }
		
		// Crear imagen inicial con posiciones visibles (radio del planeta)
		int texHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
		int totalPixels = DATA_TEX_WIDTH * texHeight;
		
		// Inicializar con datos visibles
		byte[] posData = new byte[totalPixels * 16]; // 16 bytes por pixel (RGBA32F)
		byte[] colData = new byte[totalPixels * 16];
		
		for (int i = 0; i < AgentCount && i < totalPixels; i++)
		{
			// Posición visible (radio del planeta)
			float x = (float)(i % DATA_TEX_WIDTH) * 0.01f;
			float y = (float)(i / DATA_TEX_WIDTH) * 0.01f;
			float z = _planetRadius;
			float posW = 1.0f; // Vivo
			
			// Color blanco
			float colR = 1.0f, colG = 1.0f, colB = 1.0f, colW = 1.0f; // Vivo
			
			int offset = i * 16;
			BitConverter.GetBytes(x).CopyTo(posData, offset + 0);
			BitConverter.GetBytes(y).CopyTo(posData, offset + 4);
			BitConverter.GetBytes(z).CopyTo(posData, offset + 8);
			BitConverter.GetBytes(posW).CopyTo(posData, offset + 12);
			
			BitConverter.GetBytes(colR).CopyTo(colData, offset + 0);
			BitConverter.GetBytes(colG).CopyTo(colData, offset + 4);
			BitConverter.GetBytes(colB).CopyTo(colData, offset + 8);
			BitConverter.GetBytes(colW).CopyTo(colData, offset + 12);
		}
		
		// Subir a GPU
		_rd.TextureUpdate(_posTextureRid, 0, posData);
		_rd.TextureUpdate(_colorTextureRid, 0, colData);
		
		GD.Print($"[AgentSystem] Texturas inicializadas con {AgentCount} agentes visibles.");
	}



	public void UpdateSimulation(double delta, double time)
{
	// 1. Verificación de Seguridad
	if (!_isInitialized || _rd == null) return;
	
	// Si el pipeline se rompió en el frame anterior, intentamos recuperarnos o abortar
	if (!_pipelineRid.IsValid || !_uniformSetRid.IsValid) 
	{
		if (_isInitialized) {
			GD.PrintErr("[AgentSystem] FATAL: Pipeline inválido. Deteniendo simulación.");
			_isInitialized = false; 
		}
		return;
	}

	float dt = (float)delta;
	float t = (float)time;

	// 2. Cálculos de Despacho
	// Para 50,000 agentes / 64 = 782 grupos
	uint groupsAgents = (uint)Mathf.CeilToInt(AgentCount / 64.0f);
	// Para grilla 64^3 / 64 = 4096 grupos
	uint groupsGrid = (uint)Mathf.CeilToInt(GRID_TOTAL_CELLS / 64.0f);
	
	// Despacho 3D para Phase 3 (Paint)
	// Shader local_size_x = 64. Grid X = 64.
	// 64 / 64 = 1 grupo en X. Y y Z quedan en 64.
	uint gX = (uint)Mathf.CeilToInt((float)_gridResolution / 64.0f); 
	uint gY = (uint)_gridResolution;
	uint gZ = (uint)_gridResolution;

		// ✅ ORDEN CORRECTO (Igual que legacy)
	_rd.BufferClear(_counterBufferRid, 0, 4); 

	long computeList = _rd.ComputeListBegin();
	_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
	_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);

	// FASE 0: CLEAR GRID
	UpdatePushConstants(dt, t, 0, (int)_gridResolution);
	_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
	_rd.ComputeListDispatch(computeList, groupsGrid, 1, 1);
	_rd.ComputeListAddBarrier(computeList);

	// FASE 1: POPULATE GRID (Primero contar densidad actual)
	UpdatePushConstants(dt, t, 1, AgentCount);
	_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
	_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);
	_rd.ComputeListAddBarrier(computeList);

	// FASE 2: UPDATE AGENTS (Usa densidad + heightmap)
	UpdatePushConstants(dt, t, 2, AgentCount);
	_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
	_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);
	_rd.ComputeListAddBarrier(computeList);

	// FASE 3: PAINT POIS (ÚLTIMO, para próximo frame)
	UpdatePushConstants(dt, t, 3, (int)_gridResolution);
	_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
	_rd.ComputeListDispatch(computeList, gX, gY, gZ);

	_rd.ComputeListEnd();

	// 4. Sincronización CPU (Opcional, para UI)
	// No usar TextureGetData aquí cada frame, es muy lento. BufferGetData es aceptable para contadores pequeños.
	// byte[] counterBytes = _rd.BufferGetData(_counterBufferRid);
	// ActiveAgentCount = BitConverter.ToUInt32(counterBytes, 0);

	// DEBUG (Solo cada 60 frames)
	if (Engine.GetFramesDrawn() % 60 == 0) 
	{
		// DebugInspectGPUResults(); // Descomenta solo si necesitas inspeccionar
	}
}





	
	// --- IMPLEMENTACIÓN INTERNA ---

	

	// Helper para alinear memoria (ponlo en tu clase o en Utils)
	private int GetAlignedSize(int rawSize, int alignment = 16) {
		return (rawSize + (alignment - 1)) & ~(alignment - 1);
	}

	private unsafe void UpdatePushConstants(float delta, float time, uint phase, int customParam)
	{
		// 1. Llenar la estructura con los datos del frame
		var pushData = new AgentSimulationParams
		{
			Delta = delta,
			Time = time,
			PlanetRadius = _planetRadius, // Asegúrate que estas variables de clase estén actualizadas
			NoiseScale = _noiseScale,
			NoiseHeight = _noiseHeight,
			CustomParam = (uint)customParam,
			Phase = phase,
			GridRes = (uint)_gridResolution,
			TexWidth = (uint)DATA_TEX_WIDTH
		};

		// 2. Calcular tamaños
		int rawSize = Marshal.SizeOf<AgentSimulationParams>(); // 36 bytes
		int alignedSize = GetAlignedSize(rawSize, 16);         // 48 bytes (Fix del error de pipeline)

		// 3. Preparar el buffer de bytes (solo si cambia el tamaño)
		if (_pushConstantBuffer == null || _pushConstantBuffer.Length != alignedSize)
		{
			_pushConstantBuffer = new byte[alignedSize];
		}

		// 4. Copiar la estructura al array de bytes de forma segura
		// Esto copia los 36 bytes útiles y deja los 12 restantes (del 36 al 48) como ceros.
		fixed (byte* ptr = _pushConstantBuffer)
		{
			Marshal.StructureToPtr(pushData, (IntPtr)ptr, false);
		}
	}





	private void DebugInspectGPUResults()
	{
		// 1. Descargar los datos crudos de la textura de la GPU a la RAM
		// Esto es "lento", úsalo solo para debug.
		byte[] textureData = _rd.TextureGetData(_posTextureRid, 0);

		if (textureData == null || textureData.Length == 0)
		{
			GD.PrintErr("[GPU INSPECTOR] La textura está vacía o no se pudo leer.");
			return;
		}

		// 2. Leer el Primer Agente (Pixel 0,0)
		// Formato RGBA32F = 16 bytes por pixel (4 floats de 4 bytes)
		int offset = 0; 
		
		float x = BitConverter.ToSingle(textureData, offset + 0);
		float y = BitConverter.ToSingle(textureData, offset + 4);
		float z = BitConverter.ToSingle(textureData, offset + 8);
		float w = BitConverter.ToSingle(textureData, offset + 12); // Vida / Estado

		Vector3 pos = new Vector3(x, y, z);
		float dist = pos.Length();

		GD.Print("\n--- GPU OUTPUT INSPECTOR (Agent 0) ---");
		GD.Print($"Posición Raw: {pos}");
		GD.Print($"Distancia al Centro: {dist}");
		GD.Print($"Estado (W): {w}");

		// Diagnóstico inmediato
		if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
			GD.Print("VEREDICTO: [BASURA/NaN] -> El Compute Shader está leyendo mal los inputs o dividiendo por cero.");
		else if (dist < 1.0f)
			GD.Print("VEREDICTO: [CERO] -> El Compute Shader no está escribiendo o calcula 0.");
		else if (dist > 99.0f && dist < 101.0f)
			GD.Print("VEREDICTO: [CORRECTO] -> El Compute Shader funciona perfecto. EL CULPABLE ES EL AGENT_VISUAL (Render).");
		else
			GD.Print($"VEREDICTO: [ERROR MATEMÁTICO] -> El cálculo da {dist}, esperábamos ~100. Revisa la fórmula.");
			
		GD.Print("----------------------------------------\n");
	}



// --- LOCALIZACIÓN: AgentSystem.cs -> Método SetupData() ---

	private void SetupData()
{
	GD.Print("[AgentSystem] SetupData (Modo Debug: Agentes Vivos)");
	_cpuAgents = new AgentDataSphere[AgentCount];
	
	// Semilla para random
	var rng = new Random();

	for (int i = 0; i < AgentCount; i++)
	{
		AgentDataSphere initialAgent = new AgentDataSphere();

		// 1. POSICIÓN ALEATORIA VÁLIDA (Sobre la esfera unitaria)
		// Evitamos el (0,0,0) a toda costa.
		float theta = (float)(rng.NextDouble() * 2.0 * Math.PI);
		float phi = (float)(Math.Acos(2.0 * rng.NextDouble() - 1.0));
		
		float x = (float)(Math.Sin(phi) * Math.Cos(theta));
		float y = (float)(Math.Sin(phi) * Math.Sin(theta));
		float z = (float)(Math.Cos(phi));

		// Multiplicamos por un radio base temporal (ej. 105) para que no nazcan enterrados
		// W = 1.0f -> ¡IMPORTANTE! Esto le dice al shader "ESTOY VIVO"
		initialAgent.Position = new Vector4(x * 105.0f, y * 105.0f, z * 105.0f, 1.0f);

		// 2. VELOCIDAD CERO
		initialAgent.Velocity = Vector4.Zero;

		// 3. COLOR VISIBLE
		// W = 1.0f -> Life Flag para otros sistemas
		initialAgent.Color = new Vector4(1, 1, 1, 1.0f); // Blanco puro

		// Asignamos
		_cpuAgents[i] = initialAgent;
	}
}


	// --- REEMPLAZAR FUNCIÓN SetupCompute() COMPLETA ---
	private unsafe void SetupCompute()
	{
		GD.Print("[AgentSystem] SetupCompute - Iniciando");

		// 1. COMPILACIÓN DE SHADER (Lógica original correcta)
		if (ComputeShaderFile == null) { GD.PrintErr("ShaderFile null"); return; }
		
		RDShaderSpirV shaderSpirv;
		try { shaderSpirv = ComputeShaderFile.GetSpirV(); }
		catch (Exception e) { GD.PrintErr(e.Message); return; }

		if (!string.IsNullOrEmpty(shaderSpirv.CompileErrorCompute)) {
			GD.PrintErr($"[Shader Error] {shaderSpirv.CompileErrorCompute}");
			return;
		}

		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		if (!_shaderRid.IsValid) return;

		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);
		if (!_pipelineRid.IsValid) return;

		// 2. CREAR SAMPLER (Necesario para cubemaps)
		var samplerState = new RDSamplerState { 
			MagFilter = RenderingDevice.SamplerFilter.Linear, 
			MinFilter = RenderingDevice.SamplerFilter.Linear 
		};
		_samplerRid = _rd.SamplerCreate(samplerState);

		// 3. VALIDACIÓN DE RECURSOS (Creados en CreateInternalResources)
		if (!_bufferRid.IsValid || !_gridBufferRid.IsValid || !_deadListBufferRid.IsValid) {
			GD.PrintErr("[AgentSystem] CRITICAL: Faltan buffers internos. ¿Llamaste a CreateInternalResources?");
			return;
		}

		// 4. DEFINIR UNIFORMS (BINDINGS)
		
		// Binding 0: Agentes
		var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; 
		uAgent.AddId(_bufferRid);
		
		// Binding 1: Grid
		var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; 
		uGrid.AddId(_gridBufferRid);
		
		// Bindings 2 & 3: Texturas Out
		var uPosTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; uPosTex.AddId(_posTextureRid);
		var uColTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 }; uColTex.AddId(_colorTextureRid);
		
		// Bindings 4 & 5: Mapas
		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 4 }; 
		uHeight.AddId(_samplerRid); uHeight.AddId(_bakedHeightMap);
		
		var uVector = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 5 }; 
		uVector.AddId(_samplerRid); uVector.AddId(_bakedVectorField);
		
		// Binding 6: Density 3D (Externo)
		var uDensity = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 6 }; 
		uDensity.AddId(_densityTextureRid);
		
		// Binding 7: Counter
		var uCounter = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 7 }; 
		uCounter.AddId(_counterBufferRid);
		
		// Binding 8: POIs (Externo)
		var uPoi = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 8 };
		if (_poiBufferRid.IsValid) uPoi.AddId(_poiBufferRid);
		else GD.PrintErr("POI Buffer inválido");

		// --- BINDING 9: DEAD LIST (EL QUE FALTABA) ---
		var uDeadList = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 9 };
		uDeadList.AddId(_deadListBufferRid);

		// 5. CREAR UNIFORM SET FINAL
		// Importante: El orden en el array no importa, pero SÍ importa que estén todos los bindings definidos en el shader
		var uniforms = new Godot.Collections.Array<RDUniform> { 
			uAgent, uGrid, uPosTex, uColTex, uHeight, uVector, uDensity, uCounter, uPoi, 
			uDeadList // <--- NO OLVIDAR AGREGARLO AQUÍ
		};

		_uniformSetRid = _rd.UniformSetCreate(uniforms, _shaderRid, 0);

		if (_uniformSetRid.IsValid)
			GD.Print("[AgentSystem] UniformSet creado exitosamente (Set 0 con 10 bindings).");
		else
			GD.PrintErr("[AgentSystem] Falló UniformSetCreate. Revisa los tipos de variable.");
	}

	// private void SetupVisuals()
	// {
	// 	GD.Print("[AgentSystem] SetupVisuals");

	// 	// Buscar o crear el visualizer
	// 	if (_visualizer == null)
	// 	{
	// 		_visualizer = GetNodeOrNull<MultiMeshInstance3D>("AgentVisualizer");
	// 		if (_visualizer == null) {
	// 			_visualizer = new MultiMeshInstance3D { Name = "AgentVisualizer" };
	// 			AddChild(_visualizer);
	// 		}
	// 	}
		
	// 	_visualizer.Visible = true;

	// 	// Crear el ShaderMaterial PRIMERO
	// 	var agentMat = new ShaderMaterial();
	// 	var shader = GD.Load<Shader>("res://Shaders/Visual/agent_render.gdshader");
	// 	if (shader == null) {
	// 		GD.PrintErr("[AgentSystem] ERROR: No se pudo cargar el shader agent_render.gdshader");
	// 		return;
	// 	}
	// 	agentMat.Shader = shader;
		
	// 	// Configurar los uniforms del shader
	// 	if (_posTextureRef != null && _posTextureRef.TextureRdRid.IsValid) {
	// 		agentMat.SetShaderParameter("agent_pos_texture", _posTextureRef);
	// 		GD.Print("[AgentSystem] Textura de posiciones asignada al material.");
	// 	} else {
	// 		GD.PrintErr("[AgentSystem] ERROR: _posTextureRef es null o RID inválido");
	// 	}
		
	// 	if (_colorTextureRef != null && _colorTextureRef.TextureRdRid.IsValid) {
	// 		agentMat.SetShaderParameter("agent_color_texture", _colorTextureRef);
	// 		GD.Print("[AgentSystem] Textura de colores asignada al material.");
	// 	} else {
	// 		GD.PrintErr("[AgentSystem] ERROR: _colorTextureRef es null o RID inválido");
	// 	}
		
	// 	agentMat.SetShaderParameter("tex_width", DATA_TEX_WIDTH);
	// 	agentMat.SetShaderParameter("agent_radius_visual", 1.5f);

	// 	// Crear el QuadMesh y asignar el material a la superficie 0
	// 	var quadMesh = new QuadMesh { 
	// 		Size = new Vector2(1.0f, 1.0f), 
	// 		Orientation = QuadMesh.OrientationEnum.Z
	// 	};
	// 	quadMesh.SurfaceSetMaterial(0, agentMat);

	// 	// Crear el MultiMesh
	// 	_visualizer.Multimesh = new MultiMesh
	// 	{
	// 		TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
	// 		UseColors = false,
	// 		InstanceCount = AgentCount,
	// 		Mesh = quadMesh
	// 	};
		
	// 	// AABB muy grande para evitar culling
	// 	_visualizer.Multimesh.CustomAabb = new Aabb(new Vector3(-50000, -50000, -50000), new Vector3(100000, 100000, 100000));
		
	// 	GD.Print("[AgentSystem] SetupVisuals completado. Material con texturas asignado.");
	// }

	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int structSize = Marshal.SizeOf<AgentDataSphere>();
		int totalSize = structSize * data.Length;
		byte[] arr = new byte[totalSize];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { 
			Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, totalSize); 
		} finally { handle.Free(); } 
		return arr;
	}

	// Sobrecarga para un solo agente
	private byte[] StructureToByteArray(AgentDataSphere singleAgent) {
		int structSize = Marshal.SizeOf<AgentDataSphere>();
		byte[] arr = new byte[structSize];
		GCHandle handle = GCHandle.Alloc(singleAgent, GCHandleType.Pinned);
		try { 
			Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, structSize); 
		} finally { handle.Free(); } 
		return arr;
	}

	public override void _ExitTree()
	{
		if (_rd == null) return;
		void SafeFree(Rid rid) { if (rid.IsValid && rid.Id != 0) try { _rd.FreeRid(rid); } catch {} }

		SafeFree(_bufferRid);
		SafeFree(_gridBufferRid);
		SafeFree(_posTextureRid);
		SafeFree(_colorTextureRid);
		SafeFree(_densityTextureRid);
		SafeFree(_shaderRid);
		// Pipeline y Samplers se limpian a menudo con el contexto, pero se pueden añadir si hay leaks.
	}


	// --- DENTRO DE AgentSystem.cs ---

	
	// --- LOCALIZACIÓN: AgentSystem.cs -> Método SpawnAgent() ---

	public void SpawnAgent(Vector3 worldPos, int index)
	{ 
		if (_rd == null || !_isInitialized) return;

		if (!_bufferRid.IsValid) 
		{
			GD.PrintErr($"[AgentSystem] CRITICAL: Intento de Spawn en buffer inválido. Index: {index}");
			return; 
		}

		// 1. Extraer el dato actual del pool (Copia)
		AgentDataSphere agent = _cpuAgents[index];

		// 2. Modificar los campos necesarios
		agent.Position = new Vector4(worldPos.X, worldPos.Y, worldPos.Z, 1.0f); // W=1.0 activa el slot
		agent.Color = new Vector4(1, 1, 1, 1); // Blanco para visibilidad
		agent.Velocity = new Vector4(0, 0, 0, 1.0f);
		agent.GroupData = new Vector4(0, 0, 0, 0);

		// 3. Reinsertar en el pool local
		_cpuAgents[index] = agent;

		// 4. Sincronizar con VRAM usando la sobrecarga para un solo agente
		int structSize = Marshal.SizeOf<AgentDataSphere>();
		byte[] data = StructureToByteArray(agent);
		uint offset = (uint)(index * structSize);
		
		_rd.BufferUpdate(_bufferRid, offset, (uint)structSize, data);
		
		GD.Print($"[AgentSystem] Agente {index} spawneado en posición local: {worldPos}");
		
		// DEBUG: Verificar que el dato se escribió correctamente
		VerifyBufferData(index, agent);
	}

	private void VerifyBufferData(int index, AgentDataSphere expected)
	{
		if (Engine.GetFramesDrawn() % 60 != 0) return; // Solo cada 60 frames
		
		try {
			int structSize = Marshal.SizeOf<AgentDataSphere>();
			byte[] readBack = _rd.BufferGetData(_bufferRid, (uint)(index * structSize), (uint)structSize);
			if (readBack != null && readBack.Length >= structSize * 4) {
				float x = BitConverter.ToSingle(readBack, 0);
				float y = BitConverter.ToSingle(readBack, 4);
				float z = BitConverter.ToSingle(readBack, 8);
				float w = BitConverter.ToSingle(readBack, 12);
				GD.Print($"[DEBUG BUFFER] Agente {index}: pos=({x:F2}, {y:F2}, {z:F2}), w={w}");
			}
		} catch (Exception e) {
			GD.PrintErr($"[DEBUG BUFFER] Error: {e.Message}");
		}
	}


	// --- AÑADIR AL FINAL DE LA CLASE AgentSystem ---

	public void SpawnRandomAgents(int count)
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();
		int spawnedCount = 0;

		for (int i = 0; i < AgentCount; i++)
		{
			if (spawnedCount >= count) break;

			// Solo usamos slots inactivos (W < 0.1)
			if (_cpuAgents[i].Position.W < 0.1f)
			{
				// Distribución esférica uniforme
				float phi = rng.Randf() * Mathf.Tau;
				float cosTheta = rng.RandfRange(-1.0f, 1.0f);
				float theta = Mathf.Acos(cosTheta);

				Vector3 randomDir = new Vector3(
					Mathf.Sin(theta) * Mathf.Cos(phi),
					Mathf.Sin(theta) * Mathf.Sin(phi),
					Mathf.Cos(theta)
				);

				SpawnAgent(randomDir * _planetRadius, i);
				spawnedCount++;
			}
		}
		GD.Print($"[AgentSystem] Spawn masivo completado: {spawnedCount} agentes activados.");
	}

	public Rid GetInfluenceTexture()
	{
		return _densityTextureRid;
	}

	public Rid GetPosTextureRid()
	{
		if (!_posTextureRid.IsValid)
		{
			GD.PrintErr("[AgentSystem] Advertencia: Se intentó obtener PosTextureRid antes de inicializar.");
		}
		return _posTextureRid;
	}

	// Devuelve el ID de la textura donde el Compute Shader escribe los colores/estados.
	public Rid GetColorTextureRid()
	{
		if (!_colorTextureRid.IsValid)
		{
			GD.PrintErr("[AgentSystem] Advertencia: Se intentó obtener ColorTextureRid antes de inicializar.");
			CreateInternalResources();
		}
		return _colorTextureRid;
	}


	// Asegúrate de tener esta constante definida al inicio de la clase
	// private const int DATA_TEX_WIDTH = 2048; 

	private void CreateInternalResources()
	{
		if (_rd == null) return;

		// --- 1. TEXTURAS VISUALES (Visualización de puntos) ---
		// Usamos el formato DataFormat.R32G32B32A32Sfloat para posiciones y colores
		int texHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
		var fmt = new RDTextureFormat
		{
			Width = (uint)DATA_TEX_WIDTH,
			Height = (uint)texHeight,
			Depth = 1,
			TextureType = RenderingDevice.TextureType.Type2D,
			Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
						RenderingDevice.TextureUsageBits.SamplingBit | 
						RenderingDevice.TextureUsageBits.CanUpdateBit | 
						RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		
		if (!_posTextureRid.IsValid) _posTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
		if (!_colorTextureRid.IsValid) _colorTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
		
		// _posTextureRef = new Texture2Drd { TextureRdRid = _posTextureRid };
		// _colorTextureRef = new Texture2Drd { TextureRdRid = _colorTextureRid };

		// --- 2. BUFFERS INTERNOS ---

		// A. Buffer Principal de Agentes (Binding 0) -> ¡¡ESTE FALTABA EN TU CODIGO!!
		if (!_bufferRid.IsValid)
		{
			// Asumo que tienes un método helper StructureToByteArray, o usas Marshal
			// Si _cpuAgents ya tiene datos, los subimos. Si no, reservamos espacio.
			long sizeBytes = AgentCount * System.Runtime.InteropServices.Marshal.SizeOf<AgentDataSphere>();
			_bufferRid = _rd.StorageBufferCreate((uint)sizeBytes);
			
			// Opcional: Si SetupData() lo llena después, aquí basta con crearlo.
		}






		if (_cpuAgents != null && _cpuAgents.Length > 0)
		{
			byte[] initialData = StructureToByteArray(_cpuAgents);
			_rd.BufferUpdate(_bufferRid, 0, (uint)initialData.Length, initialData);
		}







		// B. Counter Buffer (Binding 7)
		if (!_counterBufferRid.IsValid)
		{
			_counterBufferRid = _rd.StorageBufferCreate(4);
			_rd.BufferClear(_counterBufferRid, 0, 4); 
		}

		// C. Grid Buffer (Binding 1)
		if (!_gridBufferRid.IsValid)
		{
			// Calculo seguro del tamaño basado en la resolución actual
			long totalCells = (long)_gridResolution * _gridResolution * _gridResolution;
			uint gridSize = (uint)(totalCells * 4); // 4 bytes por uint
			
			_gridBufferRid = _rd.StorageBufferCreate(gridSize);
			_rd.BufferClear(_gridBufferRid, 0, gridSize);
		}
		
		// D. Dead List Buffer (Binding 9)
		if (!_deadListBufferRid.IsValid) 
		{
			uint deadListSize = (uint)AgentCount * 4;
			_deadListBufferRid = _rd.StorageBufferCreate(deadListSize);
		}

		// E. Push Constants (CPU)
		if (_pushConstantBuffer == null || _pushConstantBuffer.Length != 48)
		{
			_pushConstantBuffer = new byte[48];
		}
	}


}
