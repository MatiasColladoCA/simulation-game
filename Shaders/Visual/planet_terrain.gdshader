shader_type spatial;
render_mode specular_schlick_ggx;

// --- UNIFORMS GLOBALES ---
uniform samplerCube height_map_gpu : filter_linear, repeat_enable; 
uniform float planet_radius = 100.0;
uniform float noise_amplitude = 10.0;
uniform float water_level_norm = 0.5; 
uniform bool debug_flow = false;
uniform samplerCube vector_field_gpu : filter_linear, repeat_enable;
uniform float debug_line_density = 10.0; // Controla qué tan finas son las líneas
uniform float debug_flow_speed = 2.0;    // Velocidad del veteado


// --- COLORES ---
uniform vec3 color_seabed : source_color = vec3(0.2, 0.15, 0.1);
uniform vec3 color_beach  : source_color = vec3(0.76, 0.7, 0.5);
uniform vec3 color_grass  : source_color = vec3(0.1, 0.5, 0.1);
uniform vec3 color_rock   : source_color = vec3(0.4, 0.35, 0.3);
uniform vec3 color_snow   : source_color = vec3(0.9, 0.9, 0.95);

// --- UNIFORMS DE INSTANCIA (Chunks) ---
// Estos son vitales para que el Quadtree funcione
instance uniform vec3 chunk_center;
instance uniform vec3 chunk_axis_a;
instance uniform vec3 chunk_axis_b;
instance uniform float chunk_size;



varying float v_height_norm; 
varying vec3 v_dir_local;
varying vec3 v_world_pos;

void vertex() {
    // 1. Calcular posición en el CUBO a partir del plano UV del mesh
    vec2 relative_pos = UV - 0.5;
    
    // Proyectamos: Centro + (X * EjeA) + (Y * EjeB)
    vec3 pos_on_cube = chunk_center + (relative_pos.x * chunk_size * chunk_axis_a) + (relative_pos.y * chunk_size * chunk_axis_b);
    
    // 2. Esferificación
    vec3 dir = normalize(pos_on_cube);
    
    // 3. Lectura de Altura
    float h_val = texture(height_map_gpu, dir).r;
    v_height_norm = h_val;
    
    // 4. Desplazamiento final
    float final_height = planet_radius + (h_val * noise_amplitude);
    VERTEX = dir * final_height;
    
    v_dir_local = dir; // Guardar para el fragment shader
    v_world_pos = VERTEX; // Guardamos la posición local del vértice corregida
    // Recalcular normales
    NORMAL = dir; 
}

void fragment() {
    // Usamos la altura pura interpolada, sin ruido añadido
    float h = v_height_norm;

    // Definición de Niveles
    float level_water = water_level_norm;
    float level_beach = water_level_norm + 0.05; 
    float level_grass = water_level_norm + 0.25; 
    float level_rock  = 0.85; // Ajustable según tu gusto
    
    // Aseguramos que la roca no empiece antes que el pasto
    level_rock = max(level_rock, level_grass + 0.1); 

    vec3 final_color;

    // Lógica de Mezcla (Gradientes suaves)
    if (h < level_water) {
        float depth = (level_water - h) * 5.0; 
        final_color = mix(color_beach, color_seabed, clamp(depth, 0.0, 1.0));
        ROUGHNESS = 0.8;
    }
    else if (h < level_beach) {
        float t = smoothstep(level_water, level_beach, h);
        final_color = mix(color_beach, color_grass, t);
        ROUGHNESS = 0.9;
    }
    else if (h < level_grass) {
        float t = smoothstep(level_beach, level_grass, h);
        final_color = mix(color_grass, color_rock, t);
        ROUGHNESS = 0.8;
    }
    else if (h < level_rock) {
        float t = smoothstep(level_grass, level_rock, h);
        final_color = mix(color_rock, color_snow, t);
        ROUGHNESS = 1.0;
    }
    else {
        final_color = color_snow;
        ROUGHNESS = 0.4;
        SPECULAR = 0.2; 
    }
    
    
    if (debug_flow) {
        vec3 flow = texture(vector_field_gpu, v_dir_local).rgb;
        
        // --- NUEVA LÓGICA DE VETEADO ---
        // 1. Creamos una coordenada basada en la posición local proyectada en el flujo
        // Usamos el tiempo para animar el desplazamiento
        // Nueva lógica: Proyectamos la posición sobre la dirección del flujo
        // Esto hace que las franjas se orienten SEGÚN el vector local.
        // Esto crea planos perpendiculares a la dirección del movimiento.

        // 1. Usamos una coordenada de mundo estática para que las líneas no bailen
        // 2. Usamos el producto punto del flujo para que la animación siga la dirección del vector
        float static_stripes = dot(v_world_pos, vec3(0.5, 0.5, 0.5));
        float flow_direction = dot(flow, v_dir_local); // Dirección relativa al observador
        
        // Multiplicamos TIME por el flujo para que solo se muevan hacia donde apunta el vector
        float phase = TIME * debug_flow_speed;
        float flow_pattern = sin((static_stripes - phase) * debug_line_density);



        // 2. Suavizamos las líneas (anti-aliasing básico)
        float line_mask = smoothstep(-0.2, 0.2, flow_pattern);
        
        // 3. Mezclamos el color del flujo con el patrón de líneas
        // Las líneas oscuras ayudan a ver el contraste del movimiento
        vec3 flow_rgb = flow * 0.5 + 0.5;
        ALBEDO = mix(flow_rgb * 0.3, flow_rgb, line_mask);
        
    } else {
        ALBEDO = final_color;
    }
}