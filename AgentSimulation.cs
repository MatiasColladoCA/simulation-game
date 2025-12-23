using Godot;
using System;
using System.Runtime.InteropServices;

// --- PARTE 1: La Estructura de Datos (El molde) ---
// Debe estar alineada a 16 bytes para coincidir con el GLSL (std430)
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct AgentData
{
	public Vector4 Position; // x,y,z, padding
	public Vector4 Target;   // x,y,z, padding
	public Vector4 Velocity; // x,y,z, speed
	public Vector4 Color;    // r,g,b,a
}

// --- PARTE 2: La Clase Principal (El orquestador) ---
public partial class AgentSimulation : Node3D
{
	[Export] public int AgentCount = 20; 
	[Export] public RDShaderFile ComputeShaderFile; 
	[Export] public Mesh AgentMesh; 

	private RenderingDevice _rd;
	private Rid _shaderRid;
	private Rid _pipelineRid;
	private Rid _bufferRid;
	private Rid _uniformSetRid;

	private MultiMeshInstance3D _visualizer;
	private AgentData[] _cpuAgents;

	public override void _Ready()
	{
		if (ComputeShaderFile == null || AgentMesh == null)
		{
			GD.PrintErr("ERROR: Asigna el Shader y el Mesh en el Inspector.");
			return;
		}

		SetupData();
		SetupCompute();
		SetupVisuals();

		// --- NUEVO: Crear marcadores de Inicio/Fin ---
		float centerZ = AgentCount; // Punto medio aproximado en Z (si son 20 agentes, Z~20)
		
		// Equipo Azul (A): Empieza en X=0, va a X=50
		CreateMarkerBox(new Vector3(0, 0, centerZ), Colors.Blue, "Start_A_Blue");
		CreateMarkerBox(new Vector3(50, 0, centerZ), Colors.Cyan, "Target_A_Cyan");

		// Equipo Rojo (B): Empieza en X=50, va a X=0
		CreateMarkerBox(new Vector3(50, 0, centerZ), Colors.Red, "Start_B_Red");
		CreateMarkerBox(new Vector3(0, 0, centerZ), Colors.Orange, "Target_B_Orange");
		// -------------------------------------------

	}


	private void SetupData()
	{
		_cpuAgents = new AgentData[AgentCount];
		
		// Inicialización de datos
		for (int i = 0; i < AgentCount; i++)
		{
			bool isTeamA = i < (AgentCount / 2); // Mitad azules, mitad rojos
			
			// A va de (0,0,Z) a (50,0,Z). B va de (50,0,Z) a (0,0,Z)
			float zPos = i * 2.0f;
			
			Vector3 start = isTeamA ? new Vector3(0, 0, zPos) : new Vector3(50, 0, zPos);
			Vector3 target = isTeamA ? new Vector3(50, 0, zPos) : new Vector3(0, 0, zPos);
			Vector4 color = isTeamA ? new Vector4(0, 0, 1, 1) : new Vector4(1, 0, 0, 1);

			_cpuAgents[i] = new AgentData
			{
				Position = new Vector4(start.X, start.Y, start.Z, 1.0f),
				Target = new Vector4(target.X, target.Y, target.Z, 0.0f),
				Velocity = new Vector4(0, 0, 0, 10.0f), // Velocidad base 10
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

		int bytes = Marshal.SizeOf<AgentData>() * AgentCount;
		byte[] initData = StructureToByteArray(_cpuAgents);
		_bufferRid = _rd.StorageBufferCreate((uint)bytes, initData);

		var uniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 0
		};
		uniform.AddId(_bufferRid);
		_uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uniform }, _shaderRid, 0);
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
		AddChild(_visualizer);
	}

	public override void _Process(double delta)
	{
		if (_rd == null) return;

		// 1. Enviar Delta Time
		float[] pushConstants = { (float)delta, 2.0f, 0.0f, 0.0f };
		byte[] pushBytes = new byte[pushConstants.Length * 4];
		Buffer.BlockCopy(pushConstants, 0, pushBytes, 0, pushBytes.Length);

		// 2. Ejecutar Compute Shader
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);
		_rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
		
		uint groups = (uint)Mathf.CeilToInt(AgentCount / 64.0f);
		_rd.ComputeListDispatch(computeList, groups, 1, 1);
		_rd.ComputeListEnd();

		// 3. Esperar GPU y Leer Datos
		_rd.Submit();
		_rd.Sync(); 

		byte[] outputBytes = _rd.BufferGetData(_bufferRid);
		_cpuAgents = ByteArrayToStructure<AgentData>(outputBytes, AgentCount);

		// 4. Actualizar Visualización
		for (int i = 0; i < AgentCount; i++)
		{
			var agent = _cpuAgents[i];
			
			Transform3D t = Transform3D.Identity;
			t.Origin = new Vector3(agent.Position.X, agent.Position.Y, agent.Position.Z);
			
			_visualizer.Multimesh.SetInstanceTransform(i, t);
			_visualizer.Multimesh.SetInstanceColor(i, new Color(agent.Color.X, agent.Color.Y, agent.Color.Z));
		}
	}

	// --- Helpers Técnicos ---
	
	// Helper para crear marcadores visuales simples
	private void CreateMarkerBox(Vector3 position, Color color, string name)
	{
		// 1. Crear la malla (Cubo) y su material
		var boxMesh = new BoxMesh();
		// Hacemos los marcadores un poco más grandes (escala 2,5,2) y altos
		boxMesh.Size = new Vector3(2.0f, 5.0f, 2.0f); 

		var material = new StandardMaterial3D();
		material.AlbedoColor = color;
		// Hacerlo semitransparente para que se vea "fantasmal" (opcional)
		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.AlbedoColor = new Color(color.R, color.G, color.B, 0.5f);

		boxMesh.Material = material;

		// 2. Crear la instancia y añadirla a la escena
		var meshInstance = new MeshInstance3D
		{
			Mesh = boxMesh,
			Position = new Vector3(position.X, position.Y + 2.5f, position.Z), // Elevamos un poco Y
			Name = name
		};
		AddChild(meshInstance);
	}

	private byte[] StructureToByteArray(AgentData[] data)
	{
		int size = Marshal.SizeOf<AgentData>();
		byte[] arr = new byte[size * data.Length];
		GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try { Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length); }
		finally { handle.Free(); }
		return arr;
	}

	private AgentData[] ByteArrayToStructure<T>(byte[] bytes, int count) where T : struct
	{
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
			_rd.FreeRid(_bufferRid);
			_rd.FreeRid(_pipelineRid);
			_rd.FreeRid(_shaderRid);
			_rd.Free();
		}
	}
}
