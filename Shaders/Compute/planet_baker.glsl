#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// BINDINGS (Tus bindings originales intactos)
layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;
layout(set = 0, binding = 1, rgba16f) writeonly uniform imageCube vector_field;
layout(set = 0, binding = 2, rgba16f) writeonly uniform imageCube normal_map;
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
// --- FBM SIMPLE CON DOMINIO OFFSET (Para continentes base) ---
// --- FBM SIMPLE CON DOMINIO OFFSET ADITIVO (Más estable y probado) ---
float simple_fbm(vec3 x, int octaves, float persistence, float lacunarity) {
    float v = 0.0;
    float a = 1.0; // Amplitud inicial
    float f = 1.0; // Frecuencia inicial
    float max_amp = 0.0;
    
    for (int i = 0; i < octaves; ++i) {
        v += snoise(x * f) * a;
        max_amp += a;
        a *= persistence;
        f *= lacunarity;
        // <-- CAMBIO CLAVE: Offset aditivo simple y robusto.
        // Esto rompe la correlación entre octavas de forma más segura.
        x += vec3(1.23, 4.56, 7.89);
    }
    return v / max_amp;
}

// --- RIDGED FBM (Para montañas afiladas) ---
// Extraído del estilo "RidgeNoise" de Lague
// --- RIDGED FBM CON DOMINIO OFFSET (Para montañas afiladas) ---
float ridge_fbm(vec3 x, int octaves, float persistence, float lacunarity, float sharpness) {
    float v = 0.0;
    float a = 1.0;
    float f = 1.0;
    float max_amp = 0.0;
    
    for (int i = 0; i < octaves; ++i) {
        float n = 1.0 - abs(snoise(x * f)); // Invertir y valor absoluto = Crestas
        n = clamp(n, 0.0, 1.0);
        n = n * n * n; // Afilar crestas
        v += n * a;
        max_amp += a;
        a *= persistence;
        f *= lacunarity;
        // <-- AÑADIR ESTA LÍNEA TAMBIÉN
        x = x * lacunarity + vec3(9.87, 6.54, 3.21);
    }
    return v / max_amp;
}

// // FBM Estándar
// float simple_fbm(vec3 x, int octaves, float persistence, float lacunarity) {
//     float v = 0.0;
//     float a = 1.0;
//     float f = 1.0;
//     float max_amp = 0.0;
//     for (int i = 0; i < octaves; ++i) {
//         v += snoise(x * f) * a;
//         max_amp += a;
//         a *= persistence;
//         f *= lacunarity;
//     }
//     return v / max_amp;
// }

// Ruido "Rocoso" (Ridged) con control de agudeza
float rigid_noise(vec3 x, int octaves, float persistence, float lacunarity, float sharpness) {
    float v = 0.0;
    float a = 1.0;
    float f = 1.0;
    float max_amp = 0.0;
    for (int i = 0; i < octaves; ++i) {
        float n = 1.0 - abs(snoise(x * f)); 
        n = clamp(n, 0.0, 1.0);  // ← CRÍTICO: evita pow(negativo)
        n = n * n * n;           // ← polinomio fijo reemplaza pow(variable)
        v += n * a;
        max_amp += a;
        a *= persistence;
        f *= lacunarity;
    }
    return v / max_amp;
}


// --- LOGICA PRINCIPAL (Estilo Lague) ---
// --- LOGICA PRINCIPAL (Arquitectura de Biomas, como el shader viejo) ---
// --- LOGICA PRINCIPAL (Arquitectura de Biomas, con anti-aliasing) ---
// --- LOGICA PRINCIPAL (Con Frecuencias Desacopladas) ---
// --- LOGICA PRINCIPAL (Con Frecuencias Desacopladas - Versión Limpia) ---
// --- LOGICA PRINCIPAL (Arquitectura de Biomas, como el shader viejo) ---
float get_terrain_height(vec3 dir) {
    vec3 pos = dir + params.global_offset.xyz;
    float scale = params.noise_settings.x;
    float warp_str = params.noise_settings.w;

    // --- 1. DOMAIN WARPING (igual que antes, está bien) ---
    vec3 warp_noise = vec3(snoise(pos * scale * 0.5), snoise(pos * scale * 0.5 + vec3(5.2)), snoise(pos * scale * 0.5 + vec3(1.3)));
    vec3 p = pos + warp_noise * warp_str;

    // --- 2. CALCULAR LAS CAPAS DE RUIDO INDEPENDIENTEMENTE ---
    // Usamos los parámetros de tu C#
    float d_freq = params.detail_params.x;
    
    // Base continental (forma general)
    float continent_shape = simple_fbm(p * scale, 5, 0.5, 2.0);
    float h = continent_shape;

    // Densidad de montañas (dónde van a aparecer)
    float mountain_density = simple_fbm((p + vec3(100.0)) * scale * 1.5, 3, 0.5, 2.0);

    // Detalle de suelo (para llanuras)
    float ground_detail = simple_fbm(p * scale * d_freq * 0.5, 3, 0.5, 2.0) * 0.05; // Menor amplitud

    // --- 3. LÓGICA DE BIOMAS (Mezcla, no adición) ---
    float sea_level = params.curve_params.x; // Usamos OceanFloorLevel como sea_level

    if (h > sea_level) {
        // --- ESTAMOS EN TIERRA ---
        
        // Suavizar la transición costa/tierra
        float land_factor = smoothstep(sea_level, sea_level + 0.1, h);

        // Crear la máscara de bioma (Montaña vs Llanura) usando tus parámetros
        float mask_start = params.detail_params.z; // 0.6
        float mask_end = params.detail_params.w;   // 0.75
        float is_mountain = smoothstep(mask_start, mask_end, mountain_density);
        
        // Generar los terrenos del bioma
        float ridges = rigid_noise(p * scale * d_freq, 6, 0.5, 2.0, params.detail_params.y);
        float ridges_sharp = ridges * ridges; // Afilar un poco, sin ser tan agresivo
        
        float mtns = ridges_sharp * params.curve_params.y; // Usamos WeightMultiplier como fuerza
        
        float plains = ground_detail * 2.0;

        // MEZCLAR LOS BIOMAS, NO SUMARLOS
        float land_shape = mix(plains, mtns, is_mountain);
        
        // Añadir el resultado mezclado a la base continental
        h += land_shape * land_factor;

    } else {
        // --- ESTAMOS EN EL MAR ---
        // Añadir detalle al fondo marino
        h += ground_detail; 
    }

    // Normalizar salida a [0, 1]
    return h * 0.5 + 0.5;
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

    if (id.x >= uint(resolution) || id.y >= uint(resolution)) {
        return;
    }

    // Dirección esférica estable desde cubemap
    vec3 dir = get_direction(id, resolution);

    // 1. ALTURA (única fuente geométrica)
    float h_val = get_terrain_height(dir);
    h_val = clamp(h_val, 0.0, 1.0);  // ← AGREGAR: evita overflow amplitude
    float amplitude = params.curve_params.z; // Esto está correcto, pero ahora es exclusivo para altura.

    float h_final = h_val * amplitude;
    
    imageStore(
        height_map,
        ivec3(id),
        vec4(h_final, 0.0, 0.0, 1.0)
    );

    // 2. VECTOR FIELD (independiente de normales)
    vec3 flow = curl_noise(dir * params.noise_settings.x * 2.0);
    flow = normalize(flow - dot(flow, dir) * dir); // proyectado al plano tangente

    imageStore(
        vector_field,
        ivec3(id),
        vec4(flow, 1.0)
    );

    // 3. STATS
    h_final = clamp(h_final, -1000.0, 1000.0);  // ← AGREGAR: protege atomic
    int h_fixed = int(h_final * FIXED_POINT_SCALE);
    h_final = clamp(h_final, -1000.0, 1000.0);  // ← AGREGAR: protege atomic
    atomicMin(stats.min_h_fixed, h_fixed);
    atomicMax(stats.max_h_fixed, h_fixed);
}
