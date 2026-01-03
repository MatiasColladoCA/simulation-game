#[compute]
#version 450

// Tamaño del grupo de trabajo
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Binding 0: Parámetros (Coincide con TerrainBaker.cs)
layout(set = 0, binding = 0, std430) buffer Params {
    float radius;
    float resolution;
    float noise_scale;
    float noise_height;
} p;

// Binding 1: POIs (Coincide con EnvironmentManager.cs)
layout(set = 0, binding = 1, std430) buffer POIs {
    vec4 data[16];
} pois;

// Binding 2: Textura de Salida (Cubo tratado como Array de 2D)
layout(set = 0, binding = 2, rgba16f) uniform image2DArray influence_texture;

// --- FUNCIÓN SEGURA DE COORDENADAS ---
vec3 tex_to_world(uvec3 id, float res) {
    // 1. Convertir pixel a UV [0 a 1]
    vec2 uv = (vec2(id.xy) + 0.5) / res;
    
    // 2. Convertir a NDC [-1 a 1]
    vec2 ndc = uv * 2.0 - 1.0;
    
    // Invertir Y para corregir la orientación de Vulkan
    ndc.y = -ndc.y; 

    uint face = id.z;
    vec3 v = vec3(0.0);

    // Comparación simple de enteros (sin sufijos 'u' para máxima compatibilidad)
    if(face == 0)      v = vec3(1.0, ndc.y, -ndc.x);  // +X
    else if(face == 1) v = vec3(-1.0, ndc.y, ndc.x);  // -X
    else if(face == 2) v = vec3(ndc.x, 1.0, -ndc.y);  // +Y
    else if(face == 3) v = vec3(ndc.x, -1.0, ndc.y);  // -Y
    else if(face == 4) v = vec3(ndc.x, ndc.y, 1.0);   // +Z
    else if(face == 5) v = vec3(-ndc.x, ndc.y, -1.0); // -Z
    
    return normalize(v);
}

void main() {
    uvec3 id = gl_GlobalInvocationID;
    
    // Seguridad contra NaNs si la resolución llega mal
    float safe_res = p.resolution;
    if (safe_res < 1.0) safe_res = 1024.0;
    
    // Límites
    if (id.x >= uint(safe_res) || id.y >= uint(safe_res)) return;

    vec3 world_dir = tex_to_world(id, safe_res);
    float total_influence = 0.0;

    // Iterar POIs
    for(int i = 0; i < 16; i++) {
        // data.w es el radio. Si es 0 o negativo, saltar.
        if (pois.data[i].w <= 0.0001) continue;

        vec3 poi_dir = normalize(pois.data[i].xyz);
        float poi_range = pois.data[i].w; 
        
        float dist = distance(world_dir, poi_dir);
        
        // Cálculo de influencia con protección contra división por cero
        float influence = 1.0 - smoothstep(0.0, max(0.0001, poi_range), dist);
        
        total_influence += influence;
    }

    total_influence = clamp(total_influence, 0.0, 1.0);

    // Fondo: Celeste Oscuro (Océano de datos vacío)
    vec3 col_bg = vec3(0.0, 0.2, 0.5); 
    
    // Borde de influencia: Amarillo
    vec3 col_mid = vec3(1.0, 0.8, 0.0);
    
    // Centro del POI: Blanco Brillante
    vec3 col_core = vec3(1.0, 1.0, 1.0);

    vec3 final_rgb;
    
    // Gradiente de 3 colores para que se vea más lindo
    if (total_influence < 0.5) {
        // De Celeste a Amarillo
        final_rgb = mix(col_bg, col_mid, total_influence * 2.0);
    } else {
        // De Amarillo a Blanco
        final_rgb = mix(col_mid, col_core, (total_influence - 0.5) * 2.0);
    }
    
    // Guardar
    imageStore(influence_texture, ivec3(id), vec4(final_rgb, total_influence));
}