#[compute]
#version 450

// Tamaño del grupo de hilos.
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// Estructura alineada a 16 bytes (vec4) para evitar problemas de padding std430.
struct Agent {
    vec4 position; // xyz: pos, w: padding/radius
    vec4 target;   // xyz: target, w: padding
    vec4 velocity; // xyz: vel, w: speed
    vec4 color;    // x: r, y: g, z: b, w: a (Color del equipo)
};

// Buffer de datos (Lectura y Escritura)
layout(set = 0, binding = 0, std430) buffer AgentsBuffer {
    Agent agents[];
};

layout(push_constant) uniform Params {
    float delta_time;
    float avoid_radius;
} params;

void main() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= agents.length()) return;

    // 1. Cargar datos
    vec3 pos = agents[idx].position.xyz;
    vec3 tgt = agents[idx].target.xyz;
    vec3 vel = agents[idx].velocity.xyz;
    float speed = agents[idx].velocity.w;

    // 2. Lógica de movimiento (DOD: Datos transformados)
    vec3 direction = normalize(tgt - pos);
    
    // Repulsión básica (Fuerza bruta O(N^2) - Aceptable para <1000 agentes)
    vec3 separation = vec3(0.0);
    for (uint i = 0; i < agents.length(); i++) {
        if (i == idx) continue;
        vec3 other_pos = agents[i].position.xyz;
        vec3 diff = pos - other_pos;
        float dist = length(diff);
        if (dist < params.avoid_radius && dist > 0.001) {
            separation += normalize(diff) / dist;
        }
    }

    // Integrar
    vec3 new_vel = direction * speed + (separation * 2.0);
    pos += new_vel * params.delta_time;

    // Condición de parada simple (si está muy cerca del objetivo)
    if (distance(pos, tgt) < 0.5) {
        pos = tgt;
    }

    // 3. Escribir datos
    agents[idx].position.xyz = pos;
    agents[idx].velocity.xyz = new_vel; // Guardamos vel para debug o inercia
}