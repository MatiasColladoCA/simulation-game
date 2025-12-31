using Godot;
using System;
using System.Runtime.InteropServices;

// Mantenemos el struct aquí
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentDataSphere
{
	public Vector4 Position; 
	public Vector4 Target;   
	public Vector4 Velocity; 
	public Vector4 Color;    
}

public partial class AgentSystem : Node3D
{
	[Export] public RDShaderFile ComputeShaderFile;
	[Export] public int AgentCount = 5000;
	
	// Referencias internas
	private MultiMeshInstance3D _visualizer;
	private RenderingDevice _rd;
	
	// RIDs
	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid, _gridBufferRid;
	private Rid _posTextureRid, _colorTextureRid, _densityTextureRid;
	
	
	private Rid _samplerRid, _counterBufferRid;
	public uint ActiveAgentCount { get; private set; }
	
	// Datos externos (Inyectados)
	private Rid _bakedHeightMap;
	private Rid _bakedVectorField;
	private float _planetRadius;
	private float _noiseScale;
	private float _noiseHeight;

	private Texture2Drd _posTextureRef, _colorTextureRef;
	private AgentDataSphere[] _cpuAgents;
	private byte[] _pushConstantBuffer = new byte[48];
	private bool _isInitialized = false;

	// Constantes
	private const int DATA_TEX_WIDTH = 2048; 
	private const int GRID_RES = 64; 
	private const int GRID_TOTAL_CELLS = GRID_RES * GRID_RES * GRID_RES;

	// --- API PÚBLICA ---

	public void Initialize(RenderingDevice rd, Rid heightMap, Rid vectorField, float radius, float nScale, float nHeight)
	{
		_rd = rd;
		_bakedHeightMap = heightMap;
		_bakedVectorField = vectorField;
		_planetRadius = radius;
		_noiseScale = nScale;
		_noiseHeight = nHeight;

		SetupData();
		SetupCompute();
		SetupVisuals();
		
		_isInitialized = true;
		GD.Print("[AgentSystem] Initialized.");
	}



	public void UpdateSimulation(double delta, double time)
	{
		if (!_isInitialized || _rd == null) return;

		float dt = (float)delta;
		float t = (float)time;

		uint groupsGrid = (uint)Mathf.CeilToInt(GRID_TOTAL_CELLS / 64.0f);
		uint groupsAgents = (uint)Mathf.CeilToInt(AgentCount / 64.0f);

		_rd.BufferClear(_counterBufferRid, 0, 4);
		var computeList = _rd.ComputeListBegin();		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);

		// FASE 0: CLEAR
		UpdatePushConstants(dt, t, 0, GRID_RES); 
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsGrid, 1, 1);
		_rd.ComputeListAddBarrier(computeList);

		// FASE 1: POPULATE
		UpdatePushConstants(dt, t, 1, AgentCount);
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);
		_rd.ComputeListAddBarrier(computeList);

		// FASE 2: UPDATE
		UpdatePushConstants(dt, t, 2, AgentCount);
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);

		// _rd.ComputeListEnd();
		// _rd.Submit();
		// _rd.Sync(); // Sincronización necesaria para lectura inmediata
		// byte[] counterBytes = _rd.BufferGetData(_counterBufferRid);
		// ActiveAgentCount = BitConverter.ToUInt32(counterBytes, 0);
		_rd.ComputeListEnd();
		// Al usar el dispositivo principal, no se debe llamar a Submit ni Sync.
		// BufferGetData provocará una sincronización automática y segura con el servidor de renderizado.
		byte[] counterBytes = _rd.BufferGetData(_counterBufferRid);
	}
	





	
	// --- IMPLEMENTACIÓN INTERNA ---

	private unsafe void UpdatePushConstants(float delta, float time, uint phase, int customParam)
	{
		fixed (byte* ptr = _pushConstantBuffer)
		{
			float* fPtr = (float*)ptr;
			uint* uPtr = (uint*)ptr;
			
			fPtr[0] = delta;         
			fPtr[1] = time;          
			fPtr[2] = _planetRadius;  
			fPtr[3] = _noiseScale;    
			fPtr[4] = _noiseHeight;   
			
			uPtr[5] = (uint)customParam; 
			uPtr[6] = phase;             
			uPtr[7] = (uint)GRID_RES;    
			uPtr[8] = (uint)DATA_TEX_WIDTH; 
		}
	}



// --- LOCALIZACIÓN: AgentSystem.cs -> Método SetupData() ---

	private void SetupData()
	{
		_cpuAgents = new AgentDataSphere[AgentCount];
		
		for (int i = 0; i < AgentCount; i++)
		{
			// Creamos el struct completo primero
			AgentDataSphere initialAgent = new AgentDataSphere();
			
			// El componente W = 0.0f indica estado "Inactivo/Dormido"
			initialAgent.Position = new Vector4(0, 0, 0, 0.0f); 
			initialAgent.Target = Vector4.Zero;
			initialAgent.Velocity = Vector4.Zero;
			initialAgent.Color = Vector4.Zero;

			// Asignamos el struct al array
			// COMENTARIO: Esto evita el error CS0131
			_cpuAgents[i] = initialAgent;
		}
	}




// --- REEMPLAZAR FUNCIÓN SetupCompute() COMPLETA ---
private unsafe void SetupCompute()
{
	// 1. COMPILAR SHADER (Si esto falla, el resto no se ejecuta)
	var shaderSpirv = ComputeShaderFile.GetSpirV();
	_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
	if (!_shaderRid.IsValid) { GD.PrintErr("Fallo crítico: Bytecode de shader inválido."); return; }
	_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

	// 2. CREAR TODOS LOS RECURSOS (RIDs)
	_bufferRid = _rd.StorageBufferCreate((uint)(Marshal.SizeOf<AgentDataSphere>() * AgentCount), StructureToByteArray(_cpuAgents));
	_gridBufferRid = _rd.StorageBufferCreate((uint)(GRID_TOTAL_CELLS * 4));
	_counterBufferRid = _rd.StorageBufferCreate(4);
	_rd.BufferClear(_counterBufferRid, 0, 4);

	var samplerState = new RDSamplerState { MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear };
	_samplerRid = _rd.SamplerCreate(samplerState);

	int texHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
	var fmt = new RDTextureFormat {
		Width = (uint)DATA_TEX_WIDTH, Height = (uint)texHeight, Depth = 1,
		Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
		UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
	};
	_posTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
	_colorTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());

	var fmt3d = new RDTextureFormat {
		Width = GRID_RES, Height = GRID_RES, Depth = GRID_RES,
		TextureType = RenderingDevice.TextureType.Type3D,
		Format = RenderingDevice.DataFormat.R8Unorm,
		UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit
	};
	_densityTextureRid = _rd.TextureCreate(fmt3d, new RDTextureView(), new Godot.Collections.Array<byte[]>());

	_posTextureRef = new Texture2Drd { TextureRdRid = _posTextureRid };
	_colorTextureRef = new Texture2Drd { TextureRdRid = _colorTextureRid };

	// 3. DEFINIR UNIFORMES (Usando RIDs ya creados)
	var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; uAgent.AddId(_bufferRid);
	var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; uGrid.AddId(_gridBufferRid);
	var uPosTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; uPosTex.AddId(_posTextureRid);
	var uColTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 }; uColTex.AddId(_colorTextureRid);
	
	var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 4 }; 
	uHeight.AddId(_samplerRid); uHeight.AddId(_bakedHeightMap);
	
	var uVector = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 5 }; 
	uVector.AddId(_samplerRid); uVector.AddId(_bakedVectorField);
	
	var uDensity = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 6 }; uDensity.AddId(_densityTextureRid);
	var uCounter = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 7 }; uCounter.AddId(_counterBufferRid);

	// 4. CREAR UNIFORM SET FINAL
	_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { 
		uAgent, uGrid, uPosTex, uColTex, uHeight, uVector, uDensity, uCounter 
	}, _shaderRid, 0);
}



	private void SetupVisuals()
	{
		if (_visualizer == null)
		{
			_visualizer = GetNodeOrNull<MultiMeshInstance3D>("AgentVisualizer");
			if (_visualizer == null) {
				_visualizer = new MultiMeshInstance3D { Name = "AgentVisualizer" };
				AddChild(_visualizer);
			}
		}
		
		_visualizer.Visible = true;
		_visualizer.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = false,
			InstanceCount = AgentCount,
			Mesh = new QuadMesh { 
				Size = new Vector2(1.0f, 1.0f), 
				Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Visual/SphereImpostor.gdshader") } 
			}
		};
		_visualizer.Multimesh.CustomAabb = new Aabb(new Vector3(-2000, -2000, -2000), new Vector3(4000, 4000, 4000));

		var agentMat = _visualizer.Multimesh.Mesh.SurfaceGetMaterial(0) as ShaderMaterial;
		if (agentMat != null) {
			agentMat.SetShaderParameter("agent_pos_texture", _posTextureRef);
			agentMat.SetShaderParameter("agent_color_texture", _colorTextureRef);
			agentMat.SetShaderParameter("tex_width", DATA_TEX_WIDTH);
		}
	}

	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int size = Marshal.SizeOf<AgentDataSphere>(); byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); } finally { handle.Free(); } return arr;
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

		// 1. Extraer el dato actual del pool (Copia)
		AgentDataSphere agent = _cpuAgents[index];

		// 2. Modificar los campos necesarios
		agent.Position = new Vector4(worldPos.X, worldPos.Y, worldPos.Z, 1.0f); // W=1.0 activa el slot
		agent.Color = new Vector4(1, 1, 1, 1); // Blanco para visibilidad
		agent.Velocity = new Vector4(0, 0, 0, 10.0f); // Reset de velocidad

		// 3. Reinsertar en el pool local
		_cpuAgents[index] = agent;

		// 4. Sincronizar con VRAM
		int structSize = Marshal.SizeOf<AgentDataSphere>();
		byte[] data = StructureToByteArray(new AgentDataSphere[] { agent });
		uint offset = (uint)(index * structSize);
		
		_rd.BufferUpdate(_bufferRid, offset, (uint)structSize, data);
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

}
