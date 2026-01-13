using Godot;

public partial class AgentRender : MultiMeshInstance3D
{
    private ShaderMaterial _material;
    private const int DATA_TEX_WIDTH = 2048; // Debe coincidir con AgentSystem

    public void Initialize(Rid positionTextureRid, Rid colorTextureRid, int agentCount)
{
    GD.Print($"[AgentRender] Iniciando... Agentes solicitados: {agentCount}");

    if (agentCount <= 0)
    {
        GD.PrintErr("[AgentRender] ERROR: AgentCount es 0 o negativo. Abortando.");
        return;
    }

    // --- 1. CARGAR SHADER CON VALIDACIÓN ---
    string shaderPath = "res://Shaders/Visual/agent_render.gdshader"; 
    var shader = GD.Load<Shader>(shaderPath);

    if (shader == null)
    {
        GD.PrintErr($"[AgentRender] FATAL: No se pudo cargar el shader en: {shaderPath}");
        GD.PrintErr("[AgentRender] Verificá mayúsculas, extensión y que el archivo exista.");
        return; // Salimos para no causar más daños
    }
    else
    {
        GD.Print("[AgentRender] Shader cargado correctamente.");
    }

    // --- 2. CREAR MATERIAL ---
    if (_material == null) _material = new ShaderMaterial();
    _material.Shader = shader;
    
    // Validar texturas (aunque sean RIDs, verificamos que no sean vacíos)
    if (!positionTextureRid.IsValid || !colorTextureRid.IsValid)
    {
        GD.PrintErr("[AgentRender] ERROR: RIDs de texturas inválidos.");
    }

    var posTexWrapper = new Texture2Drd { TextureRdRid = positionTextureRid };
    var colTexWrapper = new Texture2Drd { TextureRdRid = colorTextureRid };

    _material.SetShaderParameter("agent_pos_texture", posTexWrapper);
    _material.SetShaderParameter("agent_color_texture", colTexWrapper);
    _material.SetShaderParameter("tex_width", 2048.0f); 
    _material.SetShaderParameter("agent_radius", 1.0f); 

    // --- 3. CONFIGURAR MULTIMESH ---
    if (Multimesh == null)
    {
        Multimesh = new MultiMesh();
        Multimesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        Multimesh.UseColors = false;
        Multimesh.InstanceCount = agentCount;
        Multimesh.Mesh = new QuadMesh { 
            Size = new Vector2(2.0f, 2.0f),
            Orientation = QuadMesh.OrientationEnum.Z // Importante
        };
        GD.Print("[AgentRender] MultiMesh creado y asignado.");
    }

    // --- 4. ASIGNACIÓN FINAL ---
    this.MaterialOverride = _material;
    
    // Verificación final
    if (this.MaterialOverride != null)
        GD.Print("[AgentRender] ÉXITO: MaterialOverride asignado.");
    else
        GD.PrintErr("[AgentRender] ERROR: Falló la asignación de MaterialOverride.");

    // AABB
    this.CustomAabb = new Aabb(new Vector3(-50000, -50000, -50000), new Vector3(100000, 100000, 100000));
}


}