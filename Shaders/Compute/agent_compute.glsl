#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// --- BINDINGS ---
layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;
layout(set = 0, binding = 1, rgba16f) writeonly uniform imageCube vector_field;
layout(set = 0, binding = 2, rgba16f) writeonly uniform imageCube normal_map; // <--- AHORA SÍ LO USAREMOS
layout(set = 0, binding = 3, std430) restrict buffer StatsBuffer { 
    int min_h_fixed; 
    int max_h_fixed; 
} stats;

layout(set = 0, binding = 4, std140) uniform BakeParams {
    vec4 noise_settings; 
    vec4 curve_params;   
    vec4 global_offset;  
    vec4 detail_params;     
    vec4 res_offset;     
    vec4 pad_uv;   
} params;

const float FIXED_POINT_SCALE = 100000.0;

// ... [MANTÉN AQUÍ TUS FUNCIONES DE RUIDO: mod289, permute, snoise, simple_fbm, rigid_noise] ...
// (Para ahorrar espacio en la respuesta, asumo que copias tus funciones de ruido aquí arriba igual que antes)

// ... [MANTÉN AQUÍ TUS HELPER FUNCTIONS: ease_in_cubic, smooth_blend, curl_noise] ...


// --- TU LÓGICA DE ALTURA (INTACTA) ---
float get_terrain_height(vec3 dir) {
    vec3 pos = dir + params.global_offset.xyz;
    float scale = params.noise_settings.x;
    float warp_str = params.noise_settings.w;

    // Domain Warping
    vec3 warp_noise = vec3(snoise(pos * scale * 0.5), snoise(pos * scale * 0.5 + vec3(5.2)), snoise(pos * scale * 0.5 + vec3(1.3)));
    vec3 p = pos + warp_noise * warp_str;

    // Capas
    float d_freq = params.detail_params.x;
    float continent_shape = simple_fbm(p * scale, 5, 0.5, 2.0);
    float h = continent_shape;
    float mountain_density = simple_fbm((p + vec3(100.0)) * scale * 1.5, 3, 0.5, 2.0);
    float ground_detail = simple_fbm(p * scale * d_freq * 0.5, 3, 0.5, 2.0) * 0.05;

    // Biomas
    float sea_level = params.curve_params.x; 

    if (h > sea_level) {
        float land_factor = smoothstep(sea_level, sea_level + 0.1, h);
        float mask_start = params.detail_params.z; 
        float mask_end = params.detail_params.w;   
        float is_mountain = smoothstep(mask_start, mask_end, mountain_density);
        
        float ridges = rigid_noise(p * scale * d_freq, 6, 0.5, 2.0, params.detail_params.y);
        float ridges_sharp = ridges * ridges; 
        float mtns = ridges_sharp * params.curve_params.y; 
        float plains = ground_detail * 2.0;

        float land_shape = mix(plains, mtns, is_mountain);
        h += land_shape * land_factor;
    } else {
        h += ground_detail; 
    }
    return h * 0.5 + 0.5;
}

// --- NUEVA FUNCIÓN CRÍTICA: CÁLCULO DE NORMALES ---
vec3 compute_normal(vec3 p, float resolution) {
    // Epsilon basado en la resolución para muestrear vecinos
    float eps = 1.0 / resolution; 
    
    // Generamos dos vectores tangentes arbitrarios pero ortogonales a p
    vec3 axis = vec3(0.0, 1.0, 0.0);
    if (abs(dot(p, axis)) > 0.99) axis = vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(p, axis));
    vec3 bitangent = normalize(cross(p, tangent));
    
    // Muestreamos la altura en 3 puntos muy cercanos
    float h_center = get_terrain_height(p);
    float h_tan    = get_terrain_height(normalize(p + tangent * eps));
    float h_bitan  = get_terrain_height(normalize(p + bitangent * eps));
    
    // Calculamos la pendiente (diferencia finita)
    float amplitude = params.curve_params.z; // Importante escalar por la amplitud real
    
    vec3 p_center = p * (1.0 + h_center * amplitude); // Ojo: simplificado para dirección
    // Aproximación de gradiente
    float dH_dT = (h_tan - h_center) * amplitude;
    float dH_dB = (h_bitan - h_center) * amplitude;
    
    // Construimos la normal perturbada
    // La normal base es 'p' (hacia afuera de la esfera)
    // Restamos el gradiente para inclinarla
    vec3 n = normalize(p - tangent * dH_dT * 50.0 - bitangent * dH_dB * 50.0); 
    // *50.0 es un factor de fuerza empírico, ajústalo si se ve muy suave o muy duro
    
    return n;
}

vec3 get_direction(uvec3 id, float size) {
    vec2 uv = (vec2(id.xy) + 0.5) / size;
    uv = uv * 2.0 - 1.0;
    uint face = id.z;
    vec3 dir;
    if (face == 0) dir = vec3(1.0, -uv.y, -uv.x);
    else if (face == 1) dir = vec3(-1.0, -uv.y, uv.x);
    else if (face == 2) dir = vec3(uv.x, 1.0, uv.y);
    else if (face == 3) dir = vec3(uv.x, -1.0, -uv.y);
    else if (face == 4) dir = vec3(uv.x, -uv.y, 1.0);
    else dir = vec3(-uv.x, -uv.y, -1.0);
    return normalize(dir);
}

void main() {
    uvec3 id = gl_GlobalInvocationID;
    float resolution = params.res_offset.x;

    if (id.x >= uint(resolution) || id.y >= uint(resolution)) {
        return;
    }

    vec3 dir = get_direction(id, resolution);

    // 1. ALTURA
    float h_val = get_terrain_height(dir);
    h_val = clamp(h_val, 0.0, 1.0);
    float amplitude = params.curve_params.z; 
    float h_final = h_val * amplitude;
    
    imageStore(height_map, ivec3(id), vec4(h_final, 0.0, 0.0, 1.0));

    // 2. NORMALES (¡ESTO FALTABA!)
    vec3 normal = compute_normal(dir, resolution);
    imageStore(normal_map, ivec3(id), vec4(normal, 1.0));

    // 3. VECTOR FIELD
    vec3 flow = curl_noise(dir * params.noise_settings.x * 2.0);
    flow = normalize(flow - dot(flow, dir) * dir); 
    imageStore(vector_field, ivec3(id), vec4(flow, 1.0));

    // 4. STATS
    int h_fixed = int(h_final * FIXED_POINT_SCALE);
    atomicMin(stats.min_h_fixed, h_fixed);
    atomicMax(stats.max_h_fixed, h_fixed);
}