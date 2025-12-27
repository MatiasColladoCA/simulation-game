using Godot;
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentDataSphere
{
	public Vector4 Position; // xyz: pos, w: radius
	public Vector4 Target;   // xyz: target
	public Vector4 Velocity; // xyz: vel, w: max_speed
	public Vector4 Color;    // w: status
}

public partial class AgentSimulationSphere : Node3D
{
	[Export] public int AgentCount = 10000; // ¡Probemos con 50k!
	[Export] public RDShaderFile ComputeShaderFile;
	
	// Parámetros del Planeta
	[Export] public float PlanetRadius = 100.0f;
	[Export] public float NoiseScale = 2.0f;
	[Export] public float NoiseHeight = 10.0f;
	
	[Export] private MultiMeshInstance3D _visualizer;

	// [Export] public Cubemap HeightMapCubemap;

	[Export] public RDShaderFile BakerShaderFile;
	// Variable para guardar el RID del Cubemap generado
	private const int CUBEMAP_SIZE = 1024; // Resolución por cara (1024x1024)

	private RenderingDevice _rd;
	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid, _gridBufferRid;
	// Nuevas texturas para comunicar GPU Compute -> GPU Visual
	private Rid _posTextureRid, _colorTextureRid; 
	private Texture2Drd _posTextureRef, _colorTextureRef;

	// --- VARIABLES DEL CUBEMAP ---
	private Rid _samplerRid; 
	private Rid _bakedCubemapRid; // <--- ESTA FALTABA EN TU CONTEXTO

	private AgentDataSphere[] _cpuAgents; // Solo para init
	private Label _statsLabel;
	
	// Configuración de la "Hoja de Datos" (Textura)
	private const int DATA_TEX_WIDTH = 2048; // Ancho fijo de la textura de datos
	
	// Constantes Grilla
	private const int GRID_SIZE = 131071; // Aumentado para 50k
	private const int CELL_CAPACITY = 48; 

	public override void _Ready()
	{
		SetupUI();
		SetupData();
		SetupCompute();
		SetupVisuals(); 
		SetupMarkers();
	}

private void SetupData()
	{
		_cpuAgents = new AgentDataSphere[AgentCount];
		var rng = new RandomNumberGenerator();

		Vector3 targetA = Vector3.Down * PlanetRadius; // Equipo A va al Sur
		Vector3 targetB = Vector3.Up * PlanetRadius;   // Equipo B va al Norte

		for (int i = 0; i < AgentCount; i++)
		{
			// 1. Definir Equipo
			bool isTeamA = i < (AgentCount / 2);
			
			// 2. Definir Polo de Origen
			Vector3 pole = isTeamA ? Vector3.Up : Vector3.Down;
			
			// 3. Distribución Uniforme en Hemisferio (Evita amontonamiento en el polo)
			// 'u' va de 0 (Polo) a 0.95 (Casi Ecuador). Si fuera 1.0 tocarían el ecuador.
			float u = rng.RandfRange(0.0f, 0.95f); 
			
			// Matemáticamente, para distribuir parejo en una esfera, el ángulo no es lineal.
			// Usamos Acos para corregir la densidad del área.
			float spreadAngle = Mathf.Acos(1.0f - u); 
			
			// Ángulo de rotación alrededor del polo (Azimuth)
			float azimuthAngle = rng.Randf() * Mathf.Tau;

			// 4. Calcular Posición
			// Rotamos el vector "Arriba" primero hacia un lado (spread) y luego alrededor (azimuth)
			// Nota: Usamos lógica de Cuaterniones implícita o vectores base para rotar desde el polo correcto.
			
			Vector3 offsetDir;
			if (isTeamA) // Polo Norte (Up)
			{
				// Convertir esféricas a cartesianas partiendo de Y-Up
				float x = Mathf.Sin(spreadAngle) * Mathf.Cos(azimuthAngle);
				float z = Mathf.Sin(spreadAngle) * Mathf.Sin(azimuthAngle);
				float y = Mathf.Cos(spreadAngle);
				offsetDir = new Vector3(x, y, z);
			}
			else // Polo Sur (Down)
			{
				// Invertimos Y para el hemisferio sur
				float x = Mathf.Sin(spreadAngle) * Mathf.Cos(azimuthAngle);
				float z = Mathf.Sin(spreadAngle) * Mathf.Sin(azimuthAngle);
				float y = -Mathf.Cos(spreadAngle);
				offsetDir = new Vector3(x, y, z);
			}

			Vector3 startPos = offsetDir.Normalized() * PlanetRadius;
			
			// Targets cruzados
			Vector3 myTarget = isTeamA ? targetA : targetB;
			
			// Colores de equipo
			Vector4 color = isTeamA ? new Vector4(0, 0.5f, 1, 1) : new Vector4(1, 0.2f, 0, 1);

			_cpuAgents[i] = new AgentDataSphere
			{
				Position = new Vector4(startPos.X, startPos.Y, startPos.Z, 0.5f), // W = Radio
				Target = new Vector4(myTarget.X, myTarget.Y, myTarget.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 8.0f), // W = Max Speed
				Color = color
			};
		}
	}



	private void SetupCompute()
		{
			// 1. Validaciones e Inicialización Global
			if (ComputeShaderFile == null || BakerShaderFile == null) { GD.PrintErr("Faltan Shaders"); return; }
			
			_rd = RenderingServer.GetRenderingDevice();
			if (_rd == null)
			{
				GD.PrintErr("Error: No se pudo obtener RenderingDevice (Usa Vulkan).");
				return;
			}

			// 2. EJECUTAR EL BAKER (Genera _bakedCubemapRid)
			BakeTerrain(); 

			// 3. Preparar Pipeline de Simulación
			var shaderSpirv = ComputeShaderFile.GetSpirV();
			_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
			_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

			// 4. Crear Buffers (SSBO)
			int bytes = Marshal.SizeOf<AgentDataSphere>() * AgentCount;
			byte[] initData = StructureToByteArray(_cpuAgents);
			_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);

			int stride = 1 + CELL_CAPACITY; 
			int gridBytes = GRID_SIZE * stride * 4; 
			byte[] gridData = new byte[gridBytes]; 
			_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes, gridData);

			// 5. Crear Texturas de Salida (Pos/Color)
			int texHeight = Mathf.CeilToInt((float)AgentCount / DATA_TEX_WIDTH);
			var fmt = new RDTextureFormat();
			fmt.Width = (uint)DATA_TEX_WIDTH;
			fmt.Height = (uint)texHeight;
			fmt.Depth = 1;
			fmt.Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat;
			fmt.UsageBits = RenderingDevice.TextureUsageBits.StorageBit | 
							RenderingDevice.TextureUsageBits.SamplingBit | 
							RenderingDevice.TextureUsageBits.CanUpdateBit | 
							RenderingDevice.TextureUsageBits.CanCopyFromBit;

			var view = new RDTextureView();
			_posTextureRid = _rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]>());
			_colorTextureRid = _rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]>());

			_posTextureRef = new Texture2Drd { TextureRdRid = _posTextureRid };
			_colorTextureRef = new Texture2Drd { TextureRdRid = _colorTextureRid };

			// 6. Crear Uniforms
			var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
			uAgent.AddId(_bufferRid);
			
			var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
			uGrid.AddId(_gridBufferRid);

			var uPosTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
			uPosTex.AddId(_posTextureRid);

			var uColTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 };
			uColTex.AddId(_colorTextureRid);

			// --- BINDING 4: EL CUBEMAP GENERADO ---
			var uHeightMap = new RDUniform { 
				UniformType = RenderingDevice.UniformType.SamplerWithTexture, 
				Binding = 4 
			};

			var samplerState = new RDSamplerState { 
				MagFilter = RenderingDevice.SamplerFilter.Linear, 
				MinFilter = RenderingDevice.SamplerFilter.Linear 
			};
			_samplerRid = _rd.SamplerCreate(samplerState);
			
			uHeightMap.AddId(_samplerRid);
			uHeightMap.AddId(_bakedCubemapRid); // Creado en BakeTerrain()

			// 7. CREAR SET FINAL (INCLUYENDO uHeightMap)
			// Corrección: Tu código anterior olvidaba 'uHeightMap' en esta lista
			_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { 
				uAgent, uGrid, uPosTex, uColTex, uHeightMap 
			}, _shaderRid, 0);
		}


	private void BakeTerrain()
		{
			// 1. Configurar Formato Cubemap
			var fmt = new RDTextureFormat();
			fmt.Width = (uint)CUBEMAP_SIZE;
			fmt.Height = (uint)CUBEMAP_SIZE;
			fmt.Depth = 1;
			fmt.ArrayLayers = 6; // 6 caras
			fmt.TextureType = RenderingDevice.TextureType.Cube;
			fmt.Format = RenderingDevice.DataFormat.R32Sfloat;
			fmt.UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit;

			// 2. Crear Textura
			_bakedCubemapRid = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());

			// 3. Pipeline del Baker
			var bakerSpirv = BakerShaderFile.GetSpirV();
			var bakerShaderRid = _rd.ShaderCreateFromSpirV(bakerSpirv);
			var bakerPipeline = _rd.ComputePipelineCreate(bakerShaderRid);

			// 4. Uniforms
			var uOutput = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
			uOutput.AddId(_bakedCubemapRid);
			var bakerUniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uOutput }, bakerShaderRid, 0);

			// 5. Push Constants
			var stream = new System.IO.MemoryStream();
			var writer = new System.IO.BinaryWriter(stream);
			writer.Write((float)PlanetRadius);
			writer.Write((float)NoiseScale);
			writer.Write((float)NoiseHeight);
			writer.Write((uint)CUBEMAP_SIZE);
			byte[] pushBytes = stream.ToArray();

			// 6. Ejecutar
			var computeList = _rd.ComputeListBegin();
			_rd.ComputeListBindComputePipeline(computeList, bakerPipeline);
			_rd.ComputeListBindUniformSet(computeList, bakerUniformSet, 0);
			_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
			
			uint groups = (uint)Mathf.CeilToInt(CUBEMAP_SIZE / 32.0f);
			_rd.ComputeListDispatch(computeList, groups, groups, 6); // Z=6 para las caras
			_rd.ComputeListEnd();
			
			// _rd.Submit();
			// _rd.Sync(); // Esperamos al baker

			// Limpieza local
			_rd.FreeRid(bakerPipeline);
			_rd.FreeRid(bakerShaderRid);
			_rd.FreeRid(bakerUniformSet);
			
			GD.Print("Terrain Baked en VRAM.");
		}



	private void SetupVisuals()
	{
		if (_visualizer == null)
		{
			_visualizer = GetNodeOrNull<MultiMeshInstance3D>("AgentVisualizer");
			if (_visualizer == null)
			{
				_visualizer = new MultiMeshInstance3D();
				_visualizer.Name = "AgentVisualizer";
				AddChild(_visualizer);
			}
		}

		_visualizer.Multimesh = new MultiMesh();
		_visualizer.Multimesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
		_visualizer.Multimesh.UseColors = false; // YA NO USAMOS COLORES DE INSTANCIA, LEEMOS TEXTURA
		_visualizer.Multimesh.InstanceCount = AgentCount;

		var quadMesh = new QuadMesh();
		quadMesh.Size = new Vector2(0.5f, 0.5f);

		string shaderPath = "res://Shaders/Visual/SphereImpostor.gdshader"; // RUTA ACTUALIZADA
		var shader = GD.Load<Shader>(shaderPath);

		if (shader == null)
		{
			GD.PrintErr($"CRÍTICO: No shader en {shaderPath}");
			return;
		}

		var material = new ShaderMaterial();
		material.Shader = shader;
		
		// --- AQUÍ OCURRE LA MAGIA ---
		// Pasamos las texturas de la GPU directamente al material
		material.SetShaderParameter("agent_pos_texture", _posTextureRef);
		material.SetShaderParameter("agent_color_texture", _colorTextureRef);
		material.SetShaderParameter("tex_width", DATA_TEX_WIDTH);
		material.SetShaderParameter("agent_radius_visual", 0.25f); // Mitad del quad size

		quadMesh.Material = material;
		_visualizer.Multimesh.Mesh = quadMesh;
		_visualizer.Multimesh.CustomAabb = new Aabb(new Vector3(-1000, -1000, -1000), new Vector3(2000, 2000, 2000));
	}

	public override void _Process(double delta)
	{
		if (_rd == null) return;

		// Fases Compute (Igual que antes)
		DispatchPhase(0, (float)delta, GRID_SIZE);
		DispatchPhase(1, (float)delta, AgentCount);
		DispatchPhase(2, (float)delta, AgentCount); // En esta fase ahora escribimos a la textura

		// _rd.Submit();
		
		// _rd.Sync(); //<--- ELIMINADO. No esperamos a la GPU.
		// BufferGetData <--- ELIMINADO. No leemos datos.
		// Bucle for <--- ELIMINADO. No actualizamos visuales en CPU.
		
		// Actualizar UI (Usamos valores ficticios o calculados en GPU más adelante)
		// Por ahora, solo mostramos FPS para ver el rendimiento puro
		_statsLabel.Text = $"AGENTS: {AgentCount}\nFPS: {Engine.GetFramesPerSecond()}";
	}

	private void DispatchPhase(uint phase, float delta, int threadCount)
	{
		var stream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(stream);
		
		writer.Write((float)delta);
		writer.Write((float)Time.GetTicksMsec() / 1000.0f);
		writer.Write((float)PlanetRadius);
		writer.Write((float)NoiseScale);
		writer.Write((float)NoiseHeight);
		writer.Write((uint)AgentCount);
		writer.Write((uint)phase);
		writer.Write((uint)GRID_SIZE);
		writer.Write((uint)DATA_TEX_WIDTH);             // Offset 32 (Termina en 36)
		
		// --- CORRECCIÓN: PADDING (Relleno) ---
		// El error pide 48 bytes. Tenemos 36. Faltan 12 bytes.
		// Escribimos 3 ceros (uint/float = 4 bytes c/u) extra.
		writer.Write((uint)0); // Padding 1 (+4 = 40)
		writer.Write((uint)0); // Padding 2 (+4 = 44)
		writer.Write((uint)0); // Padding 3 (+4 = 48) -> ¡BINGO!		
		byte[] pushBytes = stream.ToArray();
		
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);
		_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
		
		uint groups = (uint)Mathf.CeilToInt(threadCount / 64.0f);
		_rd.ComputeListDispatch(computeList, groups, 1, 1);
		_rd.ComputeListEnd();
	}

	private void SetupMarkers()
	{
		CreateMarker(Vector3.Down * (PlanetRadius + 5.0f), Colors.Cyan, "Goal_A_South");
		CreateMarker(Vector3.Up * (PlanetRadius + 5.0f), Colors.Orange, "Goal_B_North");
	}

	private void CreateMarker(Vector3 pos, Color color, string name)
	{
		var meshInstance = new MeshInstance3D();
		var boxMesh = new BoxMesh();
		boxMesh.Size = new Vector3(2, 10, 2); 
		var mat = new StandardMaterial3D();
		mat.AlbedoColor = color;
		mat.EmissionEnabled = true; mat.Emission = color; mat.EmissionEnergyMultiplier = 2.0f;
		boxMesh.Material = mat;
		meshInstance.Mesh = boxMesh;
		meshInstance.Name = name;
		AddChild(meshInstance);
		meshInstance.GlobalPosition = pos; // GlobalPosition es más seguro
		if (pos.LengthSquared() > 0.1f) meshInstance.LookAt(pos * 2.0f, Vector3.Right); 
	}

	private void SetupUI() {
		var canvas = new CanvasLayer(); AddChild(canvas);
		_statsLabel = new Label(); _statsLabel.Position = new Vector2(10, 10);
		var settings = new LabelSettings();
		settings.FontSize = 24; settings.OutlineSize = 4; settings.OutlineColor = Colors.Black; // Mejor visibilidad
		_statsLabel.LabelSettings = settings; canvas.AddChild(_statsLabel);
	}

	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int size = Marshal.SizeOf<AgentDataSphere>(); byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); } finally { handle.Free(); } return arr;
	}
}
