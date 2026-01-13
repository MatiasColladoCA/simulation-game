shader_type spatial;
// Desactivamos el culling para que el sprite se vea por ambos lados si gira
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx;

// --- UNIFORMS ---
uniform sampler2D agent_pos_texture;   // Posición (XYZ) + Timer (W)
uniform sampler2D agent_color_texture; // Color (RGB) + Vida (W)
uniform int tex_width = 2048;

// TAMAÑO VISUAL: Radio del agente
uniform float agent_radius_visual = 0.2; 

varying vec4 v_color;

void vertex() {
    // 1. GPU FETCH
    int idx = INSTANCE_ID;
    ivec2 tex_coord = ivec2(idx % tex_width, idx / tex_width);

    vec4 data_pos = texelFetch(agent_pos_texture, tex_coord, 0);
    vec4 data_col = texelFetch(agent_color_texture, tex_coord, 0);

    // 2. FILTRO DE VIDA (Sin usar NAN para evitar crash)
    if (data_col.w < 0.1) {
        // Colapsar vértice a 0.0 (Invisible y seguro)
        VERTEX = vec3(0.0);
        // Anular matriz para evitar artefactos
        MODELVIEW_MATRIX = mat4(0.0);
    } else {
        // --- TÉCNICA: MANUAL CAMERA-FACING QUAD ---
        
        // A. Posición del Centro del Agente en el Mundo
        vec3 world_center = data_pos.xyz;

        // B. Offset Vertical (Hover)
        // Levantamos el centro un radio hacia arriba (en dirección normal de superficie)
        // para que la esfera parezca apoyada sobre el suelo.
        vec3 up_dir = vec3(0,1,0);
        if (length(world_center) > 0.001) up_dir = normalize(world_center);
        world_center += up_dir * agent_radius_visual;

        // C. Convertir el Centro al "View Space" (Espacio de Cámara)
        // En View Space, la cámara está en (0,0,0) y mira hacia -Z.
        vec3 view_center = (VIEW_MATRIX * vec4(world_center, 1.0)).xyz;

        // D. Reconstruir el Vértice Local (Anti-Fideo)
        // Ignoramos la rotación de la malla original.
        // Usamos las coordenadas UV para saber qué esquina del cuadrado es este vértice.
        // UV (0,0) -> (-1, -1), UV (1,1) -> (1, 1)
        vec2 corner_offset = (UV * 2.0 - 1.0);
        
        // Escalamos por el radio visual
        vec3 local_pos = vec3(corner_offset * agent_radius_visual, 0.0);

        // E. Posición Final = Centro en Cámara + Offset Local
        // Al sumar esto en View Space, el offset siempre es plano respecto a la pantalla.
        VERTEX = view_center + local_pos;

        // F. LIMPIEZA DE MATRIZ
        // Como ya calculamos la posición final en View Space nosotros mismos (paso C y E),
        // seteamos la MODELVIEW_MATRIX a Identidad para que Godot no la vuelva a transformar.
        MODELVIEW_MATRIX = mat4(1.0);
        
        v_color = data_col;
    }
}

void fragment() {
    // --- IMPOSTOR ESFÉRICO ---
    vec2 uv_centered = UV * 2.0 - 1.0;
    
    // Invertir Y si se ve al revés (depende de la malla Quad)
    // uv_centered.y *= -1.0; 

    float dist_sq = dot(uv_centered, uv_centered);
    
    if (dist_sq > 1.0) discard;
    
    // Normal falsa
    float z = sqrt(1.0 - dist_sq);
    vec3 sphere_normal = vec3(uv_centered, z);
    
    NORMAL = normalize(sphere_normal);
    ALBEDO = v_color.rgb;
    
    ROUGHNESS = 0.3;
    METALLIC = 0.0;
    EMISSION = v_color.rgb * 0.2;
}