#[compute]
#version 450

layout(local_size_x = 32, local_size_y = 32, local_size_z = 1) in;

// Salida 0: Altura (R)
layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;

// Salida 1: Campo Vectorial
layout(set = 0, binding = 1, rgba16f) writeonly uniform imageCube vector_field;

// Salida 2: Normal map (RGB = normal en espacio mundo, A libre)
layout(set = 0, binding = 2, rgba16f) writeonly uniform imageCube normal_map;


layout(push_constant) uniform Params {
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint resolution;
} params;

// --- UTILIDADES DE RUIDO (Simplex 3D) ---
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

// --- GENERACIÓN AAA: HYBRID MULTIFRACTAL ---

// 1. Ruido Base (FBM Suave) - Para continentes y llanuras
float fbm_soft(vec3 x, int octaves) {
    float v = 0.0; float a = 0.5; vec3 shift = vec3(100.0);
    float freq = 1.0;
    for (int i = 0; i < octaves; ++i) {
        v += a * snoise(x * freq);
        x += shift; 
        a *= 0.5;
        freq *= 2.0;
    }
    return v;
}

// 2. Ruido Ridged (Picos) - Para cadenas montañosas afiladas
float ridged_noise(vec3 x, int octaves) {
    float v = 0.0; float a = 1.0; float freq = 1.0;
    float prev_n = 1.0;
    for (int i = 0; i < octaves; ++i) {
        float n = snoise(x * freq);
        n = 1.0 - abs(n);   // Invierte los valles -> Picos
        n = n * n;          // Afila los picos (potencia)
        v += n * a * prev_n; // Escalar por la capa anterior (hace que montañas solo salgan sobre montañas)
        prev_n = n;
        a *= 0.5;
        freq *= 2.0;
    }
    return v;
}

// 3. Domain Warping (Deforma el espacio para que no parezca ruido digital)
vec3 warp(vec3 p) {
    float q = fbm_soft(p * 0.5, 2);
    return p + q * 0.3; // Distorsión moderada
}

// --- FUNCIÓN MAESTRA DE TERRENO ---
float get_terrain_aaa(vec3 p) {
    // A. Domain Warping: Rompe la regularidad
    vec3 warped_p = warp(p * params.noise_scale);

    // B. Forma Continental (Baja Frecuencia)
    // Usamos menos octavas porque queremos formas grandes
    float continental = fbm_soft(warped_p * 0.5, 3);
    
    // Ajustar el nivel del mar: empujamos valores bajos hacia abajo
    // Esto crea llanuras oceánicas grandes
    float base_height = continental; 

    // C. Montañas (Alta Frecuencia)
    float mountains = ridged_noise(warped_p * 1.5, 4);

    // D. Mezcla Inteligente (Biome Masking)
    // Solo ponemos montañas si estamos en "Tierra firme" (continental > 0.1)
    float mountain_mask = smoothstep(0.0, 0.4, continental);
    
    // Altura final = Continente + (Montañas * Mascara)
    // El 0.6 es para controlar qué tan altas son las montañas respecto al continente
    return base_height + (mountains * mountain_mask * 0.6);
}

// --- CURL NOISE PARA VIENTO (IGUAL QUE ANTES) ---
vec3 curl_noise(vec3 p) {
    const float e = 0.01;
    float n1 = snoise(p + vec3(0, e, 0)); float n2 = snoise(p - vec3(0, e, 0));
    float n3 = snoise(p + vec3(0, 0, e)); float n4 = snoise(p - vec3(0, 0, e));
    float n5 = snoise(p + vec3(e, 0, 0)); float n6 = snoise(p - vec3(e, 0, 0));
    float x = n1 - n2; float y = n3 - n4; float z = n5 - n6;
    return normalize(vec3(y - z, z - x, x - y)); 
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
    if (id.x >= params.resolution || id.y >= params.resolution) return;

    vec3 dir = get_direction(id, float(params.resolution));

    // 1. CALCULAR ALTURA (tu zona intacta)
    float n_center = get_terrain_aaa(dir);
    imageStore(height_map, ivec3(id), vec4(n_center, 0.0, 0.0, 1.0));

    // 2. SISTEMA DE COORDENADAS LOCALES (común para normales Y flujo)
    float eps = 1.0 / float(params.resolution);
    vec3 tangent = (abs(dir.y) > 0.99) ? vec3(1, 0, 0) : vec3(0, 1, 0);
    vec3 right = normalize(cross(dir, tangent));
    vec3 up = cross(dir, right);

    // 3. NORMALES AAA (horneadas desde heightmap)
    {
        vec3 dir_r = normalize(dir + right * eps);
        vec3 dir_u = normalize(dir + up * eps);
        
        float h_r = get_terrain_aaa(dir_r);
        float h_u = get_terrain_aaa(dir_u);
        
        vec3 p_center = dir * (params.planet_radius + n_center * params.noise_height);
        vec3 p_r = dir_r * (params.planet_radius + h_r * params.noise_height);
        vec3 p_u = dir_u * (params.planet_radius + h_u * params.noise_height);
        
        vec3 n_world = normalize(cross(p_r - p_center, p_u - p_center));
        imageStore(normal_map, ivec3(id), vec4(n_world, 1.0));
    }

    // 4. CAMPO VECTORIAL (tu lógica original intacta)
    const float sea_level = 0.45;
    const float macro_eps = 0.08;
    const float local_eps = 0.01;
    
    vec3 final_flow;
    
    if (n_center < sea_level) {
        // AGUA: Escape macro
        float hr = get_terrain_aaa(normalize(dir + right * macro_eps));
        float hu = get_terrain_aaa(normalize(dir + up * macro_eps));
        vec3 macro_grad = (hr - n_center) * right + (hu - n_center) * up;
        float escape_intensity = (sea_level - n_center) * 15.0; 
        final_flow = macro_grad * escape_intensity;
    } 
    else {
        // TIERRA: Flujo orgánico + evitación montañas
        vec3 organic_flow = curl_noise(dir * params.noise_scale);
        
        float hr = get_terrain_aaa(normalize(dir + right * local_eps));
        float hu = get_terrain_aaa(normalize(dir + up * local_eps));
        vec3 local_grad = (hr - n_center) * right + (hu - n_center) * up;
        
        float steepness = length(local_grad);
        float avoidance_factor = smoothstep(0.01, 0.08, steepness);
        final_flow = mix(organic_flow, -local_grad * 10.0, avoidance_factor);
    }

    // Proyección tangencial + normalización (tu código original)
    final_flow = final_flow - dot(final_flow, dir) * dir;
    float len = length(final_flow);
    if (len > 0.0001) {
        final_flow /= len;
    } else {
        final_flow = right; 
    }
    
    imageStore(vector_field, ivec3(id), vec4(final_flow, 1.0));
}
