using Godot;

public partial class AgentRender : MultiMeshInstance3D
{
    private ShaderMaterial _material;
    
    // Configuración visual
    private const int DATA_TEX_WIDTH = 2048; // Debe coincidir con AgentSystem
    private const float AGENT_RADIUS = 1.5f;

    // Inicialización única
    public void Initialize(Rid posTexRid, Rid colTexRid, int agentCount)
    {
        GD.Print($"[AgentRender] Inicializando visualización para {agentCount} agentes...");

        // 1. Crear wrappers para las Texturas (RID -> Texture2D)
        // AgentRender es dueño de estos wrappers visuales.
        var posTexWrapper = new Texture2Drd { TextureRdRid = posTexRid };
        var colTexWrapper = new Texture2Drd { TextureRdRid = colTexRid };

        // 2. Cargar Shader
        var shader = GD.Load<Shader>("res://Shaders/Visual/agent_render.gdshader");
        if (shader == null)
        {
            GD.PrintErr("[AgentRender] CRITICAL: No se encontró el shader visual.");
            return;
        }

        // 3. Crear Material
        _material = new ShaderMaterial();
        _material.Shader = shader;
        _material.SetShaderParameter("agent_pos_texture", posTexWrapper);
        _material.SetShaderParameter("agent_color_texture", colTexWrapper);
        _material.SetShaderParameter("tex_width", DATA_TEX_WIDTH);
        _material.SetShaderParameter("agent_radius_visual", AGENT_RADIUS);
        
        // DEBUG: Forzar visibilidad inicial si quieres probar
        // _material.SetShaderParameter("debug_force_visible", true); 

        // 4. Configurar MultiMesh (NOSOTROS SOMOS LA INSTANCIA)
        this.Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            InstanceCount = agentCount,
            Mesh = new QuadMesh 
            { 
                Size = new Vector2(1.0f, 1.0f),
                Orientation = QuadMesh.OrientationEnum.Z 
            },
            // AABB Gigante para evitar que Godot deje de dibujar si miras de reojo
            CustomAabb = new Aabb(new Vector3(-50000, -50000, -50000), new Vector3(100000, 100000, 100000))
        };
        
        // Inicializar transforms a Identity para evitar problemas de "MultiMesh vacío"
        for(int i=0; i < agentCount; i++) {
            this.Multimesh.SetInstanceTransform(i, Transform3D.Identity);
        }

        this.MaterialOverride = _material;
        
        // CRÍTICO: Asegurar que se dibuje sombras y geometría
        this.CastShadow = ShadowCastingSetting.On;
        this.VisibilityRangeEnd = 5000.0f; // Distancia de visión

        GD.Print("[AgentRender] Visualización Configurada OK.");
    }

    
}