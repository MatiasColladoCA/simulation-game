shader_type spatial;
render_mode specular_schlick_ggx, cull_back;

// --- 1. DATOS GEOMÉTRICOS (GPU BAKED) ---
uniform samplerCube height_map_gpu : filter_linear, repeat_enable; 
uniform samplerCube normal_map_gpu : filter_linear, repeat_enable; 
uniform samplerCube vector_field_gpu : filter_linear, repeat_enable;
uniform samplerCube influence_texture : filter_linear, repeat_enable; // POI System

// --- 2. DATOS LÓGICOS (CPU CONTROLLER) ---
// Valores ABSOLUTOS en metros (no 0-1)
uniform float planet_radius = 100.0;
uniform float min_height_absolute = 0.0; // Solo para debug/clamping
uniform float max_height_absolute = 25.0;
uniform float water_level = 0.0; 
uniform float snow_level = 40.0;

// Configuración Visual
uniform float transition_sharpness = 2.0; // Metros para mezclar biomas
uniform float ocean_depth_color_range = 10.0; // Metros para gradiente playa->fondo

// Debug
uniform bool view_vector_field = false;
uniform bool view_poi_field = false;
uniform float debug_line_density = 20.0;
uniform float debug_flow_speed = 2.0;

// --- 3. PALETA DE COLORES (AAA PBR) ---
uniform vec3 color_deep_sea : source_color = vec3(0.02, 0.05, 0.15);
uniform vec3 color_shallow  : source_color = vec3(0.1, 0.4, 0.45);
uniform vec3 color_beach    : source_color = vec3(0.76, 0.65, 0.5);
uniform vec3 color_ground   : source_color = vec3(0.15, 0.25, 0.1); // Tierra oscura
uniform vec3 color_grass    : source_color = vec3(0.1, 0.4, 0.15);
uniform vec3 color_rock     : source_color = vec3(0.35, 0.32, 0.3); // Roca gris/marrón
uniform vec3 color_snow     : source_color = vec3(0.95, 0.95, 1.0);

// --- 4. INSTANCING (QuadTree) ---
instance uniform vec3 chunk_center;
instance uniform vec3 chunk_axis_a;
instance uniform vec3 chunk_axis_b;
instance uniform float chunk_size;

// Varyings
varying float v_altitude;     // Altura absoluta sobre el radio
varying vec3 v_dir_local;     // Dirección normalizada (para sampling de Cubemaps)
varying vec3 v_world_pos;     // Posición mundo real
varying vec3 v_normal_geom;   // Normal geométrica plana (para mezcla)

// Función auxiliar para mezclar tres colores basado en altura
vec3 biome_tri_mix(float h, float edge0, float edge1, vec3 c0, vec3 c1, vec3 c2) {
    float t = smoothstep(edge0 - transition_sharpness, edge0 + transition_sharpness, h);
    vec3 base = mix(c0, c1, t);
    float t2 = smoothstep(edge1 - transition_sharpness, edge1 + transition_sharpness, h);
    return mix(base, c2, t2);
}


void vertex() {
    vec2 relative_pos = UV - 0.5;
    v_world_pos = chunk_center + (relative_pos.x * chunk_size * chunk_axis_a) + (relative_pos.y * chunk_size * chunk_axis_b);
    vec3 dir = normalize(v_world_pos);
    v_dir_local = dir;
    v_normal_geom = dir;

    float h_absolute = texture(height_map_gpu, dir).r;
    v_altitude = h_absolute;

    VERTEX = dir * (planet_radius + h_absolute);
    NORMAL = dir; 
}

void fragment() {
    float h = v_altitude; 

    // --- MÁSCARAS ---
    float w_ocean = 1.0 - smoothstep(water_level - 0.5, water_level + 0.5, h);
    float w_snow  = smoothstep(snow_level - transition_sharpness, snow_level + transition_sharpness, h);

    // --- COLORES ---
    // Seabed
    float depth = max(0.0, water_level - h);
    float depth_factor = clamp(depth / ocean_depth_color_range, 0.0, 1.0);
    vec3 c_seabed = mix(color_beach, color_deep_sea, depth_factor); 

    // Tierra
    float limit_beach = water_level + 2.0;
    float limit_grass = snow_level - 15.0;
    
    float t_beach_to_grass = smoothstep(limit_beach, limit_beach + 5.0, h);
    float t_grass_to_rock  = smoothstep(limit_grass, limit_grass + 10.0, h);

    vec3 c_terrain = mix(color_beach, color_grass, t_beach_to_grass);
    c_terrain = mix(c_terrain, color_rock, t_grass_to_rock);

    vec3 albedo_final = mix(c_terrain, c_seabed, w_ocean);
    albedo_final = mix(albedo_final, color_snow, w_snow);

    // --- PBR ---
    float roughness_final = mix(0.8, 0.9, w_ocean); // Tierra 0.8, Fondo Marino 0.9
    roughness_final = mix(roughness_final, 0.6, w_snow); // Nieve 0.6

    // --- NORMAL MAPPING (Sin grano extra) ---
    vec3 n_baked = texture(normal_map_gpu, v_dir_local).rgb * 2.0 - 1.0;
    
    // Mezcla suave con la normal geométrica para ocultar seams de los bordes del cubo (no del terreno)
    vec3 n_final = normalize(mix(v_normal_geom, n_baked, 0.85)); 
    
    NORMAL = mat3(VIEW_MATRIX) * n_final;

    // --- DEBUG ---
    if (view_vector_field) {
        vec3 flow = texture(vector_field_gpu, v_dir_local).rgb;
        float stripe = sin(dot(v_world_pos, vec3(1.0)) * 0.5 - TIME * debug_flow_speed);
        float mask = smoothstep(0.8, 0.9, stripe);
        albedo_final = mix(albedo_final, vec3(1.0, 1.0, 0.0), mask * 0.5);
    }
    
    if (view_poi_field) {
        vec4 poi = texture(influence_texture, v_dir_local);
        albedo_final = mix(albedo_final, poi.rgb, poi.a * 0.8);
        EMISSION = poi.rgb * poi.a;
    }

    ALBEDO = albedo_final;
    ROUGHNESS = roughness_final;
    SPECULAR = 0.1;
    METALLIC = 0.0;
}