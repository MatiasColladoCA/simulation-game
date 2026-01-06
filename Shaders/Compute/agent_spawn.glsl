#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct AgentDataSphere {
    vec4 position;
    vec4 velocity;
    vec4 group_data;
    vec4 color;
};

struct SpawnRequest {
    vec4 position;
    vec4 velocity;
    vec4 color;
};

layout(set = 0, binding = 0, std430) buffer AgentBuffer { AgentDataSphere agents[]; };
layout(set = 0, binding = 1, std430) buffer DeadListBuffer { uint free_indices[]; };
// [1] es el Stack Pointer
layout(set = 0, binding = 2, std430) buffer CounterBuffer { uint counters[]; }; 
layout(set = 0, binding = 3, std430) readonly buffer SpawnRequestBuffer { SpawnRequest requests[]; };

layout(push_constant) uniform Info { uint spawn_count; } info;

void main() {
    uint gid = gl_GlobalInvocationID.x;
    if (gid >= info.spawn_count) return;

    // 1. Decrementar stack pointer para obtener un slot
    uint stack_ptr = atomicAdd(counters[1], -1);

    // Si el puntero era 0 o menos, el stack estaba vacío (no hay lugar)
    if (stack_ptr <= 0) {
        atomicAdd(counters[1], 1); // Restaurar (opcional)
        return; 
    }

    // 2. Obtener índice (stack_ptr era el valor ANTES de restar, así que restamos 1 para array 0-based)
    uint agent_idx = free_indices[stack_ptr - 1];

    // 3. Escribir
    SpawnRequest req = requests[gid];
    
    agents[agent_idx].position = req.position; // W ya viene en 1.0 desde C#
    agents[agent_idx].velocity = req.velocity;
    agents[agent_idx].group_data = vec4(0.0);
    agents[agent_idx].color = req.color;       // W ya viene en 1.0 desde C#
}