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

// --- GESTIÓN DE MEMORIA AAA ---
// [0]=Alive Count, [1]=DeadStackPtr
layout(set = 0, binding = 7, std430) restrict buffer CounterBuffer { uint counters[]; };
// Buffer de POIs (Posición + Radio) - SIN USAR
// layout(set = 0, binding = 8, std430) readonly buffer POIBuffer { vec4 pois[]; };
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

// Añade esta función auxiliar para debug
float get_height_debug(vec3 normal_dir) {
    float h = textureLod(height_map, normal_dir, 0.0).r;
    
    // DEBUG: Imprime valores (activa debug en RenderDoc)
    // if (gl_GlobalInvocationID.x == 0) {
    //     printf("Heightmap value: %f, Expected range: 0.0-1.0", h);
    // }
    
    return h;
}

// Generador de números pseudo-aleatorios rápido (Hash)
float hash12(vec2 p) {
    vec3 p3  = fract(vec3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Ruido 1D suave para el cambio de dirección (Wander)
float noise1(float t) {
    float i = floor(t);
    float f = fract(t);
    f = f * f * (3.0 - 2.0 * f); // Smoothstep
    return mix(hash12(vec2(i, 0.0)), hash12(vec2(i + 1.0, 0.0)), f);
}

// --- FASE 2: UPDATE SIMULATION (CRÍTICA) ---
// --- FASE 2: UPDATE SIMULATION (MOVIMIENTO ORGÁNICO AAA) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.custom_param) return;
    
    AgentDataSphere me = agents[idx];

    // --- 1. SPAWN / RECOVERY (Si está muerto o perdido, respawnear) ---
    // Usamos la lógica de Fibonacci que ya funcionó para spawnear
    if (me.color.w <= 0.001 || length(me.position.xyz) < 1.0) {
        float i = float(idx);
        float n = float(params.custom_param);
        float phi = acos(1.0 - 2.0 * (i + 0.5) / n);
        float theta = 3.14159265 * (1.0 + sqrt(5.0)) * i;
        vec3 n_dir = vec3(sin(phi)*cos(theta), sin(phi)*sin(theta), cos(phi));
        
        float h = textureLod(height_map, n_dir, 0.0).r;
        me.position.xyz = n_dir * (params.planet_radius + h);
        me.position.w = hash12(vec2(float(idx), params.time)) * 5.0; // Timer aleatorio
        me.color = vec4(1.0); 
        me.velocity = vec4(0.0); // W=0 (Idle)
        agents[idx] = me;
        return;
    }

    // --- 2. MÁQUINA DE ESTADOS (Cerebro) ---
    // me.velocity.w almacena el estado: 0.0 = IDLE, 1.0 = WALKING
    // me.position.w almacena el timer: Cuenta regresiva para cambiar de estado
    
    me.position.w -= params.delta;

    if (me.position.w <= 0.0) {
        // ¡CAMBIO DE ESTADO!
        if (me.velocity.w < 0.5) {
            // Estaba IDLE -> Pasa a WALK
            me.velocity.w = 1.0; 
            // Camina entre 2 y 8 segundos
            me.position.w = 2.0 + hash12(vec2(float(idx), params.time)) * 6.0; 
        } else {
            // Estaba WALK -> Pasa a IDLE
            me.velocity.w = 0.0;
            // Descansa entre 1 y 3 segundos
            me.position.w = 1.0 + hash12(vec2(float(idx), params.time + 10.0)) * 2.0;
        }
    }

    // --- 3. FÍSICA DE MOVIMIENTO ---
    vec3 current_pos = me.position.xyz;
    vec3 up = normalize(current_pos); // Normal de la esfera en mi posición

    if (me.velocity.w > 0.5) { // ESTADO: WALK
        
        // A. WANDER (Deambular)
        // Usamos el ID del agente y el tiempo para generar un ángulo "único" que cambia suavemente
        float wander_noise = noise1(params.time * 0.5 + float(idx) * 0.1); // Ruido lento
        float angle = wander_noise * 6.2831; // 0 a 360 grados

        // Construimos un vector tangente a la superficie
        vec3 arbitrary = (abs(up.y) < 0.9) ? vec3(0,1,0) : vec3(1,0,0);
        vec3 tangent = normalize(cross(up, arbitrary));
        vec3 bitangent = cross(up, tangent);
        
        // Dirección de "paseo" en espacio tangente
        vec3 wander_dir = normalize(tangent * cos(angle) + bitangent * sin(angle));

        // B. VECTOR FIELD (Influencia Ambiental)
        // Leemos el mapa de flujo, pero no lo obedecemos ciegamente
        vec3 flow = textureLod(vector_field, up, 0.0).rgb;
        flow -= dot(flow, up) * up; // Proyectar al suelo
        if (length(flow) > 0.01) flow = normalize(flow);

        // C. DENSITY AVOIDANCE (Evitar multitudes)
        // Miramos "hacia adelante"
        ivec3 grid_coord = get_grid_coord(current_pos + wander_dir * 2.0);
        float density_ahead = read_density(grid_coord);
        vec3 avoidance = vec3(0.0);
        
        if (density_ahead > 3.0) { // Si hay más de 3 vecinos adelante
            // Fuerza repulsiva hacia atrás o al costado
             avoidance = -wander_dir * 2.0; 
        }

        // --- MEZCLA DE FUERZAS (LA RECETA MÁGICA) ---
        // 60% Voluntad propia (Wander), 30% Terreno (Flow), 100% Repulsión si aplica
        vec3 target_dir = normalize(wander_dir * 0.6 + flow * 0.3 + avoidance);

        // Aceleración
        vec3 target_vel = target_dir * 15.0; // Velocidad de caminata (ajustar a gusto)
        vec3 acc = (target_vel - me.velocity.xyz) * 4.0; // Inercia/Agilidad
        
        me.velocity.xyz += acc * params.delta;

    } else { // ESTADO: IDLE
        // Fricción rápida para detenerse
        me.velocity.xyz = mix(me.velocity.xyz, vec3(0.0), params.delta * 5.0);
    }

    // Aplicar Velocidad
    me.position.xyz += me.velocity.xyz * params.delta;

    // --- 4. SNAPPING AL TERRENO (CRÍTICO) ---
    // Mantenemos tu lógica corregida para leer la altura real
    vec3 next_normal = normalize(me.position.xyz);
    float h = textureLod(height_map, next_normal, 0.0).r; // h ya tiene escala en tu baker
    
    // Suavizado para que no "tiemble" al subir montañas
    float target_r = params.planet_radius + h;
    float current_r = length(me.position.xyz);
    
    // Lerp agresivo para mantenerse pegado (20.0)
    float new_r = mix(current_r, target_r, params.delta * 20.0);
    me.position.xyz = next_normal * new_r;

    // --- 5. VISUALIZACIÓN ---
    // Color Debug: Blanco si camina, Gris si espera
    float state_color = me.velocity.w > 0.5 ? 1.0 : 0.5;
    me.color.rgb = vec3(state_color); 
    
    // Tinte rojo si hay mucha densidad (Crowding debug)
    ivec3 g_coord = get_grid_coord(me.position.xyz);
    if (read_density(g_coord) > 5.0) me.color.rgb = mix(me.color.rgb, vec3(1,0,0), 0.5);

    // Guardar
    agents[idx] = me;
    
    // Salida a Textura
    int tex_w = int(params.tex_width);
    ivec2 coord_tex = ivec2(int(idx) % tex_w, int(idx) / tex_w);
    imageStore(pos_texture, coord_tex, me.position);
    imageStore(col_texture, coord_tex, me.color);
}
// --- FASE 3: PAINT POIS (DESACTIVADO - Faltan bindings 6 y 8) ---
// void phase_paint_pois() {
//     // Invocado en volumen 3D (GridRes x GridRes x GridRes)
//     ivec3 coords = ivec3(gl_GlobalInvocationID.xyz);
//     if (any(greaterThanEqual(coords, ivec3(params.grid_res)))) return;
// 
//     // Convertir coord textura -> Mundo
//     float bounds = params.planet_radius * 1.5;
//     vec3 world_pos = (vec3(coords) / float(params.grid_res)) * (bounds * 2.0) - bounds;
//     
//     float max_influence = 0.0;
// 
//     // Iterar POIs estáticos (Máximo 16 para performance)
//     for(int i = 0; i < 16; i++) {
//         // pois[i].w es el Radio de influencia. Si es <= 0, ignorar.
//         if (pois[i].w <= 0.001) continue; 
//         
//         // pois[i].xyz es Dirección Normalizada (si viene del EnvironmentManager)
//         // Convertimos a posición en superficie
//         vec3 poi_pos = normalize(pois[i].xyz) * params.planet_radius; 
//         float poi_radius = pois[i].w * params.planet_radius;
// 
//         float d = distance(world_pos, poi_pos);
//         // Influencia suave (1.0 en el centro, 0.0 en el borde del radio)
//         float influence = 1.0 - smoothstep(0.0, poi_radius, d);
//         max_influence = max(max_influence, influence);
//     }
// 
//     imageStore(density_texture_out, coords, vec4(max_influence, 0, 0, 1));
// }

// --- MAIN DISPATCHER ---
void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else if (params.phase == 2) phase_update();
    // phase 3 deshabilitada hasta que se implementen bindings 6 y 8
}
