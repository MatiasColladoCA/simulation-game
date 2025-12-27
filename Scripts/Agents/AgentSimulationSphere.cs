using Godot;
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentDataSphere
{
	public Vector4 Position; 
	public Vector4 Target;   
	public Vector4 Velocity; 
	public Vector4 Color;    
}

public partial class AgentSimulationSphere : Node3D
{
	[Export] public PlanetRender PlanetRenderer;
	[Export] public int AgentCount = 1000; 
	[Export] public RDShaderFile ComputeShaderFile;
	[Export] public RDShaderFile BakerShaderFile;
	
	[Export] public float PlanetRadius = 100.0f;
	[Export] public float NoiseScale = 2.0f;
	[Export] public float NoiseHeight = 10.0f;

	// NUEVO: Control deslizante para el nivel del agua (0 a 1)
	[Export(PropertyHint.Range, "0.0, 1.0")] 
	public float WaterLevel = 0.45f;
	
	private MultiMeshInstance3D _visualizer;
	private RenderingDevice _rd;

	private Rid _indirectBufferRid; // El buffer que contiene las órdenes de trabajo
	private double _timeSinceLastUiUpdate = 0;


	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid, _gridBufferRid;
	private Rid _posTextureRid, _colorTextureRid; 
	private Texture2Drd _posTextureRef, _colorTextureRef;
	private Rid _samplerRid, _bakedCubemapRid, _vectorFieldRid;

	private AgentDataSphere[] _cpuAgents; 
	private Label _statsLabel;
	
	// --- CONSTANTES ---
	private const int CUBEMAP_SIZE = 1024; 
	private const int DATA_TEX_WIDTH = 2048; 
	
	// Grilla de Densidad (64x64x64)
	private const int GRID_RES = 64; 
	private const int GRID_TOTAL_CELLS = GRID_RES * GRID_RES * GRID_RES;

	private byte[] _pushConstantBuffer = new byte[48]; 

	public override void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();
		SetupUI();
		
		// 1. Hornear Terreno (Es vital que esto ocurra antes de SetupCompute)
		if (BakerShaderFile != null) BakeTerrain();
		else GD.PrintErr("Falta BakerShaderFile");
		
		// 2. Inicializar Datos y Compute
		SetupData();
		SetupCompute();
		
		// 3. Inicializar Visuales
		SetupVisuals(); 
		SetupMarkers();
	}

public override void _Process(double delta)
	{
		if (_rd == null || !_uniformSetRid.IsValid) return;

		float dt = (float)delta;
		float time = (float)Time.GetTicksMsec() / 1000.0f;

		// Calculamos grupos en CPU (Es muy barato, no vale la pena complicarse)
		uint groupsGrid = (uint)Mathf.CeilToInt(GRID_TOTAL_CELLS / 64.0f);
		uint groupsAgents = (uint)Mathf.CeilToInt(AgentCount / 64.0f);

		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);

		// --- FASE 0: CLEAR ---
		UpdateSimPushConstants(dt, time, 0, GRID_RES); 
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsGrid, 1, 1);
		_rd.ComputeListAddBarrier(computeList);

		// --- FASE 1: POPULATE ---
		UpdateSimPushConstants(dt, time, 1, AgentCount);
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);
		_rd.ComputeListAddBarrier(computeList);

		// --- FASE 2: UPDATE ---
		UpdateSimPushConstants(dt, time, 2, AgentCount);
		_rd.ComputeListSetPushConstant(computeList, _pushConstantBuffer, (uint)_pushConstantBuffer.Length);
		_rd.ComputeListDispatch(computeList, groupsAgents, 1, 1);

		_rd.ComputeListEnd();

		// --- UI THROTTLING (Para no ahogar la CPU actualizando texto) ---
		_timeSinceLastUiUpdate += delta;
		if (_timeSinceLastUiUpdate > 0.25) // 4 veces por segundo
		{
			if (_statsLabel != null)
				 _statsLabel.Text = $"AGENTS: {AgentCount}\nFPS: {Engine.GetFramesPerSecond()}";
			_timeSinceLastUiUpdate = 0;
		}
	}



	private unsafe void UpdateSimPushConstants(float delta, float time, uint phase, int customParam)
	{
		// Uso de punteros para evitar overhead de arrays y GC
		fixed (byte* ptr = _pushConstantBuffer)
		{
			float* fPtr = (float*)ptr;
			uint* uPtr = (uint*)ptr;
			
			// Escribimos directo en la memoria del buffer byte[]
			fPtr[0] = delta;         // Offset 0
			fPtr[1] = time;          // Offset 4
			fPtr[2] = PlanetRadius;  // Offset 8
			fPtr[3] = NoiseScale;    // Offset 12
			fPtr[4] = NoiseHeight;   // Offset 16
			
			uPtr[5] = (uint)customParam; // Offset 20
			uPtr[6] = phase;             // Offset 24
			uPtr[7] = (uint)GRID_RES;    // Offset 28
			uPtr[8] = (uint)DATA_TEX_WIDTH; // Offset 32
		}
	}

	

	private void PutFloat(float val, int offset) {
		BitConverter.TryWriteBytes(new Span<byte>(_pushConstantBuffer, offset, 4), val);
	}
	private void PutUint(uint val, int offset) {
		BitConverter.TryWriteBytes(new Span<byte>(_pushConstantBuffer, offset, 4), val);
	}

private unsafe void SetupCompute()
	{
		if (ComputeShaderFile == null) return;
		var shaderSpirv = ComputeShaderFile.GetSpirV();
		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		if (!_shaderRid.IsValid) { GD.PrintErr("Error Shader Simulación"); return; }
		
		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

		// --- 1. BUFFER DE AGENTES ---
		int bytes = Marshal.SizeOf<AgentDataSphere>() * AgentCount;
		byte[] initData = StructureToByteArray(_cpuAgents);
		_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);
		
		// --- 2. BUFFER DE DENSIDAD ---
		// Volvemos a la creación simple. No necesitamos flags especiales.
		int gridBytes = GRID_TOTAL_CELLS * 4; 
		_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes);
		_rd.BufferClear(_gridBufferRid, 0, (uint)gridBytes);

		// --- 3. TEXTURAS ---
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

		// --- 4. UNIFORMS ---
		var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; uAgent.AddId(_bufferRid);
		var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; uGrid.AddId(_gridBufferRid);
		var uPosTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 }; uPosTex.AddId(_posTextureRid);
		var uColTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 }; uColTex.AddId(_colorTextureRid);
		
		var samplerState = new RDSamplerState { MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear };
		_samplerRid = _rd.SamplerCreate(samplerState);
		
		if (!_bakedCubemapRid.IsValid || !_vectorFieldRid.IsValid) { GD.PrintErr("CRÍTICO: Texturas Baker no válidas."); return; }

		var uHeight = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 4 }; 
		uHeight.AddId(_samplerRid); uHeight.AddId(_bakedCubemapRid);
		var uVector = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 5 }; 
		uVector.AddId(_samplerRid); uVector.AddId(_vectorFieldRid);

		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uAgent, uGrid, uPosTex, uColTex, uHeight, uVector }, _shaderRid, 0);
	}

	private void BakeTerrain()
	{
		// 1. Texturas
		var fmtHeight = new RDTextureFormat {
			Width = (uint)CUBEMAP_SIZE, Height = (uint)CUBEMAP_SIZE, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R32Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		_bakedCubemapRid = _rd.TextureCreate(fmtHeight, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		var fmtVectors = new RDTextureFormat {
			Width = (uint)CUBEMAP_SIZE, Height = (uint)CUBEMAP_SIZE, Depth = 1, ArrayLayers = 6,
			TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
		};
		_vectorFieldRid = _rd.TextureCreate(fmtVectors, new RDTextureView(), new Godot.Collections.Array<byte[]>());

		// 2. Pipeline
		var bakerSpirv = BakerShaderFile.GetSpirV();
		var bakerShaderRid = _rd.ShaderCreateFromSpirV(bakerSpirv);
		var bakerPipeline = _rd.ComputePipelineCreate(bakerShaderRid);

		// 3. Uniforms
		var uOutHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOutHeight.AddId(_bakedCubemapRid);
		var uOutVectors = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 }; uOutVectors.AddId(_vectorFieldRid);
		var bakerSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uOutHeight, uOutVectors }, bakerShaderRid, 0);

		// 4. Dispatch (Manual MemoryStream para el baker)
		var stream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(stream);
		writer.Write((float)PlanetRadius);
		writer.Write((float)NoiseScale);
		writer.Write((float)NoiseHeight);
		writer.Write((uint)CUBEMAP_SIZE);
		byte[] pushBytes = stream.ToArray();
		
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, bakerPipeline);
		_rd.ComputeListBindUniformSet(computeList, bakerSet, 0);
		_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
		
		uint groups = (uint)Mathf.CeilToInt(CUBEMAP_SIZE / 32.0f);
		_rd.ComputeListDispatch(computeList, groups, groups, 6);
		_rd.ComputeListEnd();
		
		_rd.FreeRid(bakerPipeline); _rd.FreeRid(bakerShaderRid); _rd.FreeRid(bakerSet);
		GD.Print("Terrain Baked.");
	}

	private void SetupData()
	{
		_cpuAgents = new AgentDataSphere[AgentCount];
		var rng = new RandomNumberGenerator();
		Vector3 targetA = Vector3.Down * PlanetRadius;
		Vector3 targetB = Vector3.Up * PlanetRadius;

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
			
			Vector3 pos = new Vector3(x, y, z).Normalized() * PlanetRadius;
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

		private void SetupVisuals()
	{
		// 1. VISUALIZADOR DE AGENTES
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
		
		// 2. INICIALIZAR PLANETA (LIMPIO)
		// Eliminamos el bloque sucio. Ahora solo pasamos WaterLevel como 5to argumento.
		// PlanetRender se encarga de pasárselo al shader internamente.
		if (PlanetRenderer != null && _bakedCubemapRid.IsValid) {
			PlanetRenderer.Initialize(_bakedCubemapRid, _vectorFieldRid, PlanetRadius, NoiseHeight, WaterLevel);
		}

		// 3. ESFERA DE AGUA (VISUAL)
		var waterNode = GetNodeOrNull<MeshInstance3D>("WaterSphere");
		if (waterNode == null) {
			waterNode = new MeshInstance3D { Name = "WaterSphere" };
			AddChild(waterNode);
		}

		// El radio visual del agua depende del nivel
		float waterRadius = PlanetRadius + (NoiseHeight * WaterLevel); 
		
		waterNode.Mesh = new SphereMesh { 
			Radius = waterRadius, 
			Height = waterRadius * 2.0f 
		};

		var waterMat = new StandardMaterial3D();
		waterMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		waterMat.AlbedoColor = new Color(0.0f, 0.2f, 0.8f, 0.6f); 
		waterMat.Roughness = 0.1f; 
		waterMat.Metallic = 0.5f;  
		waterMat.EmissionEnabled = true;
		waterMat.Emission = new Color(0.0f, 0.1f, 0.3f); 
		waterMat.EmissionEnergyMultiplier = 0.5f;
		
		waterNode.MaterialOverride = waterMat;
	}


	private void SetupMarkers() { /* Opcional: tus marcadores */ }
	
	private void SetupUI() {
		var canvas = new CanvasLayer(); AddChild(canvas);
		_statsLabel = new Label(); _statsLabel.Position = new Vector2(10, 10);
		_statsLabel.LabelSettings = new LabelSettings { FontSize = 24, OutlineSize = 4, OutlineColor = Colors.Black };
		canvas.AddChild(_statsLabel);
	}

	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int size = Marshal.SizeOf<AgentDataSphere>(); byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); } finally { handle.Free(); } return arr;
	}
}
