#[compute]
#version 450

layout(local_size_x = 32, local_size_y = 32, local_size_z = 1) in;

layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;
layout(set = 0, binding = 1, rgba16f) writeonly uniform imageCube vector_field;
layout(set = 0, binding = 2, rgba16f) writeonly uniform imageCube normal_map;

layout(push_constant) uniform Params {
    float planet_radius;
    float noise_scale;  // Usar 1.0 como base
    float noise_height; // Usar ej. 0.05
    uint resolution;
} params;

// --- 1. TOOLKIT DE RUIDO (Simplex 3D Optimizado) ---
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

// --- 2. FRACTALES DE RUIDO ---

// FBM Standard (Colinas, detalle suelo)
float fbm(vec3 x, int octaves, float freq, float gain) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < octaves; ++i) {
        v += a * snoise(x * freq);
        x += vec3(12.34); // Shift para evitar artefactos en el origen
        freq *= 2.0;
        a *= gain;
    }
    return v;
}

// Ridged Multi (Montañas picosas)
float ridged(vec3 x, int octaves, float freq) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < octaves; ++i) {
        float n = snoise(x * freq);
        n = 1.0 - abs(n);
        n = n * n * n; // Más afilado (potencia cúbica)
        v += a * n;
        freq *= 2.0;
        a *= 0.5;
    }
    return v;
}

// Billow Noise (Para ríos/cañones - es el inverso del Ridged suave)
float billow(vec3 x, int octaves, float freq) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < octaves; ++i) {
        float n = snoise(x * freq);
        v += a * abs(n);
        freq *= 2.0;
        a *= 0.5;
    }
    return v;
}

// --- 3. LÓGICA DE TERRENO AVANZADA ---

float get_terrain_height(vec3 dir) {
    // A. Domain Warping (Romper la grilla del ruido)
    // Esto evita que el ruido se vea alineado y artificial
    vec3 q = dir * params.noise_scale;
    vec3 warp_vec = vec3(
        fbm(q, 2, 0.5, 0.5),
        fbm(q + vec3(5.2), 2, 0.5, 0.5),
        fbm(q + vec3(1.3), 2, 0.5, 0.5)
    );
    vec3 p = dir * params.noise_scale + warp_vec * 0.15; // 0.15 es la fuerza de distorsión

    // B. Máscara Continental (Forma base del mundo)
    // Frecuencia muy baja = formas grandes
    float continent_shape = fbm(p, 4, 1.0, 0.5); 
    
    // C. Máscara de Montañas (Independiente del continente)
    // Define DÓNDE habrá montañas si hay tierra
    float mountain_density = fbm(p + vec3(100.0), 3, 2.0, 0.5);
    
    // D. Detalle de Terreno (Llanuras/Suelo)
    // Frecuencia media/alta, amplitud baja.
    // IMPORTANTE: Esto se aplica en todo el planeta para evitar zonas "lisas" (borrosas)
    float ground_detail = fbm(p * 5.0, 3, 4.0, 0.5) * 0.05;

    // --- MEZCLA DE BIOMAS ---
    
    float h = continent_shape;
    
    // Nivel del mar base (ej. 0.0). Ajustamos para definir cuánta agua hay.
    // Si h > 0.1 es tierra, si no es mar.
    float sea_level = 0.1;
    
    if (h > sea_level) {
        // --- ESTAMOS EN TIERRA ---
        
        // Suavizar la transición costa/tierra
        float land_factor = smoothstep(sea_level, sea_level + 0.1, h);
        
        // 1. Decidir si es Montaña o Llanura
        // Si mountain_density es alto -> Montaña. Si es bajo -> Llanura.
        float is_mountain = smoothstep(0.2, 0.6, mountain_density);
        
        // Generar montañas reales
        float mtns = ridged(p, 5, 5.0) * 0.8; // 0.8 es altura máxima montañas
        
        // Generar llanuras (suaves pero con ruido)
        float plains = ground_detail * 2.0; // Poco relieve
        
        // Mezclar según la máscara
        float land_shape = mix(plains, mtns, is_mountain);
        
        // Añadir a la base continental
        h += land_shape * land_factor;
        
        // 2. Ríos
        // Usamos billow noise. Si el valor es muy bajo, cavamos un río.
        float river_noise = billow(p, 4, 3.0);
        float river_mask = smoothstep(0.1, 0.15, river_noise); // Canales estrechos
        
        // Solo cavar ríos en llanuras (evitar cortar picos de montañas surrealistamente)
        float river_strength = (1.0 - is_mountain) * 0.5; 
        
        // "Excavar" el río invirtiendo la máscara
        h = mix(h - 0.05, h, clamp(river_mask + (1.0 - river_strength), 0.0, 1.0));
    } 
    else {
        // --- ESTAMOS EN EL MAR ---
        // Añadir detalle al fondo marino para que no se vea "liso/borroso"
        // Lecho marino rugoso + fosas oceánicas
        h += ground_detail; 
        
        // Fosas profundas (ruido ridged invertido)
        float trench = ridged(p, 2, 2.0);
        if (trench > 0.8) h -= (trench - 0.8) * 0.5;
    }

    // Normalizar salida para que el shader de fragmentos tenga rango útil
    return h * 0.5 + 0.5;
}

// --- 4. CURL NOISE PARA EL AGUA ---
vec3 curl_noise(vec3 p) {
    const float e = 0.1; 
    vec3 dx = vec3(e, 0.0, 0.0); vec3 dy = vec3(0.0, e, 0.0); vec3 dz = vec3(0.0, 0.0, e);
    float n_x = snoise(p + dy) - snoise(p - dy);
    float n_y = snoise(p + dz) - snoise(p - dz);
    float n_z = snoise(p + dx) - snoise(p - dx);
    return normalize(vec3(n_x, n_y, n_z));
}

// Helper de dirección
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
    if (id.x >= params.resolution || id.y >= params.resolution) return;

    vec3 dir = get_direction(id, float(params.resolution));

    // 1. ALTURA
    float h_val = get_terrain_height(dir);
    imageStore(height_map, ivec3(id), vec4(h_val, 0.0, 0.0, 1.0));

    // 2. NORMALES (Calculadas con delta muy pequeño para capturar detalle fino)
    float eps = 1.0 / float(params.resolution); 
    vec3 tangent = normalize(cross(dir, vec3(0,1,0)));
    if (length(tangent) < 0.001) tangent = normalize(cross(dir, vec3(1,0,0)));
    vec3 bitangent = normalize(cross(dir, tangent));
    
    float h_center = h_val;
    float h_right  = get_terrain_height(normalize(dir + tangent * eps));
    float h_up     = get_terrain_height(normalize(dir + bitangent * eps));

    float r = params.planet_radius;
    float amp = params.noise_height;
    
    vec3 p_c = dir * (r + h_center * amp);
    vec3 p_r = normalize(dir + tangent * eps) * (r + h_right * amp);
    vec3 p_u = normalize(dir + bitangent * eps) * (r + h_up * amp);

    vec3 n_geom = normalize(cross(p_r - p_c, p_u - p_c));
    imageStore(normal_map, ivec3(id), vec4(n_geom, 1.0));

    // 3. FLOW (Mezcla de Curl y Pendiente)
    vec3 flow_base = curl_noise(dir * params.noise_scale * 4.0);
    
    // Gradiente para que el agua "caiga" por montañas
    vec3 gradient = (tangent * (h_right - h_center) + bitangent * (h_up - h_center)) / eps;
    float slope = length(gradient);
    
    // Si la pendiente es fuerte, el agua cae. Si es plana, fluye orgánicamente.
    float gravity_factor = smoothstep(0.001, 0.05, slope);
    vec3 flow_final = mix(flow_base, -normalize(gradient + vec3(0.0001)), gravity_factor);
    
    flow_final = flow_final - dot(flow_final, n_geom) * n_geom;
    flow_final = normalize(flow_final);
    
    imageStore(vector_field, ivec3(id), vec4(flow_final, 1.0));
}