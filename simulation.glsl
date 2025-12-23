#[compute]
#version 450

// Tamaño del grupo de trabajo
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// --- ESTRUCTURAS ---
struct Agent {
    vec4 position; // xyz: pos, w: radius
    vec4 target;   // xyz: target
    vec4 velocity; // xyz: vel, w: max_speed
    vec4 color;
};

// --- BUFFERS ---
layout(set = 0, binding = 0, std430) buffer AgentsBuffer {
    Agent agents[];
};

// NUEVO: Buffer de la Grilla (Spatial Hash)
// Estructura aplanada: Cada celda tiene una capacidad fija de IDs de agentes.
// Diseño: [Count_0, ID_0_0, ID_0_1..., Count_1, ID_1_0...]
// Capacidad: 32 agentes por celda.
#define CELL_CAPACITY 32
// Tamaño real en uints = 1 (contador) + 32 (ids) = 33 uints por celda
#define STRIDE 33 

layout(set = 0, binding = 1, std430) buffer GridBuffer {
    uint grid_data[]; 
};

layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) uint phase;
    layout(offset = 12) float map_size;
    layout(offset = 16) float cell_size;
    layout(offset = 20) uint grid_dim;
    layout(offset = 24) vec2 padding; // Recibimos los 2 floats extra aquí
} params;

// --- FUNCIONES HELPER ---

// Convertir Posición 3D -> Índice de Celda 1D
int get_cell_index(vec3 pos) {
    // Clamp para asegurar que no se salgan de la grilla
    float half_map = params.map_size * 0.5;
    vec3 offset_pos = pos + vec3(0.0, 0.0, 0.0); // Ajustar si el mapa no empieza en 0,0
    
    // Coordenadas en grilla (0 a grid_dim-1)
    int x = int(clamp(pos.x / params.cell_size, 0.0, float(params.grid_dim - 1)));
    int z = int(clamp(pos.z / params.cell_size, 0.0, float(params.grid_dim - 1)));
    
    return x + (z * int(params.grid_dim));
}

// --- FASES ---

void phase_clear() {
    // Ejecutamos por CELDA, no por agente
    uint idx = gl_GlobalInvocationID.x;
    uint total_cells = params.grid_dim * params.grid_dim;
    if (idx >= total_cells) return;

    // Poner el contador de la celda a 0
    grid_data[idx * STRIDE] = 0;
}

void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= agents.length()) return;

    vec3 pos = agents[idx].position.xyz;
    int cell_idx = get_cell_index(pos);
    
    // Operación Atómica: Incrementar contador y obtener el slot reservado
    // atomicAdd devuelve el valor previo (nuestro slot)
    uint slot = atomicAdd(grid_data[cell_idx * STRIDE], 1);

    // Si hay espacio, nos registramos
    if (slot < CELL_CAPACITY) {
        grid_data[cell_idx * STRIDE + 1 + slot] = idx; // Guardamos nuestro ID
    }
}


void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= agents.length()) return;

    // --- 1. CHECK DE VIDA ---
    // Si el alpha es 0, está muerto. No se mueve, no calcula nada.
    if (agents[idx].color.w < 0.1) return;

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w;
    vec3 vel = agents[idx].velocity.xyz;
    vec3 target = agents[idx].target.xyz;
    float max_speed = agents[idx].velocity.w;

    // --- 2. MOVIMIENTO SIN FRENOS (SEEK PURO) ---
    vec3 acc = vec3(0.0);
    vec3 to_target = target - pos;
    float dist_target = length(to_target);

    // Si no ha llegado, acelera al máximo.
    if (dist_target > 1.0) {
        vec3 desired = normalize(to_target) * max_speed;
        // Steering fuerte (reacción rápida)
        acc += (desired - vel) * 4.0; 
    } else {
        // Si llega, se detiene en seco (o teleporta al target)
        vel = vec3(0.0);
        pos = target; 
    }

    // Integrar Velocidad
    vel += acc * params.delta_time;
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    vec3 predicted_pos = pos + vel * params.delta_time;

    // --- 3. COLISIONES Y MUERTE POR APLASTAMIENTO ---
    int my_cell_x = int(clamp(predicted_pos.x / params.cell_size, 0.0, float(params.grid_dim - 1)));
    int my_cell_z = int(clamp(predicted_pos.z / params.cell_size, 0.0, float(params.grid_dim - 1)));

    int pressure_count = 0; // Contador de cuántos me tocan

    for (int x = -1; x <= 1; x++) {
        for (int z = -1; z <= 1; z++) {
            int neighbor_x = my_cell_x + x;
            int neighbor_z = my_cell_z + z;

            if (neighbor_x >= 0 && neighbor_x < int(params.grid_dim) &&
                neighbor_z >= 0 && neighbor_z < int(params.grid_dim)) {
                
                int cell_idx = neighbor_x + (neighbor_z * int(params.grid_dim));
                uint count = min(grid_data[cell_idx * STRIDE], uint(CELL_CAPACITY));

                for (uint i = 0; i < count; i++) {
                    uint other_idx = grid_data[cell_idx * STRIDE + 1 + i];
                    if (other_idx == idx) continue;

                    // Si el otro está muerto, lo tratamos como un obstáculo estático o lo ignoramos.
                    // Aquí lo ignoramos para que los cadáveres no causen más muertes (opcional).
                    if (agents[other_idx].color.w < 0.1) continue;

                    vec3 other_pos = agents[other_idx].position.xyz;
                    float other_radius = agents[other_idx].position.w;
                    
                    vec3 diff = predicted_pos - other_pos;
                    float dist = length(diff);
                    float min_dist = radius + other_radius;

                    if (dist < min_dist && dist > 0.0001) {
                        float overlap = min_dist - dist;
                        vec3 push = normalize(diff) * (overlap * 0.5);
                        predicted_pos += push;
                        
                        // ¡PRESIÓN!
                        pressure_count++; 
                    }
                }
            }
        }
    }

    // --- LÓGICA DE MUERTE ---
    // Si más de 8 agentes me están tocando a la vez, muero aplastado.
    if (pressure_count > 8) {
        agents[idx].color = vec4(0.0, 0.0, 0.0, 0.0); // Negro y Alpha 0 (Muerto)
        agents[idx].velocity = vec4(0.0); // Detener movimiento
        // No actualizamos posición, se queda donde murió.
        return; 
    }
    
    // Constraints
    predicted_pos.y = 0.0;
    
    // Guardar
    agents[idx].position.xyz = predicted_pos;
    agents[idx].velocity.xyz = vel;
}



void main() {
    if (params.phase == 0) {
        phase_clear();
    } else if (params.phase == 1) {
        phase_populate();
    } else {
        phase_update();
    }
}