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

// --- BUFFERS ---
layout(set = 0, binding = 0, std430) buffer AgentsBuffer { Agent agents[]; };

#define CELL_CAPACITY 16
#define STRIDE 17 
layout(set = 0, binding = 1, std430) buffer GridBuffer { uint grid_data[]; };

// --- TEXTURAS DE SALIDA (Para visualización) ---
layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_image_out;
layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D color_image_out;

// --- NUEVO: TEXTURA DE ENTRADA (CUBEMAP) ---
// Binding 4: Aquí leemos la altura en lugar de calcularla con matemáticas
layout(set = 0, binding = 4) uniform samplerCube height_map;

layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) float planet_radius;
    layout(offset = 12) float noise_scale;  // Ya no se usa para FBM, pero mantenemos el padding
    layout(offset = 16) float noise_height; // Multiplicador de altura
    layout(offset = 20) uint agent_count;
    layout(offset = 24) uint phase;       
    layout(offset = 28) uint grid_size;   
    layout(offset = 32) uint tex_width; 
} params;

// --- FUNCIONES UTILITARIAS ---
// (Ya no necesitamos hash/noise/fbm aquí porque usamos textura)
uint get_cell_hash(ivec3 cell) {
    const uint p1 = 73856093; const uint p2 = 19349663; const uint p3 = 83492791;
    uint n = (uint(cell.x) * p1) ^ (uint(cell.y) * p2) ^ (uint(cell.z) * p3);
    return n % params.grid_size;
}

// --- FASES 0 y 1 (Sin cambios) ---
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
    float cell_size = 2.0;
    ivec3 cell = ivec3(floor(pos / cell_size));
    uint hash_idx = get_cell_hash(cell);
    uint slot = atomicAdd(grid_data[hash_idx * STRIDE], 1);
    if (slot < CELL_CAPACITY) grid_data[hash_idx * STRIDE + 1 + slot] = idx;
}

// --- FASE 2: UPDATE (Con lectura de Cubemap) ---
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

    // 1. CHEQUEO DE LLEGADA
    if (distance(pos, target) < (params.planet_radius * 0.05)) {
        me.color.w = 2.0; 
        me.velocity = vec4(0.0);
        agents[idx] = me;
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    // --- CAMBIO PRINCIPAL AQUÍ ---
    // 2. ALTURA DEL TERRENO (Lectura de Cubemap)
    vec3 dir_norm = normalize(pos);
    
    // texture(samplerCube, vec3) devuelve el color en esa dirección 3D.
    // Tomamos el canal Rojo (.r) como altura.
    float n_val = texture(height_map, dir_norm).r; 
    
    float h = max(0.0, n_val - 0.45); 
    float terrain_radius = params.planet_radius + (h * params.noise_height);
    // -----------------------------

    // 3. MOVIMIENTO (Seek)
    vec3 desired = normalize(target - pos) * max_speed;
    vec3 steer = (desired - vel);
    vel += steer * 2.0 * params.delta_time; 

    // 4. COLISIONES (Spatial Hash)
    float cell_size = 2.0;
    ivec3 my_cell = ivec3(floor(pos / cell_size));
    float total_overlap = 0.0;
    
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec3 neighbor_cell = my_cell + ivec3(x, y, z);
                uint hash_idx = get_cell_hash(neighbor_cell);
                uint count = min(grid_data[hash_idx * STRIDE], uint(CELL_CAPACITY));
                
                for (uint i = 0; i < count; i++) {
                    uint other_idx = grid_data[hash_idx * STRIDE + 1 + i];
                    if (other_idx == idx) continue; 
                    Agent other = agents[other_idx]; 
                    if (other.color.w < 0.5 || other.color.w > 1.5) continue; 

                    float d = distance(pos, other.position.xyz);
                    float min_dist = radius + other.position.w; 

                    if (d < min_dist && d > 0.0001) {
                        float overlap = min_dist - d;
                        vec3 push = normalize(pos - other.position.xyz);
                        pos += push * (overlap * 0.5); 
                        total_overlap += overlap;
                    }
                }
            }
        }
    }

    // 5. MUERTE POR PRESIÓN
    if (total_overlap > 3.0) { 
        me.color.w = 0.0; 
        me.color.rgb = vec3(0.1, 0.1, 0.1); 
        me.velocity = vec4(0.0);
        agents[idx] = me;
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    // 6. INTEGRACIÓN Y PROYECCIÓN
    vel *= 0.98; 
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    float current_h = length(pos);
    if (current_h < terrain_radius) {
        pos = normalize(pos) * terrain_radius;
    } else {
        pos = normalize(pos) * max(terrain_radius, current_h - (9.8 * params.delta_time));
    }

    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal;

    // 7. GUARDAR
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