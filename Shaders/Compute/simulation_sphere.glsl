#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// --- ESTRUCTURAS ---
struct Agent {
    vec4 position; // xyz: pos, w: radius
    vec4 target;   // xyz: target
    vec4 velocity; // xyz: vel, w: max_speed
    vec4 color;    // w: status (0=dead, 1=alive, 2=saved)
};

// --- BUFFERS (Lógica de Simulación) ---
layout(set = 0, binding = 0, std430) buffer AgentsBuffer {
    Agent agents[];
};

#define CELL_CAPACITY 16
#define STRIDE 17 // 1 uint count + 16 uint ids
layout(set = 0, binding = 1, std430) buffer GridBuffer {
    uint grid_data[];
};

// --- TEXTURAS (Salida Visual) ---
layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_image_out;
layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D color_image_out;

layout(push_constant) uniform Params {
    layout(offset = 0) float delta_time;
    layout(offset = 4) float time;
    layout(offset = 8) float planet_radius;
    layout(offset = 12) float noise_scale;
    layout(offset = 16) float noise_height;
    layout(offset = 20) uint agent_count;
    layout(offset = 24) uint phase;       
    layout(offset = 28) uint grid_size;   
    layout(offset = 32) uint tex_width; 
} params;

// --- FUNCIONES MATEMÁTICAS (TERRENO) ---
float hash(vec3 p) {
    p  = fract( p*0.3183099 + .1 );
    p *= 17.0;
    return fract( p.x*p.y*p.z*(p.x+p.y+p.z) );
}

float noise( in vec3 x ) {
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f*f*(3.0-2.0*f);
    return mix(mix(mix( hash(i+vec3(0,0,0)), hash(i+vec3(1,0,0)),f.x), mix( hash(i+vec3(0,1,0)), hash(i+vec3(1,1,0)),f.x),f.y), mix(mix( hash(i+vec3(0,0,1)), hash(i+vec3(1,0,1)),f.x), mix( hash(i+vec3(0,1,1)), hash(i+vec3(1,1,1)),f.x),f.y),f.z);
}

float fbm(vec3 x) {
    float v = 0.0; float a = 0.5; vec3 shift = vec3(100.0);
    for (int i = 0; i < 5; ++i) { v += a * noise(x); x = x * 2.0 + shift; a *= 0.5; }
    return v;
}

uint get_cell_hash(ivec3 cell) {
    const uint p1 = 73856093; const uint p2 = 19349663; const uint p3 = 83492791;
    uint n = (uint(cell.x) * p1) ^ (uint(cell.y) * p2) ^ (uint(cell.z) * p3);
    return n % params.grid_size;
}

// --- FASE 0: CLEAR ---
void phase_clear() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.grid_size) return;
    grid_data[idx * STRIDE] = 0; // Reiniciar contador de celda
}

// --- FASE 1: POPULATE ---
void phase_populate() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;
    
    float status = agents[idx].color.w;
    // Solo agentes vivos ocupan espacio en la grilla para colisiones
    if (status < 0.5 || status > 1.5) return;

    vec3 pos = agents[idx].position.xyz;
    float cell_size = 2.0; // Ajustar según densidad deseada
    ivec3 cell = ivec3(floor(pos / cell_size));
    uint hash_idx = get_cell_hash(cell);
    
    // Intentar reservar slot
    uint slot = atomicAdd(grid_data[hash_idx * STRIDE], 1);
    if (slot < CELL_CAPACITY) {
        grid_data[hash_idx * STRIDE + 1 + slot] = idx;
    }
}

// --- FASE 2: UPDATE (Aquí está toda la lógica recuperada) ---
void phase_update() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= params.agent_count) return;

    // Leer estado actual
    Agent me = agents[idx];
    float status = me.color.w;
    
    // Coordenadas para escribir en textura (Visual)
    int tex_w = int(params.tex_width);
    ivec2 coord = ivec2(int(idx) % tex_w, int(idx) / tex_w);

    // --- SI ESTÁ MUERTO O SALVADO ---
    // No calculamos física, pero DEBEMOS escribir en la textura
    // para que el visualizador sepa que debe ocultarlos.
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
    if (distance(pos, target) < (params.planet_radius * 0.05)) { // Tolerancia ~5% radio
        me.color.w = 2.0; // Marcar como Salvado (Saved)
        me.velocity = vec4(0.0);
        
        // Guardar cambios y salir
        agents[idx] = me;
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    // 2. ALTURA DEL TERRENO (Ground Truth)
    vec3 dir_norm = normalize(pos);
    float n_val = fbm(dir_norm * params.noise_scale);
    float h = max(0.0, n_val - 0.45); 
    float terrain_radius = params.planet_radius + (h * params.noise_height);

    // 3. MOVIMIENTO (Seek Behavior)
    vec3 desired = normalize(target - pos) * max_speed;
    vec3 steer = (desired - vel);
    vel += steer * 2.0 * params.delta_time; // 2.0 = Steering strength

    // 4. COLISIONES (Spatial Hash Grid)
    float cell_size = 2.0;
    ivec3 my_cell = ivec3(floor(pos / cell_size));
    float total_overlap = 0.0;
    
    // Iterar vecinos 3x3x3
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec3 neighbor_cell = my_cell + ivec3(x, y, z);
                uint hash_idx = get_cell_hash(neighbor_cell);
                
                // Leer cuántos agentes hay en esta celda vecina
                uint count = min(grid_data[hash_idx * STRIDE], uint(CELL_CAPACITY));
                
                for (uint i = 0; i < count; i++) {
                    uint other_idx = grid_data[hash_idx * STRIDE + 1 + i];
                    
                    if (other_idx == idx) continue; // No chocar conmigo mismo
                    
                    // Leer datos del otro agente (costoso, pero necesario)
                    // Optimización: Podríamos guardar pos comprimida en la grid, pero por ahora leemos memoria global
                    Agent other = agents[other_idx]; 
                    if (other.color.w < 0.5 || other.color.w > 1.5) continue; // Ignorar inactivos

                    float d = distance(pos, other.position.xyz);
                    float min_dist = radius + other.position.w; // Radio A + Radio B

                    if (d < min_dist && d > 0.0001) {
                        float overlap = min_dist - d;
                        vec3 push = normalize(pos - other.position.xyz);
                        
                        // Respuesta suave (Position Based Dynamics)
                        pos += push * (overlap * 0.5); 
                        total_overlap += overlap;
                    }
                }
            }
        }
    }

    // 5. MUERTE POR PRESIÓN (Aplastamiento)
    // Si me están empujando demasiado, muero.
    if (total_overlap > 3.0) { // Umbral ajustable
        me.color.w = 0.0; // Muerto
        me.color.rgb = vec3(0.1, 0.1, 0.1); // Color visual gris oscuro
        me.velocity = vec4(0.0);
        
        agents[idx] = me;
        imageStore(pos_image_out, coord, me.position);
        imageStore(color_image_out, coord, me.color);
        return;
    }

    // 6. INTEGRACIÓN Y PROYECCIÓN
    // Integrar velocidad
    vel *= 0.98; // Fricción/Damping
    if (length(vel) > max_speed) vel = normalize(vel) * max_speed;
    pos += vel * params.delta_time;

    // Proyectar a superficie esférica
    // Aseguramos que siempre estén "sobre" el terreno
    float current_h = length(pos);
    if (current_h < terrain_radius) {
        pos = normalize(pos) * terrain_radius;
    } else {
        // Gravedad simple hacia el centro si vuelan (opcional)
        pos = normalize(pos) * max(terrain_radius, current_h - (9.8 * params.delta_time));
    }

    // Alinear velocidad a la tangente de la esfera (para que no "excaven")
    vec3 normal = normalize(pos);
    vel = vel - dot(vel, normal) * normal;

    // 7. GUARDAR RESULTADOS
    me.position.xyz = pos;
    me.velocity.xyz = vel;
    
    // Escribir en Buffer (para el siguiente frame)
    agents[idx] = me;
    
    // Escribir en Texturas (para el renderizado YA)
    imageStore(pos_image_out, coord, me.position);
    imageStore(color_image_out, coord, me.color);
}

void main() {
    if (params.phase == 0) phase_clear();
    else if (params.phase == 1) phase_populate();
    else phase_update();
}