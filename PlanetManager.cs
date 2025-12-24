using Godot;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public partial class PlanetManager : Node3D
{
	[Export] public float Radius = 50.0f;
	[Export] public int Resolution = 64; // Vertices por lado de cara
	[Export] public RDShaderFile ComputeShader;
	[Export] public Material PlanetMaterial;

	private List<MeshInstance3D> _faces = new();
	private RenderingDevice _rd;

	public override void _Ready()
	{
		_rd = RenderingServer.CreateLocalRenderingDevice();
		GeneratePlanet();
	}

	private void GeneratePlanet()
	{
		// 6 Direcciones base del cubo
		Vector3[] directions = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back };
		
		foreach (var dir in directions)
		{
			CreateFace(dir);
		}
	}

	private void CreateFace(Vector3 localUp)
	{
		// 1. Construir vértices base (Cubo plano)
		Vector3 axisA = new Vector3(localUp.Y, localUp.Z, localUp.X);
		Vector3 axisB = localUp.Cross(axisA);
		
		Vector3[] vertices = new Vector3[Resolution * Resolution];
		Vector3[] normals = new Vector3[Resolution * Resolution]; // Placeholder
		int[] indices = new int[(Resolution - 1) * (Resolution - 1) * 6];
		
		// Generar Geometría Plana y Normalizar a Esfera
		int triIndex = 0;
		
		// Buffer para enviar al Compute Shader (Direcciones)
		float[] queryData = new float[vertices.Length * 4]; 

		for (int y = 0; y < Resolution; y++)
		{
			for (int x = 0; x < Resolution; x++)
			{
				int i = x + y * Resolution;
				Vector2 percent = new Vector2(x, y) / (Resolution - 1);
				Vector3 pointOnCube = localUp + (percent.X - 0.5f) * 2 * axisA + (percent.Y - 0.5f) * 2 * axisB;
				Vector3 pointOnSphere = pointOnCube.Normalized(); // CubeSphere Mapping
				
				vertices[i] = pointOnSphere; // Guardamos dirección temporalmente
				
				// Preparar datos para GPU
				queryData[i * 4 + 0] = pointOnSphere.X;
				queryData[i * 4 + 1] = pointOnSphere.Y;
				queryData[i * 4 + 2] = pointOnSphere.Z;
				queryData[i * 4 + 3] = 0.0f;

				// Indices (Triangulos)
				if (x != Resolution - 1 && y != Resolution - 1)
				{
					// TRIÁNGULO 1
					indices[triIndex++] = i;
					// Intercambiamos estos dos:
					indices[triIndex++] = i + Resolution;     // Antes era: i + Resolution + 1
					indices[triIndex++] = i + Resolution + 1; // Antes era: i + Resolution

					// TRIÁNGULO 2
					indices[triIndex++] = i;
					// Intercambiamos estos dos:
					indices[triIndex++] = i + Resolution + 1; // Antes era: i + 1
					indices[triIndex++] = i + 1;              // Antes era: i + Resolution + 1
				}
			}
		}

		// 2. Calcular Alturas en GPU
		float[] heights = ComputeHeights(queryData);

		// 3. Aplicar Alturas
		for (int i = 0; i < vertices.Length; i++)
		{
			// Posición final = Dirección * (Radio + Altura)
			vertices[i] = vertices[i] * (Radius + heights[i]);
		}

		// 4. Recalcular Normales (básico para iluminación)
		// En producción se hace en Geometry Shader o Compute Shader
		RecalculateNormals(vertices, indices, ref normals);

		// 5. Crear Mesh
		var arrMesh = new ArrayMesh();
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		
		arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		
		var meshInstance = new MeshInstance3D { Mesh = arrMesh, MaterialOverride = PlanetMaterial };
		AddChild(meshInstance);
		_faces.Add(meshInstance);
	}

	private float[] ComputeHeights(float[] queries)
	{
		// Setup RD
		var shaderSpirv = ComputeShader.GetSpirV();
		var shaderRid = _rd.ShaderCreateFromSpirV(shaderSpirv);
		var pipelineRid = _rd.ComputePipelineCreate(shaderRid);

		// Buffers
		uint count = (uint)(queries.Length / 4);
		int outputBytes = (int)count * 4; // float array
		int inputBytes = queries.Length * 4;

		var outputBuffer = _rd.StorageBufferCreate((uint)outputBytes); // Salida
		var inputBuffer = _rd.StorageBufferCreate((uint)inputBytes, StructureToByteArray(queries)); // Entrada

		// Uniforms
		var u1 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		u1.AddId(outputBuffer);
		var u2 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		u2.AddId(inputBuffer);
		
		var set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u1, u2 }, shaderRid, 0);

		// Push Constants
		var pushStream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(pushStream);
		writer.Write((float)Radius);
		writer.Write((float)2.0f); // Noise Scale
		writer.Write((float)10.0f); // Noise Height (Montañas de 10m)
		writer.Write((uint)count);
		
		var computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, pipelineRid);
		_rd.ComputeListBindUniformSet(computeList, set, 0);
		_rd.ComputeListSetPushConstant(computeList, pushStream.ToArray(), (uint)pushStream.Length);
		
		_rd.ComputeListDispatch(computeList, (uint)Mathf.CeilToInt(count / 64.0f), 1, 1);
		_rd.ComputeListEnd();
		_rd.Submit();
		_rd.Sync();

		byte[] resultBytes = _rd.BufferGetData(outputBuffer);
		
		// Cleanup rapido
		_rd.FreeRid(set); _rd.FreeRid(outputBuffer); _rd.FreeRid(inputBuffer); 
		_rd.FreeRid(pipelineRid); _rd.FreeRid(shaderRid);

		return ByteArrayToFloatArray(resultBytes);
	}
	
	// Helpers de normales y bytes
	private void RecalculateNormals(Vector3[] verts, int[] inds, ref Vector3[] norms) {
		for(int i=0; i<inds.Length; i+=3) {
			Vector3 v1 = verts[inds[i]]; Vector3 v2 = verts[inds[i+1]]; Vector3 v3 = verts[inds[i+2]];
			Vector3 n = (v2-v1).Cross(v3-v1).Normalized();
			norms[inds[i]] += n; norms[inds[i+1]] += n; norms[inds[i+2]] += n;
		}
		for(int i=0; i<norms.Length; i++) norms[i] = norms[i].Normalized();
	}

	private byte[] StructureToByteArray(float[] data) {
		int size = sizeof(float) * data.Length;
		byte[] arr = new byte[size];
		Buffer.BlockCopy(data, 0, arr, 0, size);
		return arr;
	}
	
	private float[] ByteArrayToFloatArray(byte[] bytes) {
		float[] floats = new float[bytes.Length / 4];
		Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
		return floats;
	}
}
