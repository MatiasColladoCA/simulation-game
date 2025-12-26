#[compute]
#version 450

// Ejecutamos en 2D (X, Y) y usamos Z para la cara del cubo (0 a 5)
layout(local_size_x = 32, local_size_y = 32, local_size_z = 1) in;

// Salida: Un Cubemap (ImageCube) donde podemos escribir
layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;

layout(push_constant) uniform Params {
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint resolution; // Tamaño de una cara (ej. 1024)
} params;

// --- TUS FUNCIONES DE RUIDO (Copiadas para consistencia) ---
float hash(vec3 p) {
    p  = fract( p*0.3183099 + .1 );
    p *= 17.0;
    return fract( p.x*p.y*p.z*(p.x+p.y+p.z) );
}
float noise( in vec3 x ) {
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f*f*(3.0-2.0*f);
    return mix(mix(mix( hash(i+vec3(0,0,0)), hash(i+vec3(1,0,0)),f.x), mix( hash(i+vec3(0,1,0)), hash(i+vec3(1,1,0)),f.x),f.y), mix(mix( hash(i+vec3(0,0,1)), hash(i+vec3(1,0,1)),f.x), mix( hash(i+vec3(0,1,1)), hash(i+vec3(1,1,1)),f.x),f.y),f.z);
}
float fbm(vec3 x) {
    float v = 0.0; float a = 0.5; vec3 shift = vec3(100.0);
    for (int i = 0; i < 5; ++i) { v += a * noise(x); x = x * 2.0 + shift; a *= 0.5; }
    return v;
}

// Función crítica: Convertir (u, v, cara) -> Vector Dirección 3D Normalizado
vec3 get_direction(uvec3 id, float size) {
    vec2 uv = (vec2(id.xy) + 0.5) / size; // [0, 1]
    uv = uv * 2.0 - 1.0;                  // [-1, 1]
    
    // Invertir Y para coincidir con el sistema de coordenadas de Vulkan/Godot
    // uv.y *= -1.0; 

    vec3 dir;
    uint face = id.z;

    // Mapeo estándar de Cubemaps
    if (face == 0) dir = vec3(1.0, -uv.y, -uv.x);  // +X (Right)
    else if (face == 1) dir = vec3(-1.0, -uv.y, uv.x);   // -X (Left)
    else if (face == 2) dir = vec3(uv.x, 1.0, uv.y);     // +Y (Top)
    else if (face == 3) dir = vec3(uv.x, -1.0, -uv.y);   // -Y (Bottom)
    else if (face == 4) dir = vec3(uv.x, -uv.y, 1.0);    // +Z (Front)
    else dir = vec3(-uv.x, -uv.y, -1.0);                 // -Z (Back)

    return normalize(dir);
}

void main() {
    uvec3 id = gl_GlobalInvocationID;
    
    // Validar límites
    if (id.x >= params.resolution || id.y >= params.resolution) return;

    // 1. Obtener dirección 3D para este píxel
    vec3 dir = get_direction(id, float(params.resolution));

    // 2. Calcular FBM (Igual que hacían tus agentes antes)
    float n = fbm(dir * params.noise_scale);
    
    // 3. Escribir al Cubemap
    // imageStore con ivec3(x, y, cara) escribe en la capa correcta
    imageStore(height_map, ivec3(id), vec4(n, 0.0, 0.0, 1.0));
}