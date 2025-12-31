shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, depth_prepass_alpha;

// --- UNIFORMS NUEVOS ---
uniform sampler2D agent_pos_texture;   // Data Texture (Pos)
uniform sampler2D agent_color_texture; // Data Texture (Color)
uniform int tex_width = 2048;          // Debe coincidir con C#
uniform float agent_radius_visual = 0.25;

varying vec4 v_color;

void vertex() {
    // 1. OBTENER DATOS DESDE TEXTURA (GPU FETCH)
    int idx = INSTANCE_ID;
    int w = tex_width;
    ivec2 tex_coord = ivec2(idx % w, idx / w);

    // Reemplazo: Usamos texelFetch directamente para validar el estado
    // Eliminada la línea: vec4 agent_data = texture(agent_pos_texture, uv);
    vec4 data_pos = texelFetch(agent_pos_texture, tex_coord, 0);
    
    // 2. FILTRO DE ESTADO (Dormancia y Muerte)
    // .w de posición controla la existencia (0.0 = Dormido)
    // .w de color controla el estado lógico (Status)
    if (data_pos.w < 0.1) {
        // Colapsar geometría de agentes inactivos
        VERTEX = vec3(0.0);
        MODELVIEW_MATRIX = mat4(0.0); 
    } else {
        vec4 data_col = texelFetch(agent_color_texture, tex_coord, 0);
        vec3 world_pos = data_pos.xyz;
        float status = data_col.w;
        
        // Lógica de escala
        float scale_factor = 1.0;
        if (status < 0.5 || status > 1.5) {
            scale_factor = 0.0; 
        }

        // 3. CORRECCIÓN DE ALTURA (Visual Offset)
        vec3 up = normalize(world_pos);
        world_pos += up * (agent_radius_visual * 2.0); 

        // 4. BILLBOARDING EN VIEW SPACE
        vec3 center_view = (VIEW_MATRIX * vec4(world_pos, 1.0)).xyz;

        // Reset de rotación y aplicar escala
        MODELVIEW_MATRIX = mat4(
            vec4(scale_factor, 0.0, 0.0, 0.0),
            vec4(0.0, scale_factor, 0.0, 0.0),
            vec4(0.0, 0.0, 1.0, 0.0),
            vec4(center_view, 1.0)
        );
        
        v_color = data_col;
    }
}

void fragment() {
    vec2 uv_centered = UV * 2.0 - 1.0;
    uv_centered.y *= -1.0; 

    float dist_sq = dot(uv_centered, uv_centered);
    if (dist_sq > 1.0) discard;
    
    float z = sqrt(1.0 - dist_sq);
    vec3 sphere_normal = vec3(uv_centered, z);
    
    NORMAL = normalize(sphere_normal);
    ALBEDO = v_color.rgb;
    ROUGHNESS = 0.2;
    METALLIC = 0.0;
}