#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct Agent {
    vec4 position; 
    vec4 target;   
    vec4 velocity; 
    vec4 color;    
};

layout(set = 0, binding = 0, std430) buffer AgentsBuffer { Agent agents[]; };

// AUMENTAMOS CAPACIDAD PARA MEJOR FÍSICA
#define CELL_CAPACITY 32 
#define DATA_PER_AGENT 4 
#define STRIDE (1 + CELL_CAPACITY * DATA_PER_AGENT)

layout(set = 0, binding = 1, std430) buffer GridBuffer { uint grid_data[]; };

layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_image_out;
layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D color_image_out;
layout(set = 0, binding = 4) uniform samplerCube height_map;

layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) float planet_radius;
    layout(offset = 12) float noise_scale;
    layout(offset = 16) float noise_height;
    layout(offset = 20) uint agent_count;
    layout(offset = 24) uint phase;       
    layout(offset = 28) uint grid_size;   
    layout(offset = 32) uint tex_width; 
} params;

uint get_cell_hash(ivec3 cell) {
    const uint p1 = 73856093; const uint p2 = 19349663; const uint p3 = 83492791;
    uint n = (uint(cell.x) * p1) ^ (uint(cell.y) * p2) ^ (uint(cell.z) * p3);
    return n % params.grid_size;
}

void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.grid_size) return;
    grid_data[idx * STRIDE] = 0;
}

void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;
    float status = agents[idx].color.w;
    if (status < 0.5 || status > 1.5) return;

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w;

    float cell_size = 2.0;
    ivec3 cell = ivec3(floor(pos / cell_size));
    uint hash_idx = get_cell_hash(cell);
    
    uint slot = atomicAdd(grid_data[hash_idx * STRIDE], 1);
    if (slot < CELL_CAPACITY) {
        uint base_idx = hash_idx * STRIDE + 1 + (slot * DATA_PER_AGENT);
        grid_data[base_idx + 0] = floatBitsToUint(pos.x);
        grid_data[base_idx + 1] = floatBitsToUint(pos.y);
        grid_data[base_idx + 2] = floatBitsToUint(pos.z);
        grid_data[base_idx + 3] = floatBitsToUint(radius);
    }
}


// --- FASE 2: UPDATE (PULIDO) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    Agent me = agents[idx];
    float status = me.color.w;
    int tex_w = int(params.tex_width);
    ivec2 coord = ivec2(int(idx) % tex_w, int(idx) / tex_w);

    if (status < 0.5 || status > 1.5) {
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    vec3 pos = me.position.xyz;
    vec3 vel = me.velocity.xyz;
    vec3 target = me.target.xyz;
    float radius = me.position.w;
    float max_speed = me.velocity.w;

    // 0. TERRENO
    vec3 dir_norm = normalize(pos);
    float n_val = texture(height_map, dir_norm).r; 
    float h = max(0.0, n_val - 0.45); 
    float terrain_radius = params.planet_radius + (h * params.noise_height);

    // --- FUERZAS ACUMULADAS ---
    vec3 acc_force = vec3(0.0);

    // 1. SEEK (IR AL OBJETIVO)
    // Reducimos el peso de llegada (1.0) para que la separación gane
    vec3 desired = normalize(target - pos) * max_speed;
    vec3 steer_seek = (desired - vel);
    acc_force += steer_seek * 1.5; // Peso Seek normal

    // 2. SEPARACIÓN (EVITAR VECINOS)
    vec3 separation_force = vec3(0.0);
    uint neighbors_count = 0;
    
    // VARIABLES DE COLISIÓN DURA
    vec3 collision_push = vec3(0.0);

    float cell_size = 2.0;
    ivec3 my_cell = ivec3(floor(pos / cell_size));
    
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec3 neighbor_cell = my_cell + ivec3(x, y, z);
                uint hash_idx = get_cell_hash(neighbor_cell);
                uint count = min(grid_data[hash_idx * STRIDE], uint(CELL_CAPACITY));
                uint base_ptr = hash_idx * STRIDE + 1;
                
                for (uint i = 0; i < count; i++) {
                    uint ptr = base_ptr + (i * DATA_PER_AGENT);
                    vec3 other_pos = vec3(
                        uintBitsToFloat(grid_data[ptr + 0]),
                        uintBitsToFloat(grid_data[ptr + 1]),
                        uintBitsToFloat(grid_data[ptr + 2])
                    );
                    
                    vec3 diff = pos - other_pos;
                    float dist_sq = dot(diff, diff);
                    
                    if (dist_sq < 0.00001) continue; // Mismo agente

                    float other_radius = uintBitsToFloat(grid_data[ptr + 3]);
                    float dist = sqrt(dist_sq);
                    float combined_radius = radius + other_radius;
                    
                    // A) ZONA DE SEPARACIÓN (Preventiva)
                    // Actúa antes de tocarnos (Radio * 2.0)
                    float safe_zone = combined_radius * 2.0; 
                    if (dist < safe_zone) {
                        // Cuanto más cerca, más fuerte la fuerza (Ley inversa)
                        // Normalize(diff) es la dirección para huir
                        vec3 flee_dir = diff / dist; 
                        float strength = 1.0 - (dist / safe_zone); // 1.0 si tocamos, 0.0 si lejos
                        separation_force += flee_dir * strength;
                        neighbors_count++;
                    }

                    // B) ZONA DE COLISIÓN (Correctiva)
                    // Si ya nos tocamos, empujamos la POSICIÓN, no la velocidad
                    if (dist < combined_radius) {
                        float overlap = combined_radius - dist;
                        // Corrección de posición directa (PBD)
                        collision_push += (diff / dist) * overlap * 0.5;
                    }
                }
            }
        }
    }

    // APLICAR FUERZAS
    if (neighbors_count > 0) {
        // Multiplicador ALTO (8.0). La separación es prioridad máxima.
        // Divide por neighbors_count para promediar
        acc_force += (separation_force / float(neighbors_count)) * 25.0; 
    }

    // APLICAR COLISIÓN DURA (Modifica posición directamente)
    pos += collision_push;

    // INTEGRACIÓN
    vel += acc_force * params.delta_time;
    
    // Fricción condicional: Si hay muchos vecinos, frenamos para evitar explosión
    if (neighbors_count > 3) vel *= 0.90; 
    else vel *= 0.99;

    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    // PROYECCIÓN Y CIERRE (Igual que antes)
    float current_h = length(pos);
    if (current_h < terrain_radius) pos = normalize(pos) * terrain_radius;
    else pos = normalize(pos) * max(terrain_radius, current_h - (9.8 * params.delta_time));

    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal;

    // Si llegamos muy cerca del target, paramos todo para evitar jitter final
    if (distance(pos, target) < params.planet_radius * 0.02) {
         vel *= 0.5; // Frenado agresivo cerca del destino
    }

    me.position.xyz = pos;
    me.velocity.xyz = vel;
    agents[idx] = me;
    imageStore(pos_image_out, coord, me.position);
    imageStore(color_image_out, coord, me.color);
}


void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else phase_update();
}