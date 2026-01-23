shader_type spatial;

// CRÍTICO: 'skip_vertex_transform' le dice a Godot: 
// "Yo ya calculé la posición respecto a la cámara, no apliques la ModelViewMatrix de nuevo".
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx, skip_vertex_transform;

// --- UNIFORMS ---
uniform sampler2D agent_pos_texture : filter_nearest;   // IMPORTANTE: filter_nearest
uniform sampler2D agent_color_texture : filter_nearest;
uniform int tex_width = 2048; // Entero, coincidir con C#

uniform float agent_radius_visual = 1.5; // Aumentado un poco para debug

varying vec4 v_color;

void vertex() {
    // 1. GPU FETCH
    int idx = INSTANCE_ID;
    
    // Matemática de índice entero segura
    int row = idx / tex_width;
    int col = idx % tex_width;
    ivec2 tex_coord = ivec2(col, row);

    vec4 data_pos = texelFetch(agent_pos_texture, tex_coord, 0);
    vec4 data_col = texelFetch(agent_color_texture, tex_coord, 0);

    // 2. FILTRO DE VIDA (DEBUG: Deshabilitado para diagnóstico)
    // Si W < 0.1, colapsamos el vértice.
    // data_col.w viene del Compute Shader (0.0 = Muerto, 1.0 = Vivo)
    bool is_dead = false; // DEBUG: Forzado a false para ver todos los agentes
    
    // DEBUG: Mostrar valores leídos
    // GD.Print("Agent " + idx + " pos=" + data_pos.xyz + " col.w=" + data_col.w); // En Godot esto no funciona directo
    
    if (is_dead) {
        // Colapsar al infinito o a cero
        VERTEX = vec3(0.0);
    } else {
        // data_pos.xyz AHORA ES LOCAL (Relativo al centro del planeta)
        vec3 local_pos_on_sphere = data_pos.xyz;

        // 1. Transformar posición local -> View Space
        // MODELVIEW_MATRIX aplica la transformación del Planeta + Cámara
        vec3 view_center = (MODELVIEW_MATRIX * vec4(local_pos_on_sphere, 1.0)).xyz;

        // 2. Billboard
        // Queremos que el quad mire a la cámara. En View Space, la cámara mira hacia -Z.
        // Simplemente sumamos el offset XY en View Space.
        vec2 offset = UV * 2.0 - 1.0;
        vec3 local_quad_pos = vec3(offset * agent_radius_visual, 0.0);

        // 3. Resultado final
        VERTEX = view_center + local_quad_pos;
        
        v_color = data_col;
        // v_color = vec4(1.0, 0.0, 0.0, 1.0);
    }
}

void fragment() {
    // --- FORMA CIRCULAR ---
    vec2 uv_centered = UV * 2.0 - 1.0;
    float dist_sq = dot(uv_centered, uv_centered);
    
    // Recorte circular duro
    if (dist_sq > 1.0) discard;
    
    // --- COLOR DEBUG ---
    // Si el color viene negro (0,0,0), pintamos magenta para avisar que la textura de color falla
    // pero la de posición funciona.
    if (length(v_color.rgb) < 0.01) {
        ALBEDO = vec3(1.0, 0.0, 1.0); // Magenta Debug
    } else {
        ALBEDO = v_color.rgb;
    }
    
    // Simulación simple de esfera
    float z = sqrt(1.0 - dist_sq);
    NORMAL_MAP = vec3(uv_centered, z);
    NORMAL_MAP_DEPTH = 1.0;
    
    ROUGHNESS = 0.5;
    METALLIC = 0.0;
    EMISSION = v_color.rgb * 0.2;
}