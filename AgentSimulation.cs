using Godot;
using System;
using System.Runtime.InteropServices;

// --- ESTRUCTURA ---
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentData
{
	public Vector4 Position;
	public Vector4 Target;
	public Vector4 Velocity;
	public Vector4 Color;
}

public partial class AgentSimulation : Node3D
{
	[Export] public int AgentCount = 2000; // Subimos a 2000 para probar rendimiento
	[Export] public RDShaderFile ComputeShaderFile;
	[Export] public Mesh AgentMesh;

	private RenderingDevice _rd;
	private Rid _shaderRid;
	private Rid _pipelineRid;
	
	private Rid _agentBufferRid;
	private Rid _gridBufferRid; // NUEVO
	private Rid _uniformSetRid;

	private MultiMeshInstance3D _visualizer;
	private AgentData[] _cpuAgents;

	// Configuración Grilla
	private float _mapDepth = 100.0f;
	private float _cellSize = 2.0f; // Tamaño de celda
	private int _gridDim;           // Celdas por lado

	private Label _statsLabel; // Referencia al texto en pantalla

	public override void _Ready()
	{
		if (ComputeShaderFile == null || AgentMesh == null) return;

		// Calcular dimensión de grilla (ej. 100 / 2 = 50 celdas de lado)
		_gridDim = Mathf.CeilToInt(_mapDepth / _cellSize);

		SetupData();
		SetupCompute();
		SetupVisuals();
		SetupMarkers();

		SetupUI();
	}

	private void SetupUI()
	{
		// CanvasLayer asegura que el texto flote sobre el juego 3D
		var canvas = new CanvasLayer();
		AddChild(canvas);

		_statsLabel = new Label();
		_statsLabel.Position = new Vector2(10, 10);
		_statsLabel.Modulate = Colors.White;

		// Configuración para que se vea bien con fondo oscuro
		var settings = new LabelSettings();
		
		// 1. Configuración "Pixel Art" por código
		var sysFont = new SystemFont();
		sysFont.FontNames = new string[] { "Monospace", "Consolas", "Courier New" };
		sysFont.Antialiasing = TextServer.FontAntialiasing.None; // Clave: Bordes duros, sin suavizado
		sysFont.SubpixelPositioning = TextServer.SubpixelPositioning.Disabled;
		
		settings.Font = sysFont;
		settings.FontSize = 12; // Tamaño reducido (antes 24)
		
		// 2. Estilo visual limpio (sombra dura en vez de borde grueso)
		settings.OutlineSize = 0; 
		settings.ShadowSize = 1;
		settings.ShadowColor = Colors.Black;
		settings.ShadowOffset = new Vector2(1, 1);
		
		// Espaciado de línea más compacto
		settings.LineSpacing = -2;

		_statsLabel.LabelSettings = settings;
		canvas.AddChild(_statsLabel);
	}

	private void SetupMarkers()
	{
		float centerZ = _mapDepth / 2.0f;
		float centerX = 50.0f; // Asumiendo ancho X=100 aprox o fijo 50
		CreateMarkerBox(new Vector3(0, 0, centerZ), Colors.Blue, "Start_A");
		CreateMarkerBox(new Vector3(50, 0, centerZ), Colors.Cyan, "Target_A");
		CreateMarkerBox(new Vector3(50, 0, centerZ), Colors.Red, "Start_B");
		CreateMarkerBox(new Vector3(0, 0, centerZ), Colors.Orange, "Target_B");
	}


	private void SetupData()
	{
		_cpuAgents = new AgentData[AgentCount];
		Vector3 targetA = new Vector3(50, 0, _mapDepth / 2.0f);
		Vector3 targetB = new Vector3(0, 0, _mapDepth / 2.0f);

		// Configuración de la formación
		float spacing = 1.5f; // 1.5m de separación (Radio 0.5 + 0.5 + 0.5 aire)
		int agentsPerRow = 50; // Cuántos agentes por fila antes de empezar una nueva fila detrás

		for (int i = 0; i < AgentCount; i++)
		{
			int teamSize = AgentCount / 2;
			bool isTeamA = i < teamSize;
			int idxInTeam = i % teamSize;

			// --- LÓGICA DE GRILLA ---
			// Calculamos fila y columna
			int row = idxInTeam / agentsPerRow; // Profundidad (Eje X)
			int col = idxInTeam % agentsPerRow; // Anchura (Eje Z)

			// Offset en Z para centrarlos en el mapa
			float formationWidth = agentsPerRow * spacing;
			float zStartOffset = (_mapDepth - formationWidth) / 2.0f;
			float zPos = zStartOffset + (col * spacing);

			// Offset en X (Profundidad): Los acumulamos hacia ATRÁS de su línea de salida
			// Team A (X=0) se forma hacia X negativos (-1.5, -3.0...)
			// Team B (X=50) se forma hacia X positivos (51.5, 53.0...)
			float xPos;
			if (isTeamA)
				xPos = 0.0f - (row * spacing); 
			else
				xPos = 50.0f + (row * spacing);

			Vector3 start = new Vector3(xPos, 0, zPos);
			Vector3 target = isTeamA ? targetA : targetB;
			Vector4 color = isTeamA ? new Vector4(0, 0.5f, 1, 1) : new Vector4(1, 0.2f, 0, 1);

			_cpuAgents[i] = new AgentData
			{
				Position = new Vector4(start.X, start.Y, start.Z, 0.5f),
				Target = new Vector4(target.X, target.Y, target.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 10.0f), // Velocidad rápida
				Color = color
			};
		}
	}




	private void SetupCompute()
	{
		_rd = RenderingServer.CreateLocalRenderingDevice();

		var shaderSpirv = ComputeShaderFile.GetSpirV();
		_shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		_pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

		// 1. Buffer de Agentes
		int agentBytes = Marshal.SizeOf<AgentData>() * AgentCount;
		byte[] agentData = StructureToByteArray(_cpuAgents);
		_agentBufferRid = _rd.StorageBufferCreate((uint)agentBytes, agentData);

		// 2. Buffer de Grilla (NUEVO)
		// Tamaño = (GridDim * GridDim) * (1 uint count + 32 uints ids) * 4 bytes
		int cellsTotal = _gridDim * _gridDim;
		int strideInts = 1 + 32; // 33 ints por celda
		int gridBytes = cellsTotal * strideInts * 4;
		// Inicializamos con ceros
		byte[] gridInitData = new byte[gridBytes]; 
		_gridBufferRid = _rd.StorageBufferCreate((uint)gridBytes, gridInitData);

		// 3. Uniform Set (Binding 0: Agentes, Binding 1: Grilla)
		var uniformAgents = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		uniformAgents.AddId(_agentBufferRid);
		
		var uniformGrid = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		uniformGrid.AddId(_gridBufferRid);

		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uniformAgents, uniformGrid }, _shaderRid, 0);
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
		// AABB Manual crítico
		_visualizer.CustomAabb = new Aabb(new Vector3(-10, -10, -10), new Vector3(120, 50, 120));
		AddChild(_visualizer);
	}

	public override void _Process(double delta)
	{
		if (_rd == null) return;

		// struct Params { delta, time, phase, map_size, cell_size, grid_dim }
		// float, float, uint, float, float, uint = 6 valores de 4 bytes = 24 bytes
		// Alineación std430 en push constant puede ser delicada, usamos floats para todo lo posible o pack manual.
		// En GLSL definimos: float, float, uint, float, float, uint. 
		// Para seguridad en C#, usaremos un array de bytes crudo.
		
		// Fase 0: Clear Grid
		DispatchPhase(0, (float)delta, _gridDim * _gridDim); // Hilos = Cantidad de celdas
		
		// Barrier: Asegurar que Clear termine antes de Populate
		_rd.Barrier(RenderingDevice.BarrierMask.Compute);

		// Fase 1: Populate Grid
		DispatchPhase(1, (float)delta, AgentCount); // Hilos = Cantidad de agentes
		
		// Barrier: Asegurar que Populate termine antes de Update
		_rd.Barrier(RenderingDevice.BarrierMask.Compute);

		// Fase 2: Update Simulation
		DispatchPhase(2, (float)delta, AgentCount); // Hilos = Cantidad de agentes

		// Descarga y Render
		_rd.Submit();
		_rd.Sync();

		byte[] outputBytes = _rd.BufferGetData(_agentBufferRid);
		_cpuAgents = ByteArrayToStructure<AgentData>(outputBytes, AgentCount);

		// --- VARIABLES PARA ESTADÍSTICAS ---
		int arrivedA = 0;
		int arrivedB = 0;
		int deadCount = 0;
		
		// Definimos el umbral de llegada (Radio del objetivo)
		float arrivalRadius = 5.0f; 

		// Posiciones de objetivos (Deben coincidir con SetupData)
		Vector3 targetPosA = new Vector3(50, 0, _mapDepth / 2.0f);
		Vector3 targetPosB = new Vector3(0, 0, _mapDepth / 2.0f);


		// 4. Actualizar Visualización
			// ... inicio del bucle for ...
		for (int i = 0; i < AgentCount; i++)
		{
			var agent = _cpuAgents[i];
			
			// Interpretamos el estado desde W
			float status = agent.Color.W;
			bool isDead = status < 0.5f;       // Cerca de 0.0
			bool isArrived = status > 1.5f;    // Cerca de 2.0 (NUEVO)
			
			Vector3 pos = new Vector3(agent.Position.X, agent.Position.Y, agent.Position.Z);
			Vector3 vel = new Vector3(agent.Velocity.X, agent.Velocity.Y, agent.Velocity.Z);

			// --- LÓGICA DE CONTEO Y VISUALIZACIÓN ---
			Transform3D t = Transform3D.Identity;
			t.Origin = pos;

			if (isDead)
			{
				deadCount++;
				// Los muertos se quedan visibles como "manchas" o los ocultamos si prefieres
				// t = t.Scaled(Vector3.Zero); // Descomentar para ocultar cadáveres también
			}
			else if (isArrived)
			{
				// Contabilizar por equipo
				if (i < AgentCount / 2) arrivedA++;
				else arrivedB++;

				// --- MAGIA VISUAL: DESAPARECER ---
				// Escalamos a 0.0 para que el MultiMesh no lo dibuje.
				// Sigue existiendo en memoria, pero es invisible.
				t = t.Scaled(Vector3.Zero); 
			}
			else 
			{
				// ESTÁ VIVO Y CORRIENDO
				if (vel.LengthSquared() > 0.1f) 
					t = t.LookingAt(pos + vel, Vector3.Up);
			}

			_visualizer.Multimesh.SetInstanceTransform(i, t);
			_visualizer.Multimesh.SetInstanceColor(i, new Color(agent.Color.X, agent.Color.Y, agent.Color.Z));
		}

		// --- ACTUALIZAR UI ---
		UpdateStatsLabel(arrivedA, arrivedB, deadCount);
	}

	// Método helper para formatear el texto
	private void UpdateStatsLabel(int arrivedA, int arrivedB, int dead)
	{
		int totalAlive = AgentCount - dead;
		double fps = Engine.GetFramesPerSecond();
		
		_statsLabel.Text = $"""
			FPS: {fps:F0}
			----------------
			Agentes Totales: {AgentCount}
			Muertos (Aplastados): {dead}
			Vivos: {totalAlive}
			----------------
			Llegadas Equipo A (Azul): {arrivedA}
			Llegadas Equipo B (Rojo): {arrivedB}
			----------------
			Tasa de Éxito: {(double)(arrivedA + arrivedB) / AgentCount * 100:F1}%
			""";
	}

	private void DispatchPhase(uint phase, float delta, int threadCount)
	{
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);

		// --- CORRECCIÓN DE ALINEACIÓN ---
		// Total enviado: 32 bytes (8 floats/uints x 4 bytes)
		// Esto asegura compatibilidad con cualquier GPU (Nvidia/AMD/Intel)
		var stream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(stream);
		
		writer.Write((float)delta);                     // Offset 0
		writer.Write((float)Time.GetTicksMsec() / 1000.0f); // Offset 4
		writer.Write((uint)phase);                      // Offset 8
		writer.Write((float)100.0f);                    // Offset 12 (MapSize)
		writer.Write((float)_cellSize);                 // Offset 16
		writer.Write((uint)_gridDim);                   // Offset 20
		
		// Rellenamos con 2 ceros extra para llegar a 32 bytes (múltiplo de 16)
		writer.Write((float)0.0f);                      // Offset 24 (Padding)
		writer.Write((float)0.0f);                      // Offset 28 (Padding)

		byte[] pushBytes = stream.ToArray();
		_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);

		uint groups = (uint)Mathf.CeilToInt(threadCount / 64.0f);
		_rd.ComputeListDispatch(computeList, groups, 1, 1);
		_rd.ComputeListEnd();
	}

	// Helpers previos (CreateMarkerBox, StructureToByteArray, etc) se mantienen igual...
	private void CreateMarkerBox(Vector3 position, Color color, string name)
	{
		var boxMesh = new BoxMesh();
		boxMesh.Size = new Vector3(2.0f, 10.0f, 2.0f); 
		var material = new StandardMaterial3D();
		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.AlbedoColor = new Color(color.R, color.G, color.B, 0.5f);
		boxMesh.Material = material;
		var meshInstance = new MeshInstance3D { Mesh = boxMesh, Position = position, Name = name };
		AddChild(meshInstance);
	}

	private byte[] StructureToByteArray(AgentData[] data) {
		int size = Marshal.SizeOf<AgentData>();
		byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); }
		finally { handle.Free(); }
		return arr;
	}

	private AgentData[] ByteArrayToStructure<T>(byte[] bytes, int count) where T : struct {
		AgentData[] data = new AgentData[count];
		int size = Marshal.SizeOf<T>();
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length); }
		finally { handle.Free(); }
		return data;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationPredelete && _rd != null)
		{
			_rd.FreeRid(_uniformSetRid);
			_rd.FreeRid(_agentBufferRid);
			_rd.FreeRid(_gridBufferRid); // Liberar Grid
			_rd.FreeRid(_pipelineRid);
			_rd.FreeRid(_shaderRid);
			_rd.Free();
		}
	}
}
