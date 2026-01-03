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
		_shader = _rd.ShaderCreateFromSpirV(shaderFile.GetSpirV());
		_pipeline = _rd.ComputePipelineCreate(_shader);
	}

	// --- CORRECCIÓN: Tipo de dato uint para coincidir con PlanetParams ---
	// ANTES: public void PaintInfluence(..., int resolution)
	public void PaintInfluence(Rid influenceTextureRid, Rid poiBufferRid, Rid paramsBufferRid, uint resolution)
	{
		// 1. Preparación de Uniformes
		var uniformParams = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		uniformParams.AddId(paramsBufferRid);

		var uniformPois = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		uniformPois.AddId(poiBufferRid);

		var uniformTex = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
		uniformTex.AddId(influenceTextureRid);

		Rid uniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> 
		{ 
			uniformParams, 
			uniformPois, 
			uniformTex 
		}, _shader, 0);

		// 2. Despacho de Cómputo
		long computeList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(computeList, _pipeline);
		_rd.ComputeListBindUniformSet(computeList, uniformSet, 0);

		uint groups = (uint)Mathf.CeilToInt(resolution / 8.0f);
		_rd.ComputeListDispatch(computeList, groups, groups, 6u); 

		_rd.ComputeListEnd();
		
		// Nota: Submit/Sync debería ser manejado por el orquestador si se encadenan pases,
		// pero se mantiene aquí según el snippet original.
		// _rd.Submit();
		// _rd.Sync();
	}

	public new void Dispose()
	{
		// --- CORRECCIÓN: Propiedad IsValid sin paréntesis ---
		// ANTES: if (_shader.IsValid())
		if (_shader.IsValid) _rd.FreeRid(_shader);
	}
}
