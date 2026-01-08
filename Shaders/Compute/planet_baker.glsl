#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// BINDINGS (Tus bindings originales intactos)
layout(set = 0, binding = 0, r32f) restrict writeonly uniform imageCube height_map;
layout(set = 0, binding = 1, rgba16f) restrict writeonly uniform imageCube vector_field;
layout(set = 0, binding = 2, rgba16f) restrict writeonly uniform imageCube normal_map;

layout(set = 0, binding = 3, std430) restrict buffer StatsBuffer { 
    int min_h_fixed; 
    int max_h_fixed; 
} stats;

// UNIFORMS (Mapeado a tu estructura C#)
// noise_settings: x=Scale, y=Persistence, z=Lacunarity, w=Octaves
// curve_params:   x=OceanFloor(0-1), y=MtnStrength, z=Amplitude, w=Radius
// global_offset:  xyz=Seed
layout(set = 0, binding = 4, std140) uniform BakeParams {
    vec4 noise_settings; 
    vec4 curve_params;   
    vec4 global_offset;  
    vec4 pad_center;     
    vec4 res_offset;     
    vec4 pad_uv;   
} params;

const float FIXED_POINT_SCALE = 100000.0;

// --- TOOLKIT DE RUIDO (Standard Simplex) ---
vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x*34.0)+1.0)*x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoise(vec3 v) { 
  const vec2  C = vec2(1.0/6.0, 1.0/3.0) ;
  const vec4  D = vec4(0.0, 0.5, 1.0, 2.0);
  vec3 i  = floor(v + dot(v, C.yyy) );
  vec3 x0 = v - i + dot(i, C.xxx) ;
  vec3 g = step(x0.yzx, x0.xyz);
  vec3 l = 1.0 - g;
  vec3 i1 = min( g.xyz, l.zxy );
  vec3 i2 = max( g.xyz, l.zxy );
  vec3 x1 = x0 - i1 + C.xxx;
  vec3 x2 = x0 - i2 + C.yyy;
  vec3 x3 = x0 - D.yyy;
  i = mod289(i); 
  vec4 p = permute( permute( permute( 
             i.z + vec4(0.0, i1.z, i2.z, 1.0 ))
           + i.y + vec4(0.0, i1.y, i2.y, 1.0 )) 
           + i.x + vec4(0.0, i1.x, i2.x, 1.0 ));
  float n_ = 0.142857142857;
  vec3  ns = n_ * D.wyz - D.xzx;
  vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
  vec4 x_ = floor(j * ns.z);
  vec4 y_ = floor(j - 7.0 * x_ );
  vec4 x = x_ *ns.x + ns.yyyy;
  vec4 y = y_ *ns.x + ns.yyyy;
  vec4 h = 1.0 - abs(x) - abs(y);
  vec4 b0 = vec4( x.xy, y.xy );
  vec4 b1 = vec4( x.zw, y.zw );
  vec4 s0 = floor(b0)*2.0 + 1.0;
  vec4 s1 = floor(b1)*2.0 + 1.0;
  vec4 sh = -step(h, vec4(0.0));
  vec4 a0 = b0.xzyw + s0.xzyw*sh.xxyy ;
  vec4 a1 = b1.xzyw + s1.xzyw*sh.zzww ;
  vec3 p0 = vec3(a0.xy,h.x);
  vec3 p1 = vec3(a0.zw,h.y);
  vec3 p2 = vec3(a1.xy,h.z);
  vec3 p3 = vec3(a1.zw,h.w);
  vec4 norm = taylorInvSqrt(vec4(dot(p0,p0), dot(p1,p1), dot(p2, p2), dot(p3,p3)));
  p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
  vec4 m = max(0.6 - vec4(dot(x0,x0), dot(x1,x1), dot(x2,x2), dot(x3,x3)), 0.0);
  m = m * m;
  return 42.0 * dot( m*m, vec4( dot(p0,x0), dot(p1,x1), dot(p2,x2), dot(p3,x3) ) );
}

// --- FBM SIMPLE (Para continentes base) ---
float simple_fbm(vec3 x, int octaves, float persistence, float lacunarity) {
    float v = 0.0;
    float a = 1.0; // Amplitud inicial
    float f = 1.0; // Frecuencia inicial
    // Normalizar la suma de amplitudes para que regrese -1 a 1 aprox
    float max_amp = 0.0;
    
    for (int i = 0; i < octaves; ++i) {
        v += snoise(x * f) * a;
        max_amp += a;
        a *= persistence;
        f *= lacunarity;
    }
    return v / max_amp;
}

// --- RIDGED FBM (Para montañas afiladas) ---
// Extraído del estilo "RidgeNoise" de Lague
float ridge_fbm(vec3 x, int octaves, float persistence, float lacunarity) {
    float v = 0.0;
    float a = 1.0;
    float f = 1.0;
    float max_amp = 0.0;
    
    for (int i = 0; i < octaves; ++i) {
        float n = 1.0 - abs(snoise(x * f)); // Invertir y valor absoluto = Crestas
        n = n * n; // Afilar crestas (Cuadrado)
        v += n * a;
        max_amp += a;
        a *= persistence;
        f *= lacunarity;
    }
    return v / max_amp;
}

// --- LOGICA PRINCIPAL (Estilo Lague) ---
float get_terrain_height(vec3 dir) {
    // 1. Extraer Parámetros
    vec3  pos         = dir + params.global_offset.xyz;
    float scale       = params.noise_settings.x; // NoiseScale
    float persistence = params.noise_settings.y; // Usaremos fijo 0.5 o variable
    float lacunarity  = params.noise_settings.z; // Usaremos fijo 2.0 o variable
    int   octaves     = 6; // Calidad fija o params.noise_settings.w
    
    // Parámetros de "Estilo"
    float ocean_floor = params.curve_params.x; // OceanFloorLevel
    float mtn_strength= params.curve_params.y; // WeightMultiplier
    // Usaremos MountainRoughness para controlar qué tan "picosas" son las montañas
    
    // 2. Continentes (Base Shape)
    // Baja frecuencia, define tierra vs mar
    float continent_shape = simple_fbm(pos * scale, octaves, 0.5, 2.0);
    
    // Mover rango de -1..1 a 0..1 aprox
    float h = continent_shape * 0.5 + 0.5;
    
    // 3. Océanos (Aplanado)
    // Si está por debajo del nivel del mar, aplanarlo
    if (h < ocean_floor) {
        // Suavizar un poco la transición al fondo marino
        h = mix(0.0, ocean_floor, smoothstep(0.0, ocean_floor, h));
    } else {
        // 4. Montañas (Solo en tierra)
        // Máscara: ¿Dónde permitimos montañas?
        // Usamos una versión offset del ruido para que no se alinee perfectamente con los continentes
        float mtn_mask = simple_fbm((pos + vec3(12.5)) * scale * 2.0, 3, 0.5, 2.0);
        mtn_mask = smoothstep(0.15, 0.55, mtn_mask * 0.5 + 0.5); // 0 = llanura, 1 = zona montañosa
        
        // Ruido de Crestas (Ridge)
        // Mayor frecuencia (scale * 3.0) para detalle
        float ridges = ridge_fbm(pos * scale * 3.0, 5, 0.5, 2.0);
        
        // Sumar montañas a la base, modulado por la máscara y la fuerza
        h += ridges * mtn_mask * mtn_strength * 0.5;
    }

    return h; // Devuelve 0-1 (puede pasarse de 1 con las montañas, está bien)
}

// Exagera los valores altos (crea picos) y aplana los bajos
float ease_in_cubic(float x) {
    return x * x * x;
}

// Suaviza la transición entre dos valores
float smooth_blend(float edge0, float edge1, float x) {
    return clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
}

// Helper para Curl Noise (Flujo de agua/aire)
vec3 curl_noise(vec3 p) {
    const float e = 0.01; 
    vec3 dx = vec3(e, 0.0, 0.0); 
    vec3 dy = vec3(0.0, e, 0.0); 
    vec3 dz = vec3(0.0, 0.0, e);
    
    float n_x = snoise(p + dy) - snoise(p - dy);
    float n_y = snoise(p + dz) - snoise(p - dz);
    float n_z = snoise(p + dx) - snoise(p - dx);
    return normalize(vec3(n_x, n_y, n_z));
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
    
    if (id.x >= uint(resolution) || id.y >= uint(resolution)) return;

    vec3 dir = get_direction(id, resolution);

    // 1. ALTURA BASE
    float h_01 = get_terrain_height(dir);
    float amplitude = params.curve_params.z; 
    float radius = params.curve_params.w;
    
    float h_final = h_01 * amplitude;
    
    imageStore(height_map, ivec3(id), vec4(h_final, 0.0, 0.0, 1.0));

    // 2. NORMALES (Método de diferencias finitas en esfera)
    // Calculamos 3 muestras muy cercanas para sacar la pendiente
    float eps = 1.0 / resolution; 
    vec3 tangent = normalize(cross(dir, vec3(0,1,0)));
    if (length(tangent) < 0.001) tangent = normalize(cross(dir, vec3(1,0,0)));
    vec3 bitangent = normalize(cross(dir, tangent));
    
    float h_right = get_terrain_height(normalize(dir + tangent * eps)) * amplitude;
    float h_up    = get_terrain_height(normalize(dir + bitangent * eps)) * amplitude;

    vec3 p_c = dir * (radius + h_final);
    vec3 p_r = normalize(dir + tangent * eps) * (radius + h_right);
    vec3 p_u = normalize(dir + bitangent * eps) * (radius + h_up);

    vec3 n_geom = normalize(cross(p_u - p_c, p_r - p_c)); // Ojo: orden del cross product define dirección
    imageStore(normal_map, ivec3(id), vec4(n_geom, 1.0));

    // 3. VECTOR FIELD (Opcional, basado en curl noise)
    vec3 flow = curl_noise(dir * params.noise_settings.x * 2.0);
    // Proyectar sobre la superficie
    flow = normalize(flow - dot(flow, n_geom) * n_geom);
    imageStore(vector_field, ivec3(id), vec4(flow, 1.0));
    
    // 4. STATS BUFFER
    int h_fixed = int(h_final * FIXED_POINT_SCALE);
    atomicMin(stats.min_h_fixed, h_fixed);
    atomicMax(stats.max_h_fixed, h_fixed);
}