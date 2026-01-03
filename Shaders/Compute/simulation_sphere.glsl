#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// Constantes de estado (Obligatorias para phase_update)
#define STATE_IDLE 0.0
#define STATE_MOVING 1.0
#define STATE_WORK 2.0

struct AgentDataSphere {
    vec4 position;   // w: state_timer
    vec4 velocity;   // w: current_state
    vec4 group_data; // x: group_id, y: density_time
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
layout(set = 0, binding = 8, std430) buffer POIBuffer { vec4 pois[]; }; 

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
    uint pad0; uint pad1; uint pad2; 
} params;



void phase_paint_pois() {
    ivec3 tex_size = imageSize(density_texture_out);
    ivec3 coords = ivec3(gl_GlobalInvocationID.xyz);
    if (any(greaterThanEqual(coords, tex_size))) return;

    vec3 world_pos = (vec3(coords) / vec3(params.grid_res)) * 250.0 - 125.0;
    float max_influence = 0.0;

    // Iterar POIs (Máximo 16 para performance)
    for(int i = 0; i < 16; i++) {
        float d = distance(world_pos, pois[i].xyz);
        float influence = 1.0 - smoothstep(0.0, pois[i].w, d);
        max_influence = max(max_influence, influence);
    }

    imageStore(density_texture_out, coords, vec4(max_influence, 0, 0, 1));
}


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
    if (idx >= params.custom_param) return;
    
    AgentDataSphere me = agents[idx];
    if (me.color.w < 0.5) return;

    // 1. TIMERS Y ESTADOS (Chequeo robusto)
    me.position.w -= params.delta;
    if (me.position.w <= 0.0) {
        // Toggle robusto: si es > 0.5 (Moving), pasa a 0.0 (Idle) y viceversa
        me.velocity.w = (me.velocity.w > 0.5) ? STATE_IDLE : STATE_MOVING;
        me.position.w = 2.0 + (fract(sin(float(idx) * 12.9898) * 43758.5453) * 5.0);
    }

    // 2. MOVIMIENTO (INTEGRACIÓN DE INFLUENCIA)
    float is_moving = step(0.5, me.velocity.w);
    vec3 current_normal = normalize(me.position.xyz);

    if (is_moving > 0.5) {
        // A. Vector Field Estático (Ruido/Viento)
        vec3 static_flow = textureLod(vector_field, current_normal, 0.0).rgb;
        static_flow -= dot(static_flow, current_normal) * current_normal; // Tangencial

        // B. Influencia de POIs (Cálculo de Gradiente Local)
        // Leemos la celda actual y sus vecinas en la grilla 3D
        ivec3 g_coord = get_grid_coord(me.position.xyz);
        float val_c = imageLoad(density_texture_out, g_coord).r;
        float val_x = imageLoad(density_texture_out, g_coord + ivec3(1, 0, 0)).r;
        float val_y = imageLoad(density_texture_out, g_coord + ivec3(0, 1, 0)).r;
        float val_z = imageLoad(density_texture_out, g_coord + ivec3(0, 0, 1)).r;

        // El gradiente apunta hacia donde el valor de la influencia aumenta
        vec3 poi_grad = vec3(val_x - val_c, val_y - val_c, val_z - val_c);
        
        // C. Mezcla de Fuerzas (30% Ruido, 70% Atracción)
        // Si el gradiente es casi cero, poi_dir será cero y solo actuará el static_flow
        vec3 poi_dir = (length(poi_grad) > 0.001) ? normalize(poi_grad) : vec3(0.0);
        vec3 final_dir = normalize(static_flow * 0.3 + poi_dir * 0.7);

        vec3 target_vel = final_dir * 50.0; 
        vec3 acc = (target_vel - me.velocity.xyz) * 4.0;
        
        me.velocity.xyz += acc * params.delta;
        me.position.xyz += me.velocity.xyz * params.delta;
    } else {
        me.velocity.xyz *= 0.9; 
    }

    // SNAPPING (Relieve)
    vec3 new_normal = normalize(me.position.xyz);
    float h = textureLod(height_map, new_normal, 0.0).r;
    float final_r = params.planet_radius + (h * params.noise_height);
    me.position.xyz = new_normal * final_r;

    // 3. AGRUPACIÓN Y COLOR
    // (Mismo bloque de agrupación existente)
    ivec3 g_coord_final = get_grid_coord(me.position.xyz);
    float d = read_density(g_coord_final);
    if (d > 10.0) {
        me.group_data.x = float(g_coord_final.x + g_coord_final.y * 100); 
        me.color.rgb = vec3(fract(me.group_data.x * 0.1), fract(me.group_data.x * 0.7), 0.5);
    }

    agents[idx] = me;

    // ACTUALIZACIÓN DE TEXTURAS DE SALIDA
    int tex_w = int(params.tex_width);
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);
    imageStore(pos_texture, coord_tex, me.position);
    imageStore(col_texture, coord_tex, me.color);
}

// void phase_paint_pois() {
//     // Para esta fase, invocamos el shader con el tamaño de la grilla (GRID_RES)
//     ivec3 coords = ivec3(gl_GlobalInvocationID.xyz);
//     if (any(greaterThanEqual(coords, ivec3(params.grid_res)))) return;

//     vec3 world_pos = (vec3(coords) / float(params.grid_res)) * 250.0 - 125.0;
//     float max_influence = 0.0;

//     for(int i = 0; i < 16; i++) {
//         if (pois[i].w <= 0.0) continue; // POI inactivo
//         float d = distance(world_pos, pois[i].xyz);
//         float influence = 1.0 - smoothstep(0.0, pois[i].w, d);
//         max_influence = max(max_influence, influence);
//     }

//     imageStore(density_texture_out, coords, vec4(max_influence, 0, 0, 1));
// }



void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else if (params.phase == 2) phase_update();
    else if (params.phase == 3) phase_paint_pois();
}
