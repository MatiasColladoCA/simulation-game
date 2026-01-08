#[compute]
#version 450

// 8x8 hilos por grupo (coincide con tu C#: resolution / 8.0f)
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// --- BINDING 0: PARÁMETROS DEL PLANETA (Uniform Buffer - std140) ---
// Debe coincidir EXACTAMENTE con el orden de escritura de PlanetBaker.cs
// Usamos vec4 para garantizar la alineación de 16 bytes de std140 y evitar errores de padding.
layout(set = 0, binding = 0, std140) uniform PlanetParamsData {
    vec4 noise_settings; // x: scale, y: pers, z: lac, w: octaves
    vec4 curve_params;   // x: exp, y: rough, z: height, w: RADIUS (Importante)
    vec4 face_up_pad;    // xyz: face_up, w: pad
    vec4 center_pad;     // xyz: center, w: pad
    vec4 res_offset;     // xy: resolution, zw: offset
    vec4 uv_scale_pad;   // x: uv_scale, yzw: pad
} params;

// --- BINDING 1: LISTA DE POIS (Storage Buffer - std430) ---
// Coincide con EnvironmentManager.cs
layout(set = 0, binding = 1, std430) restrict readonly buffer PoiBuffer {
    // xyz: Dirección Normalizada, w: Radio de Influencia Normalizado (0.0 a 1.0 relativo al planeta)
    vec4 data[]; 
} pois;

// --- BINDING 2: TEXTURA DE SALIDA (Cubemap) ---
layout(set = 0, binding = 2, rgba16f) restrict writeonly uniform imageCube influence_map;

// --- UTILIDADES ---

// Función para obtener la dirección 3D normalizada de un texel de cubemap
vec3 get_cubemap_direction(ivec2 pos, int face, vec2 resolution) {
    vec2 uv = (vec2(pos) + 0.5) / resolution; // 0..1
    uv = uv * 2.0 - 1.0; // -1..1

    vec3 dir;
    // Mapeo estándar de Cubemap (OpenGL/Vulkan convention)
    switch(face) {
        case 0: dir = vec3(1.0, -uv.y, -uv.x); break;  // +X
        case 1: dir = vec3(-1.0, -uv.y, uv.x); break;  // -X
        case 2: dir = vec3(uv.x, 1.0, uv.y); break;    // +Y
        case 3: dir = vec3(uv.x, -1.0, -uv.y); break;  // -Y
        case 4: dir = vec3(uv.x, -uv.y, 1.0); break;   // +Z
        case 5: dir = vec3(-uv.x, -uv.y, -1.0); break; // -Z
    }
    return normalize(dir);
}

void main() {
    // Coordenadas globales: XY = Pixel, Z = Cara del Cubo (0-5)
    ivec3 id = ivec3(gl_GlobalInvocationID);
    vec2 res = params.res_offset.xy;

    // Boundary check
    if (id.x >= int(res.x) || id.y >= int(res.y)) return;

    // 1. Obtener la dirección en el espacio mundo de este píxel del cubemap
    vec3 pixel_dir = get_cubemap_direction(id.xy, id.z, res);

    // 2. Acumular Influencia
    float total_heat = 0.0;
    vec3 color_acc = vec3(0.0);

    // Recorremos todos los POIs (data.length() funciona en buffers std430 no acotados)
    uint count = pois.data.length();
    
    for (uint i = 0; i < count; i++) {
        vec4 poi = pois.data[i];
        vec3 poi_dir = poi.xyz;       // Dirección del POI
        float influence_rad = poi.w;  // Radio normalizado (ángulo aprox)

        // Si el radio es 0 o negativo, es un slot vacío o padding
        if (influence_rad <= 0.0001) continue;

        // Calcular ángulo entre el píxel actual y el centro del POI
        // Dot product: 1.0 = mismo sitio, 0.0 = 90 grados
        float dot_val = dot(pixel_dir, poi_dir);
        
        // Convertir a distancia angular (más preciso para esferas)
        // clamp para evitar errores numéricos fuera de -1..1
        float angle = acos(clamp(dot_val, -1.0, 1.0));

        // Si estamos dentro del radio de influencia
        if (angle < influence_rad) {
            // Falloff suave (Smoothstep): 1.0 en el centro, 0.0 en el borde
            // Invertimos (1.0 - t) para que el centro sea caliente
            float t = smoothstep(0.0, influence_rad, angle);
            float heat = 1.0 - t; 
            
            // Acumular (Suma simple, se puede usar max() para hard blend)
            total_heat += heat;
            
            // Debug color (rojo por defecto, podrías pasar color en el buffer)
            color_acc += vec3(1.0, 0.2, 0.1) * heat;
        }
    }

    // Saturar para no exceder 1.0
    total_heat = clamp(total_heat, 0.0, 1.0);
    color_acc = clamp(color_acc, 0.0, 1.0);

    // 3. Escribir Resultado
    // RGB = Color visual, A = Fuerza de influencia (usada por lógica de juego)
    imageStore(influence_map, id, vec4(color_acc, total_heat));
}