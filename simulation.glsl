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

    // 1. CHECK DE VIDA
    if (agents[idx].color.w < 0.1) return;

    vec3 pos = agents[idx].position.xyz;
    float radius = agents[idx].position.w; // 0.5
    vec3 vel = agents[idx].velocity.xyz;
    vec3 target = agents[idx].target.xyz;
    float max_speed = agents[idx].velocity.w;

    // --- CAPA 1: NAVEGACIÓN (Arrive + Steering) ---
    vec3 acc = vec3(0.0);
    vec3 to_target = target - pos;
    float dist_target = length(to_target);

    // Arrive: Desacelerar suavemente al llegar
    // Radio de frenado: 15 metros.
    float slowing_radius = 15.0;
    float target_speed = max_speed;
    
    if (dist_target < slowing_radius) {
        target_speed = max_speed * (dist_target / slowing_radius);
    }
    
    vec3 desired_vel = normalize(to_target) * target_speed;
    
    // Steering Force: Qué tan rápido corregimos el rumbo
    acc += (desired_vel - vel) * 2.0; 

    // --- CAPA 2: SEPARACIÓN SOCIAL (Cortesía) ---
    // Actúa ANTES de tocarse. Queremos mantener 1.5m de distancia si es posible.
    // radius (0.5) + radius (0.5) + personal_space (0.5) = 1.5
    float social_threshold = radius * 3.0; 
    vec3 social_force = vec3(0.0);

    // Integramos velocidad (Euler)
    vel += acc * params.delta_time;
    // Limitamos velocidad máxima
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    
    vec3 predicted_pos = pos + vel * params.delta_time;

    // --- CAPA 3: COLISIONES FÍSICAS (Soft PBD) Y SOCIALES ---
    int my_cell_x = int(clamp(predicted_pos.x / params.cell_size, 0.0, float(params.grid_dim - 1)));
    int my_cell_z = int(clamp(predicted_pos.z / params.cell_size, 0.0, float(params.grid_dim - 1)));

    int contact_count = 0;      // Cuántos me tocan físicamente
    float total_overlap = 0.0;  // Cuánto me están aplastando

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
                    if (agents[other_idx].color.w < 0.1) continue; // Ignorar muertos

                    vec3 other_pos = agents[other_idx].position.xyz;
                    float other_radius = agents[other_idx].position.w;
                    
                    vec3 diff = predicted_pos - other_pos;
                    float dist = length(diff);
                    float physical_dist = radius + other_radius; // 1.0m

                    // A) FUERZA SOCIAL (Cortesía)
                    // Si está cerca pero no tocando (entre 1.0m y 1.5m), empujar suavemente con steering
                    if (dist < social_threshold && dist > physical_dist) {
                        float power = (social_threshold - dist) / (social_threshold - physical_dist);
                        vec3 push_dir = normalize(diff);
                        // Esto modifica la VELOCIDAD futura, no la posición actual
                        vel += push_dir * power * 10.0 * params.delta_time; 
                    }

                    // B) CORRECCIÓN FÍSICA (Contacto)
                    // Si está tocando (dist < 1.0m)
                    if (dist < physical_dist && dist > 0.0001) {
                        float overlap = physical_dist - dist;
                        vec3 push_dir = normalize(diff);
                        
                        // FACTOR DE RIGIDEZ ("Stiffness"): 
                        // 0.5 = Rígido (resuelve 100% del choque repartido entre dos).
                        // 0.1 = Esponjoso (cede ante la presión).
                        // Usamos 0.2 para permitir que la multitud se comprima.
                        float stiffness = 0.2; 
                        
                        predicted_pos += push_dir * (overlap * stiffness);
                        
                        contact_count++;
                        total_overlap += overlap;
                    }
                }
            }
        }
    }

    // --- CAPA 4: MUERTE POR PRESIÓN EXTREMA ---
    // Si la compresión total (suma de overlaps) es muy alta, muere.
    // Esto es mejor que solo contar vecinos, porque mide "cuánto" te aplastan.
    if (total_overlap > 1.5) { // 1.5 metros de solapamiento acumulado es letal
        agents[idx].color = vec4(0.0, 0.0, 0.0, 0.0);
        agents[idx].velocity = vec4(0.0);
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