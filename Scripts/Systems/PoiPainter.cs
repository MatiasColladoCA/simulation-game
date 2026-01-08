using Godot;
using System;

public partial class PoiPainter : RefCounted
{
	private RenderingDevice _rd;
	private Rid _shader;
	private Rid _pipeline;

	public PoiPainter(RenderingDevice rd, RDShaderFile shaderFile)
	{
		_rd = rd;
		var spirv = shaderFile.GetSpirV();
		_shader = _rd.ShaderCreateFromSpirV(spirv);
		_pipeline = _rd.ComputePipelineCreate(_shader);
	}

	public void PaintInfluence(Rid influenceTextureRid, Rid poiBufferRid, Rid paramsBufferRid, uint resolution)
	{
		// 1. Preparación de Uniformes
		
		// CORRECCIÓN CRÍTICA: El Baker crea esto como UniformBuffer (std140), no StorageBuffer.
		// Si usas StorageBuffer aquí, Vulkan rechaza el set porque el recurso tiene otro usage bit.
		var uniformParams = new RDUniform { 
			UniformType = RenderingDevice.UniformType.UniformBuffer, 
			Binding = 0 
		};
		uniformParams.AddId(paramsBufferRid);

		var uniformPois = new RDUniform { 
			UniformType = RenderingDevice.UniformType.StorageBuffer, 
			Binding = 1 
		};
		uniformPois.AddId(poiBufferRid);

		var uniformTex = new RDUniform { 
			UniformType = RenderingDevice.UniformType.Image, 
			Binding = 2 
		};
		uniformTex.AddId(influenceTextureRid);

		// Validar que los recursos sean válidos antes de crear el set
		if (!paramsBufferRid.IsValid || !poiBufferRid.IsValid || !influenceTextureRid.IsValid)
		{
			GD.PrintErr("[PoiPainter] Recursos inválidos. Abortando dispatch.");
			return;
		}

		Rid uniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> 
		{ 
			uniformParams, 
			uniformPois, 
			uniformTex 
		}, _shader, 0);

		if (!uniformSet.IsValid) 
		{
			GD.PrintErr("[PoiPainter] Error creando UniformSet.");
			return;
		}

		// 2. Despacho
		long computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipeline);
		_rd.ComputeListBindUniformSet(computeList, uniformSet, 0);

		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f);
		_rd.ComputeListDispatch(computeList, groups, groups, 6u); 

		_rd.ComputeListEnd();
	}

	public new void Dispose()
	{
		if (_shader.IsValid) _rd.FreeRid(_shader);
	}
}
