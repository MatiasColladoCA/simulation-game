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
	[Export] public int AgentCount = 1000;
	[Export] public RDShaderFile ComputeShaderFile;
	[Export] public Mesh AgentMesh;
	
	// Parámetros del Planeta (Deben coincidir con PlanetManager)
	[Export] public float PlanetRadius = 50.0f;
	[Export] public float NoiseScale = 2.0f;
	[Export] public float NoiseHeight = 10.0f;

	private RenderingDevice _rd;
	private Rid _shaderRid, _pipelineRid, _bufferRid, _uniformSetRid;
	private MultiMeshInstance3D _visualizer;
	private AgentDataSphere[] _cpuAgents;
	private Label _statsLabel;

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

		// Objetivos: Polos Opuestos
		// Nota: Multiplicamos por Radius para que el target esté en la superficie aprox
		// (El shader luego lo ignora y proyecta, pero ayuda a la visualización inicial)
		Vector3 targetA = Vector3.Down * PlanetRadius; // Polo Sur
		Vector3 targetB = Vector3.Up * PlanetRadius;   // Polo Norte

		for (int i = 0; i < AgentCount; i++)
		{
			bool isTeamA = i < (AgentCount / 2);
			
			// SPAWN ESFÉRICO: Anillos alrededor de los polos
			// Evitamos el punto exacto (0,1,0) para no superponer todos en un pixel
			Vector3 pole = isTeamA ? Vector3.Up : Vector3.Down;
			
			// Generar un punto aleatorio en un casquete esférico alrededor del polo
			float angle = rng.Randf() * Mathf.Tau; // 0 a 360 grados
			float spread = rng.RandfRange(0.1f, 0.5f); // Qué tan lejos del polo spawnean
			
			// Rotamos el vector del polo un poco hacia un lado aleatorio
			Vector3 axis = Vector3.Right.Cross(pole).Normalized(); // Eje arbitrario
			if (axis.LengthSquared() < 0.01f) axis = Vector3.Forward; // Protección si pole es Right
			
			Vector3 offsetDir = pole.Rotated(Vector3.Right, spread).Rotated(pole, angle);
			Vector3 startPos = offsetDir.Normalized() * PlanetRadius;

			Vector3 myTarget = isTeamA ? targetA : targetB;
			Vector4 color = isTeamA ? new Vector4(0, 0.5f, 1, 1) : new Vector4(1, 0.2f, 0, 1);

			_cpuAgents[i] = new AgentDataSphere
			{
				Position = new Vector4(startPos.X, startPos.Y, startPos.Z, 0.5f),
				Target = new Vector4(myTarget.X, myTarget.Y, myTarget.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 8.0f), // Velocidad
				Color = color
			};
		}
	}

	// Añadir variable para el buffer de la grilla
	private Rid _gridBufferRid;
	// Tamaño de la tabla hash (Debe ser potencia de 2 y mayor que AgentCount para evitar colisiones de hash)
	// Para 1000-5000 agentes, 16384 (2^14) va sobrado.
	private const int GRID_SIZE = 16384; 
	private const int CELL_CAPACITY = 16; // Máximo de agentes por celda 3D

	private void SetupCompute()
	{
		if (ComputeShaderFile == null) { GD.PrintErr("Falta Shader"); return; }
		_rd = RenderingServer.CreateLocalRenderingDevice();

		var shaderSpirv = ComputeShaderFile.GetSpirV();
		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

		// 1. Buffer Agentes (Igual que antes)
		int bytes = Marshal.SizeOf<AgentDataSphere>() * AgentCount;
		byte[] initData = StructureToByteArray(_cpuAgents);
		_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);

		// 2. Buffer Grilla 3D (NUEVO)
		// Estructura: [Contador, ID_1, ID_2 ... ID_N] por celda
		int stride = 1 + CELL_CAPACITY; // 1 uint count + N uints ids
		int gridBytes = GRID_SIZE * stride * 4; // 4 bytes por uint
		byte[] gridData = new byte[gridBytes]; // Inicia en 0
		_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes, gridData);

		// 3. Uniform Set (Binding 0: Agentes, Binding 1: Grilla)
		var uAgent = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		uAgent.AddId(_bufferRid);
		
		var uGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		uGrid.AddId(_gridBufferRid);

		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uAgent, uGrid }, _shaderRid, 0);
	}

	private void SetupVisuals()
	{
		var multiMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = true,
			Mesh = AgentMesh,
			InstanceCount = AgentCount
		};
		_visualizer = new MultiMeshInstance3D { Multimesh = multiMesh };
		// AABB Gigante para cubrir el planeta entero
		_visualizer.CustomAabb = new Aabb(new Vector3(-100, -100, -100), new Vector3(200, 200, 200));
		AddChild(_visualizer);
	}


	public override void _Process(double delta)
	{
		// 1. Guard Clause
		if (_rd == null || !_pipelineRid.IsValid || !_uniformSetRid.IsValid) return;

		// --- ORQUESTACIÓN DEL COMPUTE SHADER (3 FASES) ---

		// FASE 0: CLEAR (Limpiar la grilla)
		// Ejecutamos tantos hilos como celdas hay en la grilla
		DispatchPhase(0, (float)delta, GRID_SIZE);
		
		// Barrera: Esperar a que la limpieza termine antes de escribir
		_rd.Barrier(RenderingDevice.BarrierMask.Compute);

		// FASE 1: POPULATE (Llenar la grilla)
		// Ejecutamos tantos hilos como agentes
		DispatchPhase(1, (float)delta, AgentCount);
		
		// Barrera: Esperar a que todos se registren antes de leer colisiones
		_rd.Barrier(RenderingDevice.BarrierMask.Compute);

		// FASE 2: UPDATE (Física y Colisiones)
		DispatchPhase(2, (float)delta, AgentCount);

		// --- DESCARGA DE DATOS ---
		_rd.Submit();
		_rd.Sync();

		byte[] outputBytes = _rd.BufferGetData(_bufferRid);
		_cpuAgents = ByteArrayToStructure<AgentDataSphere>(outputBytes, AgentCount);

		// --- VISUALIZACIÓN Y STATS (Tu lógica original intacta) ---
		int arrivedA = 0, arrivedB = 0, dead = 0;

		for (int i = 0; i < AgentCount; i++)
		{
			var agent = _cpuAgents[i];
			float status = agent.Color.W;
			Vector3 pos = new Vector3(agent.Position.X, agent.Position.Y, agent.Position.Z);
			
			Transform3D t = Transform3D.Identity;
			t.Origin = pos;

			if (status < 0.5f) // Muerto
			{
				dead++;
			}
			else if (status > 1.5f) // Salvado
			{
				if (i < AgentCount/2) arrivedA++; else arrivedB++;
				t = t.Scaled(Vector3.Zero); 
			}
			else // Vivo
			{
				Vector3 up = pos.Normalized();
				Vector3 vel = new Vector3(agent.Velocity.X, agent.Velocity.Y, agent.Velocity.Z);
				Vector3 forward = vel.LengthSquared() > 0.1f ? vel.Normalized() : Vector3.Forward;
				
				if (Mathf.Abs(up.Dot(forward)) < 0.99f)
				{
					t = t.LookingAt(pos + forward, up);
				}
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
		
		// EL ORDEN DEBE COINCIDIR EXACTAMENTE CON EL STRUCT 'Params' EN GLSL
		writer.Write((float)delta);                     // Offset 0
		writer.Write((float)Time.GetTicksMsec() / 1000.0f); // Offset 4
		writer.Write((float)PlanetRadius);              // Offset 8
		writer.Write((float)NoiseScale);                // Offset 12
		writer.Write((float)NoiseHeight);               // Offset 16
		writer.Write((uint)AgentCount);                 // Offset 20
		writer.Write((uint)phase);                      // Offset 24 (La Fase actual)
		writer.Write((uint)GRID_SIZE);                  // Offset 28 (Tamaño Grilla)
		
		// Total: 32 bytes exactos.
		
		byte[] pushBytes = stream.ToArray();
		
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);
		_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
		
		// Calculamos grupos de trabajo (Threads / 64)
		uint groups = (uint)Mathf.CeilToInt(threadCount / 64.0f);
		_rd.ComputeListDispatch(computeList, groups, 1, 1);
		_rd.ComputeListEnd();
	}


	private void SetupMarkers()
	{
		// Creamos marcadores visuales en los polos (donde están los Targets)
		// Target A (Sur) y Target B (Norte)
		// Elevamos el marcador (PlanetRadius + 10) para que flote visiblemente
		CreateMarker(Vector3.Down * (PlanetRadius + 5.0f), Colors.Cyan, "Goal_A_South");
		CreateMarker(Vector3.Up * (PlanetRadius + 5.0f), Colors.Orange, "Goal_B_North");
	}

	private void CreateMarker(Vector3 pos, Color color, string name)
	{
		var meshInstance = new MeshInstance3D();
		var boxMesh = new BoxMesh();
		boxMesh.Size = new Vector3(2, 10, 2); // Un pilar alto
		
		var mat = new StandardMaterial3D();
		mat.AlbedoColor = color;
		mat.EmissionEnabled = true; // Que brille un poco
		mat.Emission = color;
		mat.EmissionEnergyMultiplier = 2.0f;
		
		boxMesh.Material = mat;

		meshInstance.Mesh = boxMesh;
		meshInstance.Name = name;

		// 1. Primero añadimos al árbol
		AddChild(meshInstance);
		
		// IMPORTANTE: Orientar el cubo para que salga radialmente del planeta
		meshInstance.Position = pos;
		if (pos.LengthSquared() > 0.1f)
		{
			// Mirar hacia afuera desde el centro (0,0,0)
			meshInstance.LookAt(pos * 2.0f, Vector3.Right); 
		}

		AddChild(meshInstance);
	}

	// --- UI Setup (Tu estilo Bitmap) ---
	private void SetupUI() {
		var canvas = new CanvasLayer(); AddChild(canvas);
		_statsLabel = new Label(); _statsLabel.Position = new Vector2(10, 10);
		var settings = new LabelSettings();
		var sysFont = new SystemFont(); sysFont.FontNames = new[] { "Monospace" };
		sysFont.Antialiasing = TextServer.FontAntialiasing.None;
		sysFont.SubpixelPositioning = TextServer.SubpixelPositioning.Disabled;
		settings.Font = sysFont; settings.FontSize = 12; settings.ShadowSize = 1; settings.ShadowColor = Colors.Black;
		_statsLabel.LabelSettings = settings; canvas.AddChild(_statsLabel);
	}
	
	private void UpdateStats(int a, int b, int d) {
		_statsLabel.Text = $"AGENTS: {AgentCount}\nDEAD: {d}\nARRIVED A: {a}\nARRIVED B: {b}";
	}

	// Marshaling Helpers (Igual que antes)
	private byte[] StructureToByteArray(AgentDataSphere[] data) {
		int size = Marshal.SizeOf<AgentDataSphere>(); byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); } finally { handle.Free(); } return arr;
	}
	// CORRECCIÓN: Cambiar el tipo de retorno de 'AgentDataSphere[]' a 'T[]'
	private T[] ByteArrayToStructure<T>(byte[] bytes, int count) where T : struct 
	{
		T[] data = new T[count]; 
		int size = Marshal.SizeOf<T>();
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try 
		{ 
			Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length); 
		} 
		finally 
		{ 
			handle.Free(); 
		} 
		return data;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationPredelete && _rd != null)
		{
			_rd.FreeRid(_uniformSetRid);
			// _rd.FreeRid(_agentBufferRid);
			if (_gridBufferRid.IsValid) _rd.FreeRid(_gridBufferRid);
			_rd.FreeRid(_gridBufferRid); // Liberar Grid
			_rd.FreeRid(_pipelineRid);
			_rd.FreeRid(_shaderRid);
			_rd.Free();
		}
	}
}
