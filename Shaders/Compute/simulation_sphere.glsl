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
layout(set = 0, binding = 0, std430) buffer AgentsBuffer {
    Agent agents[];
};

// GRILLA 3D ESPARCIDA (Hash Table)
#define CELL_CAPACITY 16
#define STRIDE 17 // 1 count + 16 ids
layout(set = 0, binding = 1, std430) buffer GridBuffer {
    uint grid_data[];
};

layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) float planet_radius;
    layout(offset = 12) float noise_scale;
    layout(offset = 16) float noise_height;
    layout(offset = 20) uint agent_count;
    layout(offset = 24) uint phase;       // 0:Clear, 1:Populate, 2:Update
    layout(offset = 28) uint grid_size;   // Tamaño del array hash (16384)
} params;

// --- FUNCIONES DE RUIDO Y UTILIDADES ---
// (Copia aquí las funciones hash, noise y fbm del paso anterior o del planet compute)
// ... [INSERTAR noise() y fbm() AQUÍ] ...
// Por brevedad, asumo que las tienes del código anterior. Si no, avísame.
float hash1(vec3 p) {
    p  = fract( p*0.3183099 + .1 );
    p *= 17.0;
    return fract( p.x*p.y*p.z*(p.x+p.y+p.z) );
}
float noise( in vec3 x ) {
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f*f*(3.0-2.0*f);
    return mix(mix(mix( hash1(i+vec3(0,0,0)), hash1(i+vec3(1,0,0)),f.x), mix( hash1(i+vec3(0,1,0)), hash1(i+vec3(1,1,0)),f.x),f.y), mix(mix( hash1(i+vec3(0,0,1)), hash1(i+vec3(1,0,1)),f.x), mix( hash1(i+vec3(0,1,1)), hash1(i+vec3(1,1,1)),f.x),f.y),f.z);
}
float fbm(vec3 x) {
    float v = 0.0; float a = 0.5; vec3 shift = vec3(100.0);
    for (int i = 0; i < 5; ++i) { v += a * noise(x); x = x * 2.0 + shift; a *= 0.5; }
    return v;
}

// --- HASHING ESPACIAL 3D ---
// Convierte una coordenada de celda (x,y,z) en un índice único
uint get_cell_hash(ivec3 cell) {
    // Primos grandes para minimizar colisiones
    const uint p1 = 73856093;
    const uint p2 = 19349663;
    const uint p3 = 83492791;
    
    // XOR y Modulo
    uint n = (uint(cell.x) * p1) ^ (uint(cell.y) * p2) ^ (uint(cell.z) * p3);
    return n % params.grid_size;
}

// --- FASE 0: CLEAR ---
void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.grid_size) return;
    grid_data[idx * STRIDE] = 0; // Reset contador
}

// --- FASE 1: POPULATE ---
void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;
    
    // Si no está vivo, no ocupa espacio
    float status = agents[idx].color.w;
    if (status < 0.5 || status > 1.5) return;

    vec3 pos = agents[idx].position.xyz;
    float cell_size = 2.0; // Tamaño de celda (ajustar según densidad)
    
    // Discretizar posición 3D
    ivec3 cell = ivec3(floor(pos / cell_size));
    uint hash_idx = get_cell_hash(cell);
    
    // Registro atómico
    uint slot = atomicAdd(grid_data[hash_idx * STRIDE], 1);
    if (slot < CELL_CAPACITY) {
        grid_data[hash_idx * STRIDE + 1 + slot] = idx;
    }
}

// --- FASE 2: UPDATE ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    float status = agents[idx].color.w;
    if (status < 0.5 || status > 1.5) return;

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w;
    vec3 vel = agents[idx].velocity.xyz;
    vec3 target = agents[idx].target.xyz;
    float max_speed = agents[idx].velocity.w;

    // 1. Lógica de Extracción
    if (distance(pos, target) < 2.0) {
        agents[idx].color.w = 2.0; agents[idx].velocity = vec4(0.0); return;
    }

    // 2. Altura y Gravedad
    vec3 dir_norm = normalize(pos);
    float h = fbm(dir_norm * params.noise_scale);
    float terrain_radius = params.planet_radius + (max(0.0, h - 0.45) * params.noise_height);

    // 3. Movimiento (Seek)
    vec3 acc = vec3(0.0);
    vec3 desired = normalize(target - pos) * max_speed;
    acc += (desired - vel) * 2.0;

    // 4. COLISIONES OPTIMIZADAS (Hash 3D)
    float cell_size = 2.0;
    ivec3 my_cell = ivec3(floor(pos / cell_size));
    float total_overlap = 0.0;
    
    // Revisar 3x3x3 celdas vecinas (27 celdas)
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec3 neighbor_cell = my_cell + ivec3(x, y, z);
                uint hash_idx = get_cell_hash(neighbor_cell);
                
                uint count = min(grid_data[hash_idx * STRIDE], uint(CELL_CAPACITY));
                
                for (uint i = 0; i < count; i++) {
                    uint other_idx = grid_data[hash_idx * STRIDE + 1 + i];
                    
                    if (other_idx == idx) continue;
                    // Ignorar muertos/salvados
                    if (agents[other_idx].color.w < 0.5 || agents[other_idx].color.w > 1.5) continue;

                    vec3 other_pos = agents[other_idx].position.xyz;
                    float d = distance(pos, other_pos);
                    float min_dist = radius + agents[other_idx].position.w; // 1.0m usually

                    if (d < min_dist && d > 0.0001) {
                        float overlap = min_dist - d;
                        vec3 push = normalize(pos - other_pos);
                        
                        // Soft PBD collision response
                        pos += push * (overlap * 0.2); 
                        total_overlap += overlap;
                    }
                }
            }
        }
    }

    // Muerte por presión
    if (total_overlap > 2.0) {
        agents[idx].color = vec4(0.0, 0.0, 0.0, 0.0);
        agents[idx].velocity = vec4(0.0);
        return;
    }

    // Integración
    vel += acc * params.delta_time;
    vel *= 0.98;
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    // Proyección Esférica
    pos = normalize(pos) * terrain_radius;
    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal;

    agents[idx].position.xyz = pos;
    agents[idx].velocity.xyz = vel;
}

void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else phase_update();
}