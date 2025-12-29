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
	private Rid _posTextureRid, _colorTextureRid;
	private Rid _samplerRid;
	
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

		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
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

		_rd.ComputeListEnd();
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

	private void SetupData()
	{
		_cpuAgents = new AgentDataSphere[AgentCount];
		var rng = new RandomNumberGenerator();
		Vector3 targetA = Vector3.Down * _planetRadius;
		Vector3 targetB = Vector3.Up * _planetRadius;

		for (int i = 0; i < AgentCount; i++)
		{
			bool isTeamA = i < (AgentCount / 2);
			float u = rng.RandfRange(0.0f, 0.95f);
			float spread = Mathf.Acos(1.0f - u);
			float az = rng.Randf() * Mathf.Tau;
			
			float y = isTeamA ? Mathf.Cos(spread) : -Mathf.Cos(spread);
			float r = Mathf.Sin(spread);
			float x = r * Mathf.Cos(az);
			float z = r * Mathf.Sin(az);
			
			Vector3 pos = new Vector3(x, y, z).Normalized() * _planetRadius;
			Vector4 color = isTeamA ? new Vector4(0, 0.5f, 1, 1) : new Vector4(1, 0.2f, 0, 1);
			Vector3 tgt = isTeamA ? targetA : targetB;

			_cpuAgents[i] = new AgentDataSphere {
				Position = new Vector4(pos.X, pos.Y, pos.Z, 0.5f),
				Target = new Vector4(tgt.X, tgt.Y, tgt.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 8.0f),
				Color = color
			};
		}
	}

	private unsafe void SetupCompute()
	{
		var shaderSpirv = ComputeShaderFile.GetSpirV();
		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

		int bytes = Marshal.SizeOf<AgentDataSphere>() * AgentCount;
		byte[] initData = StructureToByteArray(_cpuAgents);
		_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);
		
		int gridBytes = GRID_TOTAL_CELLS * 4; 
		_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes);
		_rd.BufferClear(_gridBufferRid, 0, (uint)gridBytes);

		int texHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
		var fmt = new RDTextureFormat {
			Width = (uint)DATA_TEX_WIDTH, Height = (uint)texHeight, Depth = 1,
			Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		_posTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
		_colorTextureRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
		_posTextureRef = new Texture2Drd { TextureRdRid = _posTextureRid };
		_colorTextureRef = new Texture2Drd { TextureRdRid = _colorTextureRid };

		var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; uAgent.AddId(_bufferRid);
		var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; uGrid.AddId(_gridBufferRid);
		var uPosTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; uPosTex.AddId(_posTextureRid);
		var uColTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 }; uColTex.AddId(_colorTextureRid);
		
		var samplerState = new RDSamplerState { MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear };
		_samplerRid = _rd.SamplerCreate(samplerState);
		
		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 4 }; 
		uHeight.AddId(_samplerRid); uHeight.AddId(_bakedHeightMap);
		var uVector = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 5 }; 
		uVector.AddId(_samplerRid); uVector.AddId(_bakedVectorField);

		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uAgent, uGrid, uPosTex, uColTex, uHeight, uVector }, _shaderRid, 0);
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
		SafeFree(_shaderRid);
		// Pipeline y Samplers se limpian a menudo con el contexto, pero se pueden añadir si hay leaks.
	}
}
