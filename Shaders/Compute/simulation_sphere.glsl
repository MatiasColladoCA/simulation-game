#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// --- ESTRUCTURAS ---
struct Agent {
    vec4 position; 
    vec4 target;   
    vec4 velocity; 
    vec4 color;    
};

// --- BINDINGS ---
layout(set = 0, binding = 0, std430) buffer AgentsBuffer { Agent agents[]; };

#define CELL_CAPACITY 32 
#define DATA_PER_AGENT 4 
#define STRIDE (1 + CELL_CAPACITY * DATA_PER_AGENT)
layout(set = 0, binding = 1, std430) buffer GridBuffer { uint grid_data[]; };

layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_image_out;
layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D color_image_out;

layout(set = 0, binding = 4) uniform samplerCube height_map;
layout(set = 0, binding = 5) uniform samplerCube vector_field;

// --- PUSH CONSTANTS (48 Bytes totales con padding implícito) ---
layout(push_constant) uniform Params {
    float delta_time;
    float time;
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint agent_count;
    uint phase;       
    uint grid_size;   
    uint tex_width; 
} params;

// --- FUNCIONES ---

uint get_cell_hash(ivec3 cell) {
    const uint p1 = 73856093; const uint p2 = 19349663; const uint p3 = 83492791;
    // abs() previene índices negativos en el hash
    uint n = (uint(abs(cell.x)) * p1) ^ (uint(abs(cell.y)) * p2) ^ (uint(abs(cell.z)) * p3);
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
    if (agents[idx].color.w < 0.5) return; 

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w;

    float cell_size = 2.0;
    ivec3 cell = ivec3(floor(pos / cell_size));
    uint hash_idx = get_cell_hash(cell);
    
    // atomicAdd devuelve el valor ANTES de sumar
    uint slot = atomicAdd(grid_data[hash_idx * STRIDE], 1);
    if (slot < CELL_CAPACITY) {
        uint base_idx = hash_idx * STRIDE + 1 + (slot * DATA_PER_AGENT);
        grid_data[base_idx + 0] = floatBitsToUint(pos.x);
        grid_data[base_idx + 1] = floatBitsToUint(pos.y);
        grid_data[base_idx + 2] = floatBitsToUint(pos.z);
        grid_data[base_idx + 3] = floatBitsToUint(radius);
    }
}


// --- FASE 2: UPDATE (CORREGIDA: FIlAS Y VIBRACIÓN) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    Agent me = agents[idx];
    int tex_w = int(params.tex_width);
    ivec2 coord = ivec2(int(idx) % tex_w, int(idx) / tex_w);

    if (me.color.w < 0.5) {
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    vec3 pos = me.position.xyz;
    vec3 vel = me.velocity.xyz;
    float radius = me.position.w;
    float max_speed = me.velocity.w;

    // 1. TERRENO & NAVEGACIÓN
    vec3 dir_norm = normalize(pos);
    float h_val = textureLod(height_map, dir_norm, 0.0).r;
    float terrain_floor = params.planet_radius + (h_val * params.noise_height);

    vec3 flow_dir = textureLod(vector_field, dir_norm, 0.0).rgb;
    vec3 desired = flow_dir * max_speed;
    vec3 steer = (desired - vel);
    vec3 acc_force = steer * 4.0; 

    // /// --- FIX 1: ROMPER LAS FILAS (Wander Noise) ---
    // Generamos un vector de ruido pseudo-aleatorio basado en el ID del agente y el tiempo.
    // Esto asegura que cada agente tenga una ligera variación en su dirección, rompiendo la simetría de la grilla.
    float noise_seed = float(idx) * 13.1 + params.time * 43.7; // Números primos arbitrarios
    vec3 wander_noise = vec3(
        sin(noise_seed),
        cos(noise_seed * 1.3),
        sin(noise_seed * 0.7)
    );
    // Añadimos una fuerza pequeña pero constante de caos.
    acc_force += normalize(wander_noise) * 2.0; 
    // -----------------------------------------------------


    // 2. COLISIONES
    vec3 separation = vec3(0.0);
    vec3 collision_push = vec3(0.0);
    uint neighbors = 0;
    
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
                    
                    if (dist_sq < 0.000001) {
                         // Protección de singularidad (mantenemos esto)
                         float noise = float(idx % 10u) * 0.1; 
                         diff = vec3(0.01 + noise, 0.01, 0.0); 
                         dist_sq = dot(diff, diff);
                    }

                    float other_r = uintBitsToFloat(grid_data[ptr + 3]);
                    float dist = sqrt(dist_sq);
                    float combined_r = radius + other_r;
                    
                    // Separación suave (Aumentamos un poco el radio de percepción a 2.5x)
                    if (dist < combined_r * 2.5) {
                        separation += (diff / dist) * (1.0 - dist / (combined_r * 2.5));
                        neighbors++;
                    }
                    
                    // Colisión Dura
                    if (dist < combined_r) {
                        float overlap = combined_r - dist;
                        // /// --- FIX 2: REDUCIR VIBRACIÓN EN COLISIÓN DURA ---
                        // Antes era * 0.5 (resolver 50% por frame). Ahora es * 0.15.
                        // Esto hace que se "empujen" suavemente fuera de la colisión en varios frames, no de golpe.
                        collision_push += (diff / dist) * overlap * 0.15;
                        // -----------------------------------------------------
                    }
                }
            }
        }
    }

    if (neighbors > 0) {
        // /// --- FIX 3: REDUCIR FUERZA DE SEPARACIÓN ---
        // Antes era 40.0. Bajamos a 15.0 para evitar que salgan disparados y luego reboten.
        acc_force += (separation / float(neighbors)) * 15.0;
        // -------------------------------------------------
    }

    // Limitador de presión (Fricción de multitud)
    // Hacemos que la fricción empiece antes (con 5 vecinos ya se siente)
    float crowd_friction = clamp(1.0 - (float(neighbors) / 10.0), 0.2, 1.0);
    vel *= crowd_friction;

    // Hard Clamp (Mantenemos esto como seguridad final)
    float push_len = length(collision_push);
    if (push_len > 0.3) { // Reduje un poco el límite máximo de empuje también
        collision_push = (collision_push / push_len) * 0.3;
    }

    // 3. INTEGRACIÓN
    pos += collision_push; 
    vel += acc_force * params.delta_time;
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    // 4. SNAP TO TERRAIN (Mantenemos igual)
    float current_r = length(pos);
    if (current_r < terrain_floor) {
        pos = normalize(pos) * terrain_floor;
    } else {
        pos = normalize(pos) * max(terrain_floor, current_r - (20.0 * params.delta_time));
    }
    
    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal; 

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