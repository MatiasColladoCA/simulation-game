shader_type spatial;
render_mode specular_schlick_ggx;

// --- UNIFORMS GLOBALES ---
uniform samplerCube height_map_gpu : filter_linear, repeat_enable; 
uniform float planet_radius = 100.0;
uniform float noise_amplitude = 10.0;
uniform float water_level_norm = 0.5; 

uniform samplerCube vector_field_gpu : filter_linear, repeat_enable;
uniform float debug_line_density = 10.0; 
uniform float debug_flow_speed = 2.0;    

uniform bool view_vector_field = false;
uniform bool view_poi_field = false;

// Esta textura viene del Compute Shader (RGBA16F)
// RGB = Color (Celeste -> Blanco)
// A = Influencia (0.0 -> 1.0)
uniform samplerCube influence_texture; 

// --- COLORES ---
uniform vec3 color_seabed : source_color = vec3(0.2, 0.15, 0.1);
uniform vec3 color_beach  : source_color = vec3(0.76, 0.7, 0.5);
uniform vec3 color_grass  : source_color = vec3(0.1, 0.5, 0.1);
uniform vec3 color_rock   : source_color = vec3(0.4, 0.35, 0.3);
uniform vec3 color_snow   : source_color = vec3(0.9, 0.9, 0.95);

// --- UNIFORMS DE INSTANCIA ---
instance uniform vec3 chunk_center;
instance uniform vec3 chunk_axis_a;
instance uniform vec3 chunk_axis_b;
instance uniform float chunk_size;

varying float v_height_norm; 
varying vec3 v_dir_local;
varying vec3 v_world_pos;

void vertex() {
    vec2 relative_pos = UV - 0.5;
    vec3 pos_on_cube = chunk_center + (relative_pos.x * chunk_size * chunk_axis_a) + (relative_pos.y * chunk_size * chunk_axis_b);
    vec3 dir = normalize(pos_on_cube);
    
    float h_val = texture(height_map_gpu, dir).r;
    v_height_norm = h_val;
    
    float final_height = planet_radius + (h_val * noise_amplitude);
    VERTEX = dir * final_height;
    
    v_dir_local = dir; 
    v_world_pos = VERTEX; 
    NORMAL = dir; 
}

void fragment() {
    float h = v_height_norm;

    // --- MEZCLA DE BIOMAS (Igual que antes) ---
    float level_water = water_level_norm;
    float level_beach = water_level_norm + 0.05; 
    float level_grass = water_level_norm + 0.25; 
    float level_rock  = 0.85; 
    level_rock = max(level_rock, level_grass + 0.1); 

    vec3 final_color;

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

    vec3 result_color = final_color;

    // --- VISUALIZACIÓN DE VECTORES ---
    if (view_vector_field) {
        vec3 flow = texture(vector_field_gpu, v_dir_local).rgb;
        float static_stripes = dot(v_world_pos, vec3(0.5, 0.5, 0.5));
        float phase = TIME * debug_flow_speed;
        float flow_pattern = sin((static_stripes - phase) * debug_line_density);
        float line_mask = smoothstep(-0.2, 0.2, flow_pattern);
        vec3 flow_rgb = flow * 0.5 + 0.5;
        vec3 flow_visual = mix(flow_rgb * 0.3, flow_rgb, line_mask);
        result_color = mix(result_color, flow_visual, 0.7);
    }

    // --- VISUALIZACIÓN DE POIS (CORREGIDA) ---
    if (view_poi_field) {
        vec4 poi_sample = texture(influence_texture, v_dir_local);
        
        // Asignamos el color directo (Magenta o Verde)
        result_color = poi_sample.rgb;
        
        // --- AGREGAR ESTA LÍNEA ---
        // Hacemos que el color emita luz para que no se vea negro en la sombra
        EMISSION = result_color; 
        // --------------------------
    }
    
    ALBEDO = result_color;
}