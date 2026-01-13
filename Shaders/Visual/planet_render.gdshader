shader_type spatial;
render_mode specular_schlick_ggx, cull_back;

// --- UNIFORMS ---
uniform samplerCube height_map_gpu : filter_linear, repeat_enable;
uniform samplerCube normal_map_gpu : filter_linear, repeat_enable;
uniform samplerCube vector_field_gpu : filter_linear, repeat_enable;
uniform samplerCube influence_texture : filter_linear, repeat_enable;

uniform float planet_radius = 100.0;
// uniform float noise_amplitude = 10.0;
uniform float water_level_norm = 0.5;
uniform bool view_vector_field = false;
uniform bool view_poi_field = false;
uniform float debug_line_density = 10.0;
uniform float debug_flow_speed = 2.0;

uniform float min_height_absolute;
uniform float max_height_absolute;


// Colores biomas
uniform vec3 color_seabed = vec3(0.2,0.15,0.1);
uniform vec3 color_beach  = vec3(0.76,0.7,0.5);
uniform vec3 color_grass  = vec3(0.25,0.4,0.35);
uniform vec3 color_rock   = vec3(0.4,0.35,0.3);
uniform vec3 color_snow   = vec3(0.9,0.9,0.95);

// --- INSTANCING ---
instance uniform vec3 chunk_center;
instance uniform vec3 chunk_axis_a;
instance uniform vec3 chunk_axis_b;
instance uniform float chunk_size;

// VARYINGS
varying float v_height_norm;
varying vec3 v_dir_local;
varying vec3 v_world_pos;

void vertex() {
    vec2 relative_pos = UV - 0.5;
    vec3 pos_on_cube = chunk_center
        + relative_pos.x * chunk_size * chunk_axis_a
        + relative_pos.y * chunk_size * chunk_axis_b;

    vec3 dir = normalize(pos_on_cube);
    float h_val = texture(height_map_gpu, dir).r;

    float h_norm = (h_val - min_height_absolute)
                / max(0.0001, max_height_absolute - min_height_absolute);

    h_norm = clamp(h_norm, 0.0, 1.0);

    v_height_norm = h_norm;


    VERTEX = dir * (planet_radius + h_val);
    v_world_pos = VERTEX;
    v_dir_local = dir;

    // NORMAL = vec3(0.0); // o eliminar la asignación

}


void fragment() {
    float h = v_height_norm;

    // Normales horneadas del baker
    vec3 n_baked = texture(normal_map_gpu, v_dir_local).xyz;

    // Normal geométrica de fallback (solo para seam blending)
    vec3 n_geo = normalize(cross(dFdx(v_world_pos), dFdy(v_world_pos)));

    // Forzar orientación correcta respecto al centro del planeta
    if (dot(n_geo, v_dir_local) < 0.0) {
        n_geo = -n_geo;
    }

    // Mezcla suave para ocultar seams del cubemap
    vec3 n_final = n_geo;
    
    // Transformar al espacio vista
    NORMAL = mat3(VIEW_MATRIX) * n_geo;

    // --- BIOMAS ---
    vec3 final_color;
    if (h < water_level_norm) {
        float depth = (water_level_norm - h) * 5.0;
        final_color = mix(color_beach, color_seabed, clamp(depth,0.0,1.0));
        ROUGHNESS = 0.8;
    } else if (h < water_level_norm+0.05) {
        float t = smoothstep(water_level_norm, water_level_norm+0.05, h);
        final_color = mix(color_beach, color_grass, t);
        ROUGHNESS = 0.9;
    } else if (h < 0.75) {
        float t = smoothstep(water_level_norm+0.15, 0.8, h);
        final_color = mix(color_grass, color_rock, t);
        ROUGHNESS = 0.8;
    } else if (h < 0.85) {
        float t = smoothstep(0.75,0.85,h);
        final_color = mix(color_rock, color_snow, t);
        ROUGHNESS = 1.0;
    } else {
        final_color = color_snow;
        ROUGHNESS = 0.6;
        SPECULAR = 0.1;
    }

    // --- DEBUG VECTOR FIELD ---
    if (view_vector_field) {
        vec3 flow = texture(vector_field_gpu, v_dir_local).rgb;
        float stripes = dot(v_world_pos, vec3(0.5)) - TIME*debug_flow_speed;
        float mask = smoothstep(-0.2,0.2,sin(stripes*debug_line_density));
        vec3 flow_vis = mix(flow*0.3, flow, mask);
        final_color = mix(final_color, flow_vis,0.7);
    }

    // --- DEBUG POI ---
    if (view_poi_field) {
        vec4 poi = texture(influence_texture, v_dir_local);
        final_color = poi.rgb;
        EMISSION = poi.rgb * 0.5;
    }

    ALBEDO = final_color;
    METALLIC = 0.0;
}
