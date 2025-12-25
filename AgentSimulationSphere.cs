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
	[Export] public int AgentCount = 50000;
	[Export] public RDShaderFile ComputeShaderFile;
	
	// Se elimina 'AgentMesh' genérico, usamos QuadMesh generado en código
	
	// Parámetros del Planeta
	[Export] public float PlanetRadius = 50.0f;
	[Export] public float NoiseScale = 2.0f;
	[Export] public float NoiseHeight = 10.0f;
	
	// EXPORT para asignar en editor si se desea, sino se crea solo.
	[Export] private MultiMeshInstance3D _visualizer;

	private RenderingDevice _rd;
	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid, _gridBufferRid;
	private AgentDataSphere[] _cpuAgents;
	private Label _statsLabel;
	
	// Constantes Grilla
	private const int GRID_SIZE = 16384; 
	private const int CELL_CAPACITY = 16; 

	public override void _Ready()
	{
		SetupUI();
		SetupData();
		SetupCompute();
		SetupVisuals(); // Corrección aplicada aquí dentro
		SetupMarkers(); // Corrección aplicada aquí dentro
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
			Vector3 pole = isTeamA ? Vector3.Up : Vector3.Down;
			
			float angle = rng.Randf() * Mathf.Tau;
			float spread = rng.RandfRange(0.1f, 0.5f);
			
			Vector3 axis = Vector3.Right.Cross(pole).Normalized();
			if (axis.LengthSquared() < 0.01f) axis = Vector3.Forward;
			
			Vector3 offsetDir = pole.Rotated(Vector3.Right, spread).Rotated(pole, angle);
			Vector3 startPos = offsetDir.Normalized() * PlanetRadius;

			Vector3 myTarget = isTeamA ? targetA : targetB;
			Vector4 color = isTeamA ? new Vector4(0, 0.5f, 1, 1) : new Vector4(1, 0.2f, 0, 1);

			_cpuAgents[i] = new AgentDataSphere
			{
				Position = new Vector4(startPos.X, startPos.Y, startPos.Z, 0.5f),
				Target = new Vector4(myTarget.X, myTarget.Y, myTarget.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 8.0f),
				Color = color
			};
		}
	}

	private void SetupCompute()
	{
		if (ComputeShaderFile == null) { GD.PrintErr("Falta Shader"); return; }
		_rd = RenderingServer.CreateLocalRenderingDevice();

		var shaderSpirv = ComputeShaderFile.GetSpirV();
		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

		int bytes = Marshal.SizeOf<AgentDataSphere>() * AgentCount;
		byte[] initData = StructureToByteArray(_cpuAgents);
		_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);

		int stride = 1 + CELL_CAPACITY; 
		int gridBytes = GRID_SIZE * stride * 4; 
		byte[] gridData = new byte[gridBytes]; 
		_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes, gridData);

		var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		uAgent.AddId(_bufferRid);
		
		var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		uGrid.AddId(_gridBufferRid);

		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uAgent, uGrid }, _shaderRid, 0);
	}

	private void SetupVisuals()
	{
		// 1. Vincular el nodo (Si no lo asignaste en el inspector, lo busca o crea)
		if (_visualizer == null)
		{
			// Intenta buscarlo por nombre primero
			_visualizer = GetNodeOrNull<MultiMeshInstance3D>("AgentVisualizer");
			
			if (_visualizer == null)
			{
				_visualizer = new MultiMeshInstance3D();
				_visualizer.Name = "AgentVisualizer";
				AddChild(_visualizer);
				GD.Print("Aviso: _visualizer creado por código.");
			}
		}

		// 2. Reiniciar Multimesh (CRÍTICO para evitar bloqueos)
		_visualizer.Multimesh = new MultiMesh(); // Crear uno nuevo siempre asegura limpieza
		_visualizer.Multimesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
		_visualizer.Multimesh.UseColors = true; 
		_visualizer.Multimesh.InstanceCount = AgentCount; // Asignar count AHORA para reservar memoria

		// 3. Configurar Mesh y Material
		var quadMesh = new QuadMesh();
		quadMesh.Size = new Vector2(0.5f, 0.5f); // Tamaño del agente

		// Cargar Shader
		string shaderPath = "res://Shaders/SphereImpostor.gdshader";
		var shader = GD.Load<Shader>(shaderPath);

		if (shader == null)
		{
			GD.PrintErr($"CRÍTICO: No se pudo cargar el shader en {shaderPath}. Verifica la ruta.");
			// Material de error (Rojo brillante) para debug
			var errMat = new StandardMaterial3D();
			errMat.AlbedoColor = Colors.Red; 
			errMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
			quadMesh.Material = errMat;
		}
		else
		{
			var material = new ShaderMaterial();
			material.Shader = shader;
			quadMesh.Material = material;
		}

		_visualizer.Multimesh.Mesh = quadMesh;
		
		// AABB Manual (Evita que desaparezcan al mover la cámara)
		_visualizer.Multimesh.CustomAabb = new Aabb(new Vector3(-1000, -1000, -1000), new Vector3(2000, 2000, 2000));
	}



	public override void _Process(double delta)
	{
		// 1. Guard Clause para evitar crash si init falló
		if (_rd == null || !_pipelineRid.IsValid || !_uniformSetRid.IsValid || _visualizer == null) return;

		DispatchPhase(0, (float)delta, GRID_SIZE);
		DispatchPhase(1, (float)delta, AgentCount);
		DispatchPhase(2, (float)delta, AgentCount);

		_rd.Submit();
		_rd.Sync();

		byte[] outputBytes = _rd.BufferGetData(_bufferRid);
		_cpuAgents = ByteArrayToStructure<AgentDataSphere>(outputBytes, AgentCount);

		// Visualización
		int arrivedA = 0, arrivedB = 0, dead = 0;
		
		// Asignamos cantidad correcta antes de iterar
		if (_visualizer.Multimesh.InstanceCount != AgentCount) 
			_visualizer.Multimesh.InstanceCount = AgentCount;
		
		// Variable para ajustar cuánto flotan (Radio visual + pequeño margen)
		float visualOffset = 0.5f;

		for (int i = 0; i < AgentCount; i++)
		{
			var agent = _cpuAgents[i];
			float status = agent.Color.W;
			
			// Posición física original (Centro del agente)
			Vector3 physPos = new Vector3(agent.Position.X, agent.Position.Y, agent.Position.Z);
			
			// Calculamos el vector "Arriba" (Normal de la superficie)
			Vector3 up = physPos.Normalized();
			
			// --- CORRECCIÓN ---
			// Elevamos la posición visual sumando el radio en dirección de la normal
			Vector3 visualPos = physPos + (up * visualOffset);

			Transform3D t = Transform3D.Identity;
			t.Origin = visualPos; // Usamos la nueva posición corregida

			if (status < 0.5f) dead++;

			else if (status > 1.5f)
			{
				if (i < AgentCount/2) arrivedA++; else arrivedB++;
				t = t.Scaled(Vector3.Zero); 
			}
			else
			{
				// Billboard: Solo necesitamos posición y escala, el shader hace el billboard.
				// Pero mantenemos lógica de 'up' si quisieras orientación real.
				// Para impostores esféricos, la rotación no importa, solo la posición.
			}

			_visualizer.Multimesh.SetInstanceTransform(i, t);
			_visualizer.Multimesh.SetInstanceColor(i, new Color(agent.Color.X, agent.Color.Y, agent.Color.Z));
		}
		
		UpdateStats(arrivedA, arrivedB, dead);
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
		mat.EmissionEnabled = true;
		mat.Emission = color;
		mat.EmissionEnergyMultiplier = 2.0f;
		
		boxMesh.Material = mat;
		meshInstance.Mesh = boxMesh;
		meshInstance.Name = name;

		AddChild(meshInstance);

		meshInstance.Position = pos;
		if (pos.LengthSquared() > 0.1f)
		{
			meshInstance.LookAt(pos * 2.0f, Vector3.Right); 
		}

	}

	private void SetupUI() {
		var canvas = new CanvasLayer(); AddChild(canvas);
		_statsLabel = new Label(); _statsLabel.Position = new Vector2(10, 10);
		var settings = new LabelSettings();
		var sysFont = new SystemFont(); sysFont.FontNames = new[] { "Monospace" };
		settings.Font = sysFont; settings.FontSize = 12; settings.ShadowSize = 1; settings.ShadowColor = Colors.Black;
		_statsLabel.LabelSettings = settings; canvas.AddChild(_statsLabel);
	}
	
	private void UpdateStats(int a, int b, int d) {
		_statsLabel.Text = $"AGENTS: {AgentCount}\nDEAD: {d}\nA_ARRIVED: {a}\nB_ARRIVED: {b}";
	}

	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int size = Marshal.SizeOf<AgentDataSphere>(); byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); } finally { handle.Free(); } return arr;
	}

	private T[] ByteArrayToStructure<T>(byte[] bytes, int count) where T : struct 
	{
		T[] data = new T[count]; 
		int size = Marshal.SizeOf<T>();
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length); } finally { handle.Free(); } return data;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationPredelete && _rd != null)
		{
			if (_uniformSetRid.IsValid) _rd.FreeRid(_uniformSetRid);
			if (_bufferRid.IsValid) _rd.FreeRid(_bufferRid);
			if (_gridBufferRid.IsValid) _rd.FreeRid(_gridBufferRid);
			if (_pipelineRid.IsValid) _rd.FreeRid(_pipelineRid);
			if (_shaderRid.IsValid) _rd.FreeRid(_shaderRid);
			_rd.Free();
		}
	}
}
