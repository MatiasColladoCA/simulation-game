using Godot;
using System;
using System.Threading.Tasks;

public partial class TerrainBaker : Node
{
    [ExportGroup("Baking Settings")]
    [Export] public RDShaderFile BakerShaderFile;
    [Export] public float PlanetRadius = 100.0f;
    [Export] public float NoiseScale = 2.0f;
    [Export] public float NoiseHeight = 10.0f;
    [Export] public int CubemapResolution = 1024; // Ajustable según GPU

    public struct BakeResult
    {
        public Rid HeightMapRid;
        public Rid VectorFieldRid;
        public bool Success;
    }

    public BakeResult Bake(RenderingDevice rd)
    {
        if (BakerShaderFile == null || rd == null) 
        {
            GD.PrintErr("TerrainBaker: Faltan dependencias.");
            return new BakeResult { Success = false };
        }

        // 1. Crear Texturas
        var fmtHeight = new RDTextureFormat {
            Width = (uint)CubemapResolution, Height = (uint)CubemapResolution, Depth = 1, ArrayLayers = 6,
            TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
        };
        var heightMapRid = rd.TextureCreate(fmtHeight, new RDTextureView(), new Godot.Collections.Array<byte[]>());

        var fmtVectors = new RDTextureFormat {
            Width = (uint)CubemapResolution, Height = (uint)CubemapResolution, Depth = 1, ArrayLayers = 6,
            TextureType = RenderingDevice.TextureType.Cube, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
        };
        var vectorFieldRid = rd.TextureCreate(fmtVectors, new RDTextureView(), new Godot.Collections.Array<byte[]>());

        // 2. Pipeline
        var bakerSpirv = BakerShaderFile.GetSpirV();
        var bakerShaderRid = rd.ShaderCreateFromSpirV(bakerSpirv);
        var bakerPipeline = rd.ComputePipelineCreate(bakerShaderRid);

        // 3. Uniforms
        var uOutHeight = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOutHeight.AddId(heightMapRid);
        var uOutVectors = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 }; uOutVectors.AddId(vectorFieldRid);
        var bakerSet = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uOutHeight, uOutVectors }, bakerShaderRid, 0);

        // 4. Push Constants
        var stream = new System.IO.MemoryStream();
        var writer = new System.IO.BinaryWriter(stream);
        writer.Write((float)PlanetRadius);
        writer.Write((float)NoiseScale);
        writer.Write((float)NoiseHeight);
        writer.Write((uint)CubemapResolution);
        byte[] pushBytes = stream.ToArray();

        // 5. Dispatch
        var computeList = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(computeList, bakerPipeline);
        rd.ComputeListBindUniformSet(computeList, bakerSet, 0);
        rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
        
        uint groups = (uint)Mathf.CeilToInt(CubemapResolution / 32.0f);
        rd.ComputeListDispatch(computeList, groups, groups, 6);
        rd.ComputeListEnd();

        // Limpieza de pipeline (las texturas persisten)
        rd.FreeRid(bakerPipeline);
        rd.FreeRid(bakerShaderRid); // El shader module se puede liberar tras compilar pipeline, o aquí.
        // rd.FreeRid(bakerSet); // Cuidado, liberar el set a veces da problemas si el comando no terminó, mejor dejar que el orquestador limpie o Godot.

        GD.Print($"[TerrainBaker] Baked {CubemapResolution}x{CubemapResolution}");

        return new BakeResult { HeightMapRid = heightMapRid, VectorFieldRid = vectorFieldRid, Success = true };
    }
}