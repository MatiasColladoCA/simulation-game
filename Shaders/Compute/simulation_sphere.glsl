#[compute]
#version 450

// --- REEMPLAZAR DESDE EL INICIO HASTA EL FINAL DE 'params' ---

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct AgentDataSphere {
    vec4 position;
    vec4 target;
    vec4 velocity;
    vec4 color;
};

layout(set = 0, binding = 0, std430) buffer AgentBuffer { AgentDataSphere agents[]; };
layout(set = 0, binding = 1, std430) buffer GridBuffer { uint density_grid[]; };
layout(set = 0, binding = 2, rgba32f) uniform image2D pos_texture;
layout(set = 0, binding = 3, rgba32f) uniform image2D col_texture;
layout(set = 0, binding = 4) uniform samplerCube height_map;
layout(set = 0, binding = 5) uniform samplerCube vector_field;
layout(set = 0, binding = 6, r8) uniform image3D density_texture_out;
layout(set = 0, binding = 7, std430) buffer CounterBuffer { uint active_count; };

layout(push_constant) uniform Params {
    float delta;
    float time;
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint custom_param; 
    uint phase;       
    uint grid_res;    
    uint tex_width;
    uint pad0;         // Padding para alinear a 48 bytes (9 + 3 = 12 variables)
    uint pad1;
    uint pad2;   
} params;


// --- FUNCIONES DE GRILLA ---

// Convierte Coordenada 3D -> Índice 1D (para el buffer)
uint get_grid_idx(ivec3 coord) {
    // Clamp para seguridad (evitar salir del array)
    ivec3 c = clamp(coord, ivec3(0), ivec3(int(params.grid_res) - 1));
    return uint(c.x + c.y * int(params.grid_res) + c.z * int(params.grid_res) * int(params.grid_res));
}

ivec3 get_grid_coord(vec3 pos) {
    vec3 normalized_pos = (pos + 125.0) / 250.0; // Ajustado al radio de seguridad
    return ivec3(clamp(normalized_pos * float(params.grid_res), vec3(0.0), vec3(float(params.grid_res - 1))));
}

float read_density(ivec3 coord) {
    uint idx = uint(coord.x + coord.y * int(params.grid_res) + coord.z * int(params.grid_res * params.grid_res));
    return float(density_grid[idx]) / 255.0;
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
    // params.agent_count no existía, usamos custom_param enviado desde C#
    if (idx >= params.custom_param) return; 
    if (agents[idx].color.w < 0.5) return; 

    ivec3 coord = get_grid_coord(agents[idx].position.xyz);
    uint grid_idx = get_grid_idx(coord);
    
    atomicAdd(density_grid[grid_idx], 1);
}

void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    // Reemplazado el literal 5000 por el parámetro dinámico
    if (idx >= params.custom_param) return;

    if (agents[idx].position.w < 0.1) return;

    AgentDataSphere me = agents[idx];    int tex_w = int(params.tex_width);
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);

    vec3 pos = me.position.xyz;
    vec3 vel = me.velocity.xyz;
    float max_speed = me.velocity.w;

    // 2. NAVEGACIÓN (Flowfield & Terreno)
    vec3 dir_norm = normalize(pos);
    
    // textureLod es obligatorio en Compute Shaders para samplers
    float h_val = textureLod(height_map, dir_norm, 0.0).r;
    float terrain_floor = params.planet_radius + (h_val * params.noise_height);
    vec3 flow_dir = textureLod(vector_field, dir_norm, 0.0).rgb;
    
    // Fuerza de dirección base (Steering)
    vec3 acc_force = (flow_dir * max_speed - vel) * 4.0;

    // 3. REPULSIÓN POR DENSIDAD (GRADIENTE)
    // get_grid_coord y read_density deben estar definidos arriba en tu archivo
    ivec3 g_coord = ivec3(((pos + 120.0) / 240.0) * float(params.grid_res));
    
    float d_left  = read_density(g_coord + ivec3(-1, 0, 0));
    float d_right = read_density(g_coord + ivec3(1, 0, 0));
    float d_down  = read_density(g_coord + ivec3(0, -1, 0));
    float d_up    = read_density(g_coord + ivec3(0, 1, 0));
    float d_back  = read_density(g_coord + ivec3(0, 0, -1));
    float d_fwd   = read_density(g_coord + ivec3(0, 0, 1));
    
    vec3 gradient = vec3(d_right - d_left, d_up - d_down, d_fwd - d_back);
    
    // Repulsión: -gradient * factor_presión
    acc_force -= gradient * 15.0; 

    // Ruido de desatasco (Jitter)
    float local_density = read_density(g_coord);
    if (local_density > 5.0) {
         float noise_seed = float(idx) * 0.1 + params.time;
         vec3 jitter = vec3(sin(noise_seed), cos(noise_seed * 1.3), sin(noise_seed * 0.7));
         acc_force += jitter * local_density * 2.0;
    }

    // 4. INTEGRACIÓN FÍSICA (Euler)
    // Usamos params.delta para coincidir con el struct Params
    vel += acc_force * params.delta;
    
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta;

    // 5. RESTRICCIÓN ESFÉRICA Y SNAP
    float current_r = length(pos);
    if (current_r < terrain_floor) {
        pos = normalize(pos) * terrain_floor;
    } else {
        // Gravedad suave hacia la superficie
        pos = normalize(pos) * max(terrain_floor, current_r - (10.0 * params.delta));
    }
    
    // Proyectar velocidad al plano tangente (evita que intenten "atravesar" el suelo)
    vec3 surface_normal = normalize(pos);
    vel = vel - dot(vel, surface_normal) * surface_normal; 

    // 6. GUARDAR DATOS (Buffer y Texturas)
    me.position.xyz = pos;
    me.velocity.xyz = vel;
    agents[idx] = me;

    // Sincronizado con bindings de AgentSystem.cs
    imageStore(pos_texture, coord_tex, me.position);
    imageStore(col_texture, coord_tex, me.color);
    }



void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else phase_update();
}
