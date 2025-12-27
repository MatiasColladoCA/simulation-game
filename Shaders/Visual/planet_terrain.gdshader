shader_type spatial;
render_mode specular_schlick_ggx;

// --- UNIFORMS ---
uniform samplerCube height_map_gpu : filter_linear, repeat_enable; 
uniform float planet_radius = 100.0;
uniform float noise_amplitude = 10.0;
// NUEVO: Recibimos el nivel del agua (0.0 a 1.0) desde C#
uniform float water_level_norm = 0.5; 

// --- COLORES ---
uniform vec3 color_seabed : source_color = vec3(0.2, 0.15, 0.1);
uniform vec3 color_beach  : source_color = vec3(0.76, 0.7, 0.5);
uniform vec3 color_grass  : source_color = vec3(0.1, 0.5, 0.1);
uniform vec3 color_rock   : source_color = vec3(0.4, 0.35, 0.3);
uniform vec3 color_snow   : source_color = vec3(0.9, 0.9, 0.95);

varying float v_height_norm; 

void vertex() {
    vec3 dir = normalize(VERTEX);
    float h_val = texture(height_map_gpu, dir).r;
    v_height_norm = h_val;
    float final_height = planet_radius + (h_val * noise_amplitude);
    VERTEX = dir * final_height;
}

void fragment() {
    // --- LÓGICA DINÁMICA DE BIOMAS ---
    // Definimos los biomas RELATIVOS al nivel del agua
    float level_water = water_level_norm;
    float level_beach = water_level_norm + 0.05; // La playa mide un 5% de altura sobre el agua
    float level_grass = water_level_norm + 0.25; // El pasto llega hasta un 25% más arriba
    float level_rock  = 0.85; // La roca empieza alto (fijo o relativo, tú decides)
    
    // Aseguramos que la roca no empiece debajo del agua si subimos mucho el nivel
    level_rock = max(level_rock, level_grass + 0.1); 

    vec3 final_color;

    if (v_height_norm < level_water) {
        // Fondo marino
        // Hacemos un gradiente: más oscuro cuanto más profundo
        float depth = (level_water - v_height_norm) * 5.0; 
        final_color = mix(color_beach, color_seabed, clamp(depth, 0.0, 1.0));
        ROUGHNESS = 0.8;
    }
    else if (v_height_norm < level_beach) {
        // Playa
        float t = smoothstep(level_water, level_beach, v_height_norm);
        final_color = mix(color_beach, color_grass, t); // Transición suave arena->pasto
        ROUGHNESS = 0.9;
    }
    else if (v_height_norm < level_grass) {
        // Pasto -> Roca
        float t = smoothstep(level_beach, level_grass, v_height_norm);
        final_color = mix(color_grass, color_rock, t);
        ROUGHNESS = 0.8;
    }
    else if (v_height_norm < level_rock) {
        // Roca pura
        float t = smoothstep(level_grass, level_rock, v_height_norm);
        final_color = mix(color_rock, color_snow, t);
        ROUGHNESS = 1.0;
    }
    else {
        // Nieve (Cima)
        final_color = color_snow;
        ROUGHNESS = 0.4;
        SPECULAR = 0.2; 
    }
    
    ALBEDO = final_color;
}