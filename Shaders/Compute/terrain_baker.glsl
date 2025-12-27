#[compute]
#version 450

layout(local_size_x = 32, local_size_y = 32, local_size_z = 1) in;

// Salida 0: Altura (R)
layout(set = 0, binding = 0, r32f) writeonly uniform imageCube height_map;

// Salida 1: Campo Vectorial (RGB = Dirección, A = Fuerza)
layout(set = 0, binding = 1, rgba16f) writeonly uniform imageCube vector_field;

layout(push_constant) uniform Params {
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint resolution;
} params;

// --- RUIDO (Simplex 3D barato) ---
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

// --- FBM para Altura ---
float fbm(vec3 x) {
    float v = 0.0; float a = 0.5; vec3 shift = vec3(100.0);
    for (int i = 0; i < 5; ++i) { v += a * snoise(x); x = x * 2.0 + shift; a *= 0.5; }
    return v;
}

// --- CURL NOISE (Vectores de fluido) ---
// Calcula el rotacional del campo de ruido potencial
vec3 curl_noise(vec3 p) {
    const float e = 0.01; // epsilon para derivadas
    
    // Calculamos derivadas parciales del ruido (cambio en x, y, z)
    float n1 = snoise(p + vec3(0, e, 0)); 
    float n2 = snoise(p - vec3(0, e, 0)); 
    float n3 = snoise(p + vec3(0, 0, e)); 
    float n4 = snoise(p - vec3(0, 0, e)); 
    float n5 = snoise(p + vec3(e, 0, 0)); 
    float n6 = snoise(p - vec3(e, 0, 0));

    float x = n1 - n2;
    float y = n3 - n4;
    float z = n5 - n6;

    // Curl = (Gradiente del ruido) x (Vector unitario o normal)
    // Aproximación simple de curl noise 3D:
    return normalize(vec3(y - z, z - x, x - y)); 
}

vec3 get_direction(uvec3 id, float size) {
    vec2 uv = (vec2(id.xy) + 0.5) / size;
    uv = uv * 2.0 - 1.0;
    vec3 dir;
    uint face = id.z;
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

    // 1. ESCRIBIR ALTURA (Como antes)
    float n = fbm(dir * params.noise_scale);
    imageStore(height_map, ivec3(id), vec4(n, 0.0, 0.0, 1.0));

    // 2. ESCRIBIR VECTOR FIELD (Nuevo)
    // Usamos una escala diferente para el viento (más suave)
    vec3 flow = curl_noise(dir * (params.noise_scale * 0.5));
    
    // Proyectar el flujo sobre la superficie de la esfera para que sea tangente
    // (El viento sopla paralelo al suelo, no hacia el espacio)
    vec3 normal = dir; // En una esfera, la normal es la dirección desde el centro
    flow = flow - dot(flow, normal) * normal; 
    flow = normalize(flow);

    imageStore(vector_field, ivec3(id), vec4(flow, 1.0)); // Alpha 1.0 = Fuerza máxima
}