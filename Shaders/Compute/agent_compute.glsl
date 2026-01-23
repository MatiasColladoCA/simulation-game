// #[compute]
// #version 450

// // Tamaño de grupo local (ajústalo según tu hardware, 64 es estándar)
// layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// // --- ESTRUCTURAS ---
// struct Agent {
//     vec4 position;   // xyz = posición actual, w = activo (1.0) / inactivo (0.0)
//     vec4 velocity;   // xyz = dirección movimiento, w = velocidad
//     vec4 color;      // visualización
//     vec4 group_data; // lógica de juego
// };

// // --- BINDINGS (Deben coincidir con AgentSystem.cs) ---

// // 1. El Buffer de Agentes (Lectura y Escritura)
// layout(set = 0, binding = 0, std430) restrict buffer AgentBuffer {
//     Agent agents[];
// };

// // 2. Grid Espacial (Para colisiones futuras, ignorar por ahora)
// layout(set = 0, binding = 1, std430) restrict buffer GridBuffer {
//     int grid_cells[];
// };

// // 3. Texturas de Visualización (Donde escribimos los puntitos para el render)
// layout(set = 0, binding = 2, rgba32f) writeonly uniform image2D pos_tex;
// layout(set = 0, binding = 3, rgba32f) writeonly uniform image2D col_tex;

// // 4. MAPAS DEL MUNDO (Lectura - ¡Aquí está la magia!)
// // Usamos samplerCube porque el Baker generó Cubemaps
// layout(set = 0, binding = 4) uniform samplerCube height_map;
// layout(set = 0, binding = 5) uniform samplerCube vector_field;

// // 5. Parámetros Globales (Delta Time, Radio del Planeta)
// layout(push_constant) uniform Params {
//     float delta_time;
//     float planet_radius;
//     float terrain_amplitude; // Altura máxima de las montañas
//     float move_speed;
// } params;

// // --- UTILS ---
// vec3 slerp(vec3 start, vec3 end, float percent) {
//     float dot = dot(start, end);
//     dot = clamp(dot, -1.0, 1.0);
//     float theta = acos(dot) * percent;
//     vec3 relative = normalize(end - start * dot);
//     return start * cos(theta) + relative * sin(theta);
// }

// void main() {
//     uint id = gl_GlobalInvocationID.x;
    
//     // Seguridad: No salirnos del array
//     if (id >= agents.length()) return;

//     // Copia local del agente (más rápido)
//     Agent agent = agents[id];

//     // Si está muerto/inactivo, no procesar
//     if (agent.position.w == 0.0) return;

//     // --- PASO 1: LEER EL ENTORNO ---
//     // La dirección desde el centro del planeta es simplemente la posición normalizada
//     vec3 dir = normalize(agent.position.xyz);

//     // Muestrear altura (Rojo = Altura)
//     // El Baker guardó la altura normalizada (0.0 a 1.0) en el canal R
//     float height_val = texture(height_map, dir).r;
    
//     // Calcular radio objetivo (Radio Base + Montaña)
//     float target_radius = params.planet_radius + (height_val * params.terrain_amplitude);

//     // Muestrear Flujo (Vector Field)
//     // El Baker guardó la dirección del flujo en RGB
//     vec3 flow_dir = texture(vector_field, dir).rgb;

//     // --- PASO 2: MOVIEMIENTO (Navegación) ---
//     // Por ahora, simplemente nos movemos en la dirección del flujo
//     // flow_dir es tangente a la esfera gracias al curl noise del Baker
    
//     vec3 current_pos = agent.position.xyz;
    
//     // Movimiento básico: Posición + Dirección * Velocidad * DeltaTime
//     // (Nota: Esto nos despegará ligeramente de la esfera, por eso el paso 3 es vital)
//     vec3 velocity = flow_dir * params.move_speed;
//     vec3 next_pos_raw = current_pos + (velocity * params.delta_time);

//     // --- PASO 3: SNAPPING (Pegarse al Suelo) ---
//     // Recalculamos la dirección basada en la nueva posición tentativa
//     vec3 next_dir = normalize(next_pos_raw);
    
//     // Volvemos a leer la altura en la NUEVA posición para ajustar
//     float next_height_val = texture(height_map, next_dir).r;
//     float next_target_radius = params.planet_radius + (next_height_val * params.terrain_amplitude);
    
//     // Proyectamos el punto a la superficie exacta
//     vec3 final_pos = next_dir * next_target_radius;

//     // --- PASO 4: ACTUALIZAR VISUALIZACIÓN ---
//     // Escribimos en la textura que el MeshInstance3D leerá para renderizar
//     int tex_w = imageSize(pos_tex).x;
//     ivec2 tex_coord = ivec2(int(id) % tex_w, int(id) / tex_w);
    
//     imageStore(pos_tex, tex_coord, vec4(final_pos, 1.0));
//     imageStore(col_tex, tex_coord, agent.color); // Color blanco o basado en facción

//     // --- PASO 5: GUARDAR ESTADO ---
//     agent.position.xyz = final_pos;
//     agent.velocity.xyz = velocity; // Guardamos para inercia futura
    
//     agents[id] = agent;
// }