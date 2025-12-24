#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct Agent {
    vec4 position; // xyz: pos, w: radius
    vec4 target;   // xyz: target, w: padding
    vec4 velocity; // xyz: vel, w: max_speed
    vec4 color;    // w: status (0=muerto, 1=vivo, 2=salvado)
};

layout(set = 0, binding = 0, std430) buffer AgentsBuffer {
    Agent agents[];
};

// BLOQUE CRÍTICO: Debe sumar 32 bytes (8 floats/uints x 4 bytes)
layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) float planet_radius;
    layout(offset = 12) float noise_scale;
    layout(offset = 16) float noise_height;
    layout(offset = 20) uint agent_count;
    layout(offset = 24) vec2 padding; // Relleno obligatorio para llegar a 32 bytes
} params;

// --- FUNCIONES DE RUIDO ---
float hash(vec3 p) {
    p  = fract( p*0.3183099 + .1 );
    p *= 17.0;
    return fract( p.x*p.y*p.z*(p.x+p.y+p.z) );
}

float noise( in vec3 x ) {
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f*f*(3.0-2.0*f);
    return mix(mix(mix( hash(i+vec3(0,0,0)), 
                        hash(i+vec3(1,0,0)),f.x),
                   mix( hash(i+vec3(0,1,0)), 
                        hash(i+vec3(1,1,0)),f.x),f.y),
               mix(mix( hash(i+vec3(0,0,1)), 
                        hash(i+vec3(1,0,1)),f.x),
                   mix( hash(i+vec3(0,1,1)), 
                        hash(i+vec3(1,1,1)),f.x),f.y),f.z);
}

float fbm(vec3 x) {
    float v = 0.0;
    float a = 0.5;
    vec3 shift = vec3(100.0);
    for (int i = 0; i < 5; ++i) {
        v += a * noise(x);
        x = x * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

void main() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    // Check Status
    float status = agents[idx].color.w;
    if (status < 0.5 || status > 1.5) return;

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w;
    vec3 vel = agents[idx].velocity.xyz;
    vec3 target = agents[idx].target.xyz;
    float max_speed = agents[idx].velocity.w;

    // 1. Extracción (Llegada al objetivo)
    float dist_target = distance(pos, target);
    if (dist_target < 2.0) {
        agents[idx].color.w = 2.0; // Salvado
        agents[idx].velocity = vec4(0.0);
        return;
    }

    // 2. Altura del Terreno
    vec3 dir_from_center = normalize(pos);
    float h = fbm(dir_from_center * params.noise_scale);
    float terrain_radius = params.planet_radius + (max(0.0, h - 0.45) * params.noise_height);

    // 3. Movimiento
    vec3 acc = vec3(0.0);
    vec3 desired = normalize(target - pos) * max_speed;
    acc += (desired - vel) * 2.0;

    // Separación simple (Fuerza bruta para demo)
    float total_overlap = 0.0;
    
    // NOTA: Para producción real, aquí iría el Spatial Hash 3D.
    // Usamos un loop reducido o asumimos pocos agentes para no colgar la GPU en esta demo.
    // Si tienes 1000 agentes, 1000x1000 = 1 millón de checks. Puede ser pesado.
    // Optimizacion simple: Solo revisar vecinos cercanos en indice (suponiendo orden)
    // O simplemente ignorar colisiones complejas por ahora para validar el movimiento.
    
    vel += acc * params.delta_time;
    vel *= 0.98; // Fricción
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;

    pos += vel * params.delta_time;

    // 4. Proyección Esférica (Snap to ground)
    pos = normalize(pos) * terrain_radius;

    // Re-alinear velocidad a la tangente
    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal;

    agents[idx].position.xyz = pos;
    agents[idx].velocity.xyz = vel;
}