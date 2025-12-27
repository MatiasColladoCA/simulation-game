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

// CAMBIO: El Grid ahora es solo un array de enteros (Contador de densidad)
layout(set = 0, binding = 1, std430) buffer DensityBuffer { uint density_grid[]; };

layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_image_out;
layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D color_image_out;
layout(set = 0, binding = 4) uniform samplerCube height_map;
layout(set = 0, binding = 5) uniform samplerCube vector_field;

// --- PUSH CONSTANTS ---
layout(push_constant) uniform Params {
    float delta_time;
    float time;
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint agent_count;
    uint phase;       
    uint grid_res;   // Resolución de la grilla (ej: 64)
    uint tex_width; 
} params;

// --- FUNCIONES DE GRILLA ---

// Convierte posición 3D mundo -> Coordenada 3D grilla
ivec3 get_grid_coord(vec3 pos) {
    float limit = params.planet_radius * 1.5; // Espacio de simulación un poco más grande que el planeta
    vec3 norm = (pos + limit) / (limit * 2.0); // Normalizar a 0..1
    return ivec3(floor(norm * float(params.grid_res)));
}

// Convierte Coordenada 3D -> Índice 1D (para el buffer)
uint get_grid_idx(ivec3 coord) {
    // Clamp para seguridad (evitar salir del array)
    ivec3 c = clamp(coord, ivec3(0), ivec3(int(params.grid_res) - 1));
    return uint(c.x + c.y * int(params.grid_res) + c.z * int(params.grid_res) * int(params.grid_res));
}

// Leer densidad de una celda de manera segura
float read_density(ivec3 coord) {
    if (coord.x < 0 || coord.y < 0 || coord.z < 0 || 
        coord.x >= int(params.grid_res) || coord.y >= int(params.grid_res) || coord.z >= int(params.grid_res)) {
        return 0.0;
    }
    return float(density_grid[get_grid_idx(coord)]);
}

// --- FASES ---

void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    uint total_cells = params.grid_res * params.grid_res * params.grid_res;
    if (idx >= total_cells) return;
    density_grid[idx] = 0;
}

void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;
    if (agents[idx].color.w < 0.5) return; 

    ivec3 coord = get_grid_coord(agents[idx].position.xyz);
    uint grid_idx = get_grid_idx(coord);
    
    // Aquí el atomicAdd es mucho más rápido porque el buffer es pequeño y simple
    atomicAdd(density_grid[grid_idx], 1);
}

void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    Agent me = agents[idx];
    int tex_w = int(params.tex_width);
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);

    vec3 pos = me.position.xyz;
    vec3 vel = me.velocity.xyz;
    float max_speed = me.velocity.w;

    // 1. NAVEGACIÓN (Flowfield & Terreno)
    vec3 dir_norm = normalize(pos);
    float h_val = textureLod(height_map, dir_norm, 0.0).r;
    float terrain_floor = params.planet_radius + (h_val * params.noise_height);
    vec3 flow_dir = textureLod(vector_field, dir_norm, 0.0).rgb;
    
    // Fuerza de dirección base
    vec3 acc_force = (flow_dir * max_speed - vel) * 4.0;

    // 2. REPULSIÓN POR DENSIDAD (GRADIENTE)
    // Calculamos hacia dónde disminuye la densidad para movernos hacia allá
    ivec3 g_coord = get_grid_coord(pos);
    
    // Muestreo de gradiente (vecinos)
    float d_left = read_density(g_coord + ivec3(-1, 0, 0));
    float d_right = read_density(g_coord + ivec3(1, 0, 0));
    float d_down = read_density(g_coord + ivec3(0, -1, 0));
    float d_up = read_density(g_coord + ivec3(0, 1, 0));
    float d_back = read_density(g_coord + ivec3(0, 0, -1));
    float d_fwd = read_density(g_coord + ivec3(0, 0, 1));
    
    // Vector Gradiente: Apunta hacia donde AUMENTA la densidad
    vec3 gradient = vec3(d_right - d_left, d_up - d_down, d_fwd - d_back);
    
    // Nosotros queremos ir al contrario (-gradiente) para escapar de la multitud
    // Multiplicamos por un factor de "Presión" (ej: 10.0)
    acc_force -= gradient * 15.0; 

    // Ruido extra si la densidad local es muy alta (para desatascar)
    float local_density = read_density(g_coord);
    if (local_density > 5.0) {
         float noise_seed = float(idx) * 0.1 + params.time;
         vec3 jitter = vec3(sin(noise_seed), cos(noise_seed * 1.3), sin(noise_seed * 0.7));
         acc_force += jitter * local_density * 2.0;
    }

    // 3. INTEGRACIÓN FÍSICA
    vel += acc_force * params.delta_time;
    
    // Limitar velocidad
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    // 4. SNAP TO TERRAIN
    float current_r = length(pos);
    if (current_r < terrain_floor) {
        pos = normalize(pos) * terrain_floor;
    } else {
        // Gravedad suave hacia el suelo
        pos = normalize(pos) * max(terrain_floor, current_r - (10.0 * params.delta_time));
    }
    
    // Alinear velocidad al suelo
    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal; 

    // Guardar
    me.position.xyz = pos;
    me.velocity.xyz = vel;
    agents[idx] = me;

    imageStore(pos_image_out, coord_tex, me.position);
    imageStore(color_image_out, coord_tex, me.color);
}

void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else phase_update();
}