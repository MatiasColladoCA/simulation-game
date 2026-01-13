#[compute]
#version 450

// Tamaño de grupo
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
    vec4 color;      // rgb: color, w: LIFE
};

// --- BINDINGS (Tus bindings correctos) ---
layout(set = 0, binding = 0, std430) restrict buffer AgentBuffer { AgentDataSphere agents[]; };
layout(set = 0, binding = 1, std430) restrict buffer GridBuffer { uint density_grid[]; };
layout(set = 0, binding = 2, rgba32f) uniform writeonly image2D pos_texture;
layout(set = 0, binding = 3, rgba32f) uniform writeonly image2D col_texture;
layout(set = 0, binding = 4) uniform samplerCube height_map;
layout(set = 0, binding = 5) uniform samplerCube vector_field;
layout(set = 0, binding = 6, r8) uniform image3D density_texture_out;
layout(set = 0, binding = 7, std430) restrict buffer CounterBuffer { uint counters[]; };
layout(set = 0, binding = 8, std430) readonly buffer POIBuffer { vec4 pois[]; }; 
// layout(set = 0, binding = 9, std430) restrict buffer DeadListBuffer { uint free_indices[]; };

// --- PUSH CONSTANTS BLINDADOS (La clave para que funcione con tu C#) ---
// Usamos vec4 para alineación perfecta de 16 bytes
layout(push_constant) uniform Params {
    vec4 v0; // x:delta, y:time, z:radius, w:scale
    vec4 v1; // x:height, y:custom_param, z:phase, w:grid_res
    vec4 v2; // x:tex_width, y:pad, z:pad, w:pad
} p;

// --- GETTERS (Traducción de Datos) ---
float GetDelta()        { return p.v0.x; }
float GetTime()         { return p.v0.y; }
float GetRadius()       { return p.v0.z; }
float GetNoiseScale()   { return p.v0.w; }
float GetNoiseHeight()  { return p.v1.x; }
uint  GetCustomParam()  { return floatBitsToUint(p.v1.y); } 
uint  GetPhase()        { return floatBitsToUint(p.v1.z); }
uint  GetGridRes()      { return floatBitsToUint(p.v1.w); }
uint  GetTexWidth()     { return floatBitsToUint(p.v2.x); }

// --- FUNCIONES AUXILIARES ---

uint get_grid_idx(ivec3 coord) {
    uint res = GetGridRes();
    ivec3 c = clamp(coord, ivec3(0), ivec3(int(res) - 1));
    return uint(c.x + c.y * int(res) + c.z * int(res) * int(res));
}

ivec3 get_grid_coord(vec3 pos) {
    float radius = GetRadius();
    float res = float(GetGridRes());
    float bounds = radius * 1.5;
    vec3 normalized_pos = (pos + bounds) / (bounds * 2.0); 
    return ivec3(clamp(normalized_pos * res, vec3(0.0), vec3(res - 1.0)));
}

float read_density(ivec3 coord) {
    uint idx = get_grid_idx(coord);
    return float(density_grid[idx]); 
}

// --- FASE 0: CLEAR ---
void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    uint res = GetGridRes();
    uint total_cells = res * res * res;
    if (idx >= total_cells) return;
    density_grid[idx] = 0;
}

// --- FASE 1: POPULATE GRID ---
void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= GetCustomParam()) return;

    if (agents[idx].color.w <= 0.001) return; 

    ivec3 coord = get_grid_coord(agents[idx].position.xyz);
    uint grid_idx = get_grid_idx(coord);
    atomicAdd(density_grid[grid_idx], 1);
}




// --- FASE 2: UPDATE SIMULATION (MOVIMIENTO AAA + ALTURA CORREGIDA) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= GetCustomParam()) return;
    
    AgentDataSphere me = agents[idx];

    // 0. FILTRO DE EXISTENCIA
    if (me.color.w <= 0.001) return;

    // --- PARTE 1: CEREBRO (ESTADOS Y TIMERS) ---
    // Usamos .w de posición como Timer y .w de velocidad como Estado (0=Idle, 1=Moving)
    me.position.w -= GetDelta(); 
    
    if (me.position.w <= 0.0) {
        // Se acabó el tiempo: Cambiar de estado
        float current_state = me.velocity.w;
        
        if (current_state > 0.5) {
            // Estaba moviéndose -> A descansar (Idle)
            me.velocity.w = 0.0; 
            me.position.w = 1.0 + (fract(sin(float(idx)*12.989) * 43758.545) * 2.0); // 1-3 segs descanso
        } else {
            // Estaba descansando -> A moverse
            me.velocity.w = 1.0;
            me.position.w = 4.0 + (fract(sin(float(idx)*78.233) * 43758.545) * 4.0); // 4-8 segs viaje
        }
    }

    // --- PARTE 2: MOVIMIENTO LATERAL (NAVEGACIÓN) ---
    float is_moving = me.velocity.w;
    vec3 current_pos = me.position.xyz;
    vec3 dir_normal = normalize(current_pos); // Dirección "Arriba" local
    
    if (is_moving > 0.5) {
        // A. LEER VECTOR FIELD (Corrientes globales)
        vec3 flow = textureLod(vector_field, dir_normal, 0.0).rgb;
        // Proyectar sobre el plano tangente (eliminar componente vertical)
        flow = normalize(flow - dot(flow, dir_normal) * dir_normal);

        // B. LEER INFLUENCIA POI (Gradiente de atracción)
        // Calculamos hacia dónde aumenta la densidad en la textura 3D
        ivec3 g_coord = get_grid_coord(current_pos);
        ivec3 size_3d = imageSize(density_texture_out);
        
        // Muestrear vecinos para obtener gradiente (slope)
        float val_c = imageLoad(density_texture_out, clamp(g_coord, ivec3(0), size_3d-1)).r;
        float val_x = imageLoad(density_texture_out, clamp(g_coord + ivec3(1,0,0), ivec3(0), size_3d-1)).r;
        float val_y = imageLoad(density_texture_out, clamp(g_coord + ivec3(0,1,0), ivec3(0), size_3d-1)).r;
        float val_z = imageLoad(density_texture_out, clamp(g_coord + ivec3(0,0,1), ivec3(0), size_3d-1)).r;
        
        vec3 poi_dir = vec3(val_x - val_c, val_y - val_c, val_z - val_c);
        if (length(poi_dir) > 0.001) poi_dir = normalize(poi_dir);

        // C. MEZCLA DE FUERZAS
        // 40% Flow Field, 60% Atracción a POIs (ajustable)
        vec3 target_dir = normalize(flow * 0.4 + poi_dir * 0.6);
        if (length(target_dir) < 0.001) target_dir = normalize(me.velocity.xyz + vec3(0.01));

        // D. FÍSICA (ACELERACIÓN)
        float speed = 30.0; // Velocidad de desplazamiento
        vec3 desired_vel = target_dir * speed;
        vec3 steering = (desired_vel - me.velocity.xyz) * 4.0; // Fuerza de viraje
        
        me.velocity.xyz += steering * GetDelta();
        
        // Aplicar movimiento PROVISIONAL (esto los desalinea del suelo un poco)
        me.position.xyz += me.velocity.xyz * GetDelta();
        
    } else {
        // Fricción si está quieto
        me.velocity.xyz *= 0.90; 
        me.position.xyz += me.velocity.xyz * GetDelta();
    }

    // --- PARTE 3: SNAPPING AL SUELO (TU LÓGICA DE ORO) ---
    // Una vez movidos lateralmente, forzamos la altura correcta.
    
    vec3 final_dir = normalize(me.position.xyz);
    
    // 1. LEER EL VALOR CRUDO
    float raw_val = texture(height_map, final_dir).r; 

    // 2. CALIBRACIÓN (Tu configuración)
    float scale_factor = 1.0; 
    float height_meters = raw_val * scale_factor;
    

    // 3. CALCULAR RADIO OBJETIVO
    float target_radius = GetRadius() + height_meters;

    // 4. APLICAR POSICIÓN FINAL
    // Sobrescribimos la posición para pegarlo al radio exacto
    vec3 final_pos = final_dir * target_radius;
    
    me.position.xyz = final_pos;

    me.color.rgb = vec3(1.0);

    // --- GUARDADO ---
    agents[idx] = me;

    // Actualizar Texturas
    int tex_w = int(GetTexWidth());
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);
    
    imageStore(pos_texture, coord_tex, vec4(final_pos, me.position.w));
    
    // // Debug Color: Verde si se mueve, Azul si descansa
    // vec3 state_color = (is_moving > 0.5) ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
    // // Mezclar con altura para no perder ese debug (Rojo si muy alto)
    // float h_debug = clamp(height_meters * 0.05, 0.0, 1.0);
    // vec3 final_debug_col = mix(state_color, vec3(1.0, 0.0, 0.0), h_debug);
    
    imageStore(col_texture, coord_tex, vec4(1.0, 1.0, 1.0, 1.0));
}


// --- FASE 3: PAINT POIS ---
void phase_paint_pois() {
    ivec3 coords = ivec3(gl_GlobalInvocationID.xyz);
    uint res = GetGridRes();
    if (any(greaterThanEqual(coords, ivec3(res)))) return;

    float planet_r = GetRadius();
    float bounds = planet_r * 1.5;
    vec3 world_pos = (vec3(coords) / float(res)) * (bounds * 2.0) - bounds;
    
    float max_influence = 0.0;

    for(int i = 0; i < 16; i++) {
        if (pois[i].w <= 0.001) continue; 
        vec3 poi_pos = normalize(pois[i].xyz) * planet_r; 
        float poi_radius = pois[i].w * planet_r;
        float d = distance(world_pos, poi_pos);
        float influence = 1.0 - smoothstep(0.0, poi_radius, d);
        max_influence = max(max_influence, influence);
    }
    imageStore(density_texture_out, coords, vec4(max_influence, 0, 0, 1));
}

// --- MAIN ---
void main() {
    // Usar Getter
    uint ph = GetPhase();
    if (ph == 0) phase_clear();
    else if (ph == 1) phase_populate();
    else if (ph == 2) phase_update();
    else if (ph == 3) phase_paint_pois();
}