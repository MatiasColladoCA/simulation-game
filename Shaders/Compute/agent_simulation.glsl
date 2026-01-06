#[compute]
#version 450

// Tamaño de grupo estándar
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// --- CONSTANTES ---
#define STATE_IDLE 0.0
#define STATE_MOVING 1.0
#define STATE_WORK 2.0

// --- ESTRUCTURAS ---
struct AgentDataSphere {
    vec4 position;   // xyz: pos, w: state_timer
    vec4 velocity;   // xyz: vel, w: current_state
    vec4 group_data; // x: group_id, y: density_time
    vec4 color;      // rgb: color, w: LIFE (0=Muerto, 1=Vivo)
};

// --- BINDINGS ---
// Buffer Principal de Agentes
layout(set = 0, binding = 0, std430) restrict buffer AgentBuffer { AgentDataSphere agents[]; };

// Grilla de Densidad (Contador de agentes por celda)
layout(set = 0, binding = 1, std430) restrict buffer GridBuffer { uint density_grid[]; };

// Texturas de salida para el Visualizer
layout(set = 0, binding = 2, rgba32f) uniform writeonly image2D pos_texture;
layout(set = 0, binding = 3, rgba32f) uniform writeonly image2D col_texture;

// Mapas del entorno (Solo lectura)
layout(set = 0, binding = 4) uniform samplerCube height_map;
layout(set = 0, binding = 5) uniform samplerCube vector_field;

// Mapa de Influencia 3D (POI Field) - Se lee en Update, se escribe en Paint
layout(set = 0, binding = 6, r8) uniform image3D density_texture_out;

// --- GESTIÓN DE MEMORIA AAA ---
// [0]=Alive Count, [1]=DeadStackPtr
layout(set = 0, binding = 7, std430) restrict buffer CounterBuffer { uint counters[]; };
// Buffer de POIs (Posición + Radio)
layout(set = 0, binding = 8, std430) readonly buffer POIBuffer { vec4 pois[]; }; 
// Stack de Muertos (Índices reciclables)
layout(set = 0, binding = 9, std430) restrict buffer DeadListBuffer { uint free_indices[]; };


// --- PARÁMETROS ---
layout(push_constant) uniform Params {
    float delta;
    float time;
    float planet_radius;
    float noise_scale;
    float noise_height;
    uint custom_param; // MaxAgents o GridRes
    uint phase;       
    uint grid_res;    
    uint tex_width;   
} params;

// --- FUNCIONES AUXILIARES ---

// Convierte coordenada 3D a índice lineal de array
uint get_grid_idx(ivec3 coord) {
    ivec3 c = clamp(coord, ivec3(0), ivec3(int(params.grid_res) - 1));
    return uint(c.x + c.y * int(params.grid_res) + c.z * int(params.grid_res) * int(params.grid_res));
}

// Convierte posición de mundo a coordenada de grilla 3D
ivec3 get_grid_coord(vec3 pos) {
    float bounds = params.planet_radius * 1.5;
    vec3 normalized_pos = (pos + bounds) / (bounds * 2.0); 
    // Clamp vital para no salirnos de la grilla si el agente vuela muy lejos
    return ivec3(clamp(normalized_pos * float(params.grid_res), vec3(0.0), vec3(float(params.grid_res) - 1.0)));
}

float read_density(ivec3 coord) {
    uint idx = get_grid_idx(coord);
    return float(density_grid[idx]); 
}

// --- FASE 0: CLEAR ---
void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    uint total_cells = params.grid_res * params.grid_res * params.grid_res;
    if (idx >= total_cells) return;
    density_grid[idx] = 0;
}

// --- FASE 1: POPULATE GRID ---
void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.custom_param) return; // custom_param = MaxAgents

    // Solo agentes vivos contribuyen a la densidad
    if (agents[idx].color.w <= 0.001) return; 

    ivec3 coord = get_grid_coord(agents[idx].position.xyz);
    uint grid_idx = get_grid_idx(coord);
    
    atomicAdd(density_grid[grid_idx], 1);
}

// --- FASE 2: UPDATE SIMULATION (CRÍTICA) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.custom_param) return; // custom_param = MaxAgents
    
    AgentDataSphere me = agents[idx];

    // 1. GESTIÓN DE VIDA (Salida rápida)
    if (me.color.w <= 0.001) {
        // Escribir ceros en las texturas visuales para que desaparezca
        int tex_w = int(params.tex_width);
        ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);
        imageStore(pos_texture, coord_tex, vec4(0));
        imageStore(col_texture, coord_tex, vec4(0));
        return; 
    }

    // 2. LÓGICA DE MUERTE
    bool should_die = false;
    // Ejemplo: Murió por salir demasiado lejos (3 veces el radio)
    if (length(me.position.xyz) > params.planet_radius * 3.0) should_die = true;
    
    if (should_die) {
        me.color.w = 0.0;     // Marcar muerto
        me.position = vec4(0.0); // Resetear posición para evitar artefactos
        
        // RECICLAR: Devolver ID al Stack
        uint stack_idx = atomicAdd(counters[1], 1);
        
        // Safety check: no escribir fuera del buffer de muertos
        if (stack_idx < params.custom_param) {
            free_indices[stack_idx] = idx;
        }
        
        agents[idx] = me; // Guardar estado de muerte
        return;
    }

    // 3. COMPORTAMIENTO (TIMERS)
    me.position.w -= params.delta; // state_timer
    if (me.position.w <= 0.0) {
        // Cambiar estado aleatoriamente o cíclicamente
        me.velocity.w = (me.velocity.w > 0.5) ? STATE_IDLE : STATE_MOVING;
        // Reiniciar timer con algo de aleatoriedad basada en el índice
        me.position.w = 2.0 + (fract(sin(float(idx) * 12.9898) * 43758.5453) * 5.0);
    }

    // 4. MOVIMIENTO
    float is_moving = step(0.5, me.velocity.w);
    vec3 current_pos = me.position.xyz;
    
    // Safety: Evitar normalizar vector cero
    vec3 current_normal = vec3(0, 1, 0);
    if (length(current_pos) > 0.001) current_normal = normalize(current_pos);

    if (is_moving > 0.5) {
        // A. Vector Field (Flujo global)
        vec3 static_flow = textureLod(vector_field, current_normal, 0.0).rgb;
        // Proyectar sobre la tangente para que no apunte hacia el cielo/suelo
        static_flow -= dot(static_flow, current_normal) * current_normal; 

        // B. Influencia POI (Gradiente local de la textura 3D)
        ivec3 g_coord = get_grid_coord(current_pos);
        
        // Clamp para no leer fuera de la textura 3D
        ivec3 size_3d = imageSize(density_texture_out);
        ivec3 c = clamp(g_coord, ivec3(0), size_3d - ivec3(1));
        ivec3 cx = clamp(g_coord + ivec3(1, 0, 0), ivec3(0), size_3d - ivec3(1));
        ivec3 cy = clamp(g_coord + ivec3(0, 1, 0), ivec3(0), size_3d - ivec3(1));
        ivec3 cz = clamp(g_coord + ivec3(0, 0, 1), ivec3(0), size_3d - ivec3(1));

        // Lectura
        float val_c = imageLoad(density_texture_out, c).r;
        float val_x = imageLoad(density_texture_out, cx).r;
        float val_y = imageLoad(density_texture_out, cy).r;
        float val_z = imageLoad(density_texture_out, cz).r;

        // Gradiente: Hacia donde aumenta el valor (atracción)
        vec3 poi_grad = vec3(val_x - val_c, val_y - val_c, val_z - val_c);
        vec3 poi_dir = vec3(0.0);
        if (length(poi_grad) > 0.0001) poi_dir = normalize(poi_grad);
        
        // C. Mezcla de Fuerzas
        // 40% Flujo del planeta, 60% Atracción a POIs
        vec3 final_dir = normalize(static_flow * 0.4 + poi_dir * 0.6);
        
        // Si no hay fuerza (ej. en medio de la nada), mantener inercia
        if (length(final_dir) < 0.001) final_dir = normalize(me.velocity.xyz + vec3(0.001));

        // Física simple (Aceleración)
        float speed = 40.0;
        vec3 target_vel = final_dir * speed; 
        vec3 acc = (target_vel - me.velocity.xyz) * 4.0; // Amortiguación
        
        me.velocity.xyz += acc * params.delta;
        me.position.xyz += me.velocity.xyz * params.delta;
    } else {
        // Fricción en estado IDLE
        me.velocity.xyz *= 0.9; 
    }

    // 5. SNAPPING (Pegar al suelo)
    // Recalcular normal tras movimiento
    vec3 next_normal = vec3(0, 1, 0);
    if (length(me.position.xyz) > 0.001) next_normal = normalize(me.position.xyz);
    
    // Leer altura del terreno
    float h = textureLod(height_map, next_normal, 0.0).r;
    float final_r = params.planet_radius + (h * params.noise_height);
    
    // Interpolación suave de altura (Lerp) para evitar "teletransportes" en pendientes
    float current_r = length(me.position.xyz);
    float lerped_r = mix(current_r, final_r, params.delta * 10.0);
    
    me.position.xyz = next_normal * lerped_r;

    // 6. COLOR POR DENSIDAD (Visualización de grupos)
    ivec3 g_coord_final = get_grid_coord(me.position.xyz);
    float d = read_density(g_coord_final);
    
    // Si la densidad es alta (>5 vecinos), tinte rojo. Si no, blanco.
    vec3 target_col = (d > 5.0) ? vec3(1.0, 0.2, 0.2) : vec3(1.0);
    me.color.rgb = mix(me.color.rgb, target_col, params.delta * 2.0); // Lerp suave de color

    // 7. GUARDADO FINAL
    agents[idx] = me;

    // Actualizar Texturas para el Visual Shader
    int tex_w = int(params.tex_width);
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);
    imageStore(pos_texture, coord_tex, me.position);
    imageStore(col_texture, coord_tex, me.color);
}

// --- FASE 3: PAINT POIS ---
void phase_paint_pois() {
    // Invocado en volumen 3D (GridRes x GridRes x GridRes)
    ivec3 coords = ivec3(gl_GlobalInvocationID.xyz);
    if (any(greaterThanEqual(coords, ivec3(params.grid_res)))) return;

    // Convertir coord textura -> Mundo
    float bounds = params.planet_radius * 1.5;
    vec3 world_pos = (vec3(coords) / float(params.grid_res)) * (bounds * 2.0) - bounds;
    
    float max_influence = 0.0;

    // Iterar POIs estáticos (Máximo 16 para performance)
    for(int i = 0; i < 16; i++) {
        // pois[i].w es el Radio de influencia. Si es <= 0, ignorar.
        if (pois[i].w <= 0.001) continue; 
        
        // pois[i].xyz es Dirección Normalizada (si viene del EnvironmentManager)
        // Convertimos a posición en superficie
        vec3 poi_pos = normalize(pois[i].xyz) * params.planet_radius; 
        float poi_radius = pois[i].w * params.planet_radius;

        float d = distance(world_pos, poi_pos);
        // Influencia suave (1.0 en el centro, 0.0 en el borde del radio)
        float influence = 1.0 - smoothstep(0.0, poi_radius, d);
        max_influence = max(max_influence, influence);
    }

    imageStore(density_texture_out, coords, vec4(max_influence, 0, 0, 1));
}

// --- MAIN DISPATCHER ---
void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else if (params.phase == 2) phase_update();
    else if (params.phase == 3) phase_paint_pois();
}