// #[compute]
// #version 450

// layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// // Salida: Alturas para un array de vectores unitarios (posiciones en esfera)
// layout(set = 0, binding = 0, std430) buffer HeightBuffer {
//     float heights[];
// };

// // Entrada: Posiciones normalizadas (esfera unitaria) para consultar
// layout(set = 0, binding = 1, std430) buffer QueryBuffer {
//     vec4 queries[]; // xyz: direccion, w: padding
// };

// layout(push_constant) uniform Params {
//     float planet_radius;
//     float noise_scale;
//     float noise_height;
//     uint count;
// } params;

// // --- FUNCIONES DE RUIDO ---
// // Hash simple 3D -> 1D
// float hash(vec3 p) {
//     p  = fract( p*0.3183099 + .1 );
//     p *= 17.0;
//     return fract( p.x*p.y*p.z*(p.x+p.y+p.z) );
// }

// // Noise base (suavizado trilineal)
// float noise( in vec3 x ) {
//     vec3 i = floor(x);
//     vec3 f = fract(x);
//     f = f*f*(3.0-2.0*f);
//     return mix(mix(mix( hash(i+vec3(0,0,0)), 
//                         hash(i+vec3(1,0,0)),f.x),
//                    mix( hash(i+vec3(0,1,0)), 
//                         hash(i+vec3(1,1,0)),f.x),f.y),
//                mix(mix( hash(i+vec3(0,0,1)), 
//                         hash(i+vec3(1,0,1)),f.x),
//                    mix( hash(i+vec3(0,1,1)), 
//                         hash(i+vec3(1,1,1)),f.x),f.y),f.z);
// }

// // Fractal Brownian Motion (Capas de detalle)
// float fbm(vec3 x) {
//     float v = 0.0;
//     float a = 0.5;
//     vec3 shift = vec3(100.0);
//     // 5 octavas de ruido
//     for (int i = 0; i < 5; ++i) {
//         v += a * noise(x);
//         x = x * 2.0 + shift;
//         a *= 0.5;
//     }
//     return v;
// }

// void main() {
//     uint idx = gl_GlobalInvocationID.x;
//     if (idx >= params.count) return;

//     vec3 dir = normalize(queries[idx].xyz);
    
//     // Muestrear ruido
//     // Multiplicamos dir * scale para frecuencia
//     float n = fbm(dir * params.noise_scale);
    
//     // Máscara simple: Océano vs Tierra
//     // Si el ruido es bajo, es agua (nivel base). Si es alto, es montaña.
//     float h = max(0.0, n - 0.45); // Nivel del mar en 0.45
    
//     heights[idx] = h * params.noise_height;
// }