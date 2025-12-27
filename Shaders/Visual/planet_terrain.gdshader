shader_type spatial;
render_mode cull_back;

// UNIFORMS (Deben coincidir EXACTAMENTE con C#)
uniform samplerCube height_map_gpu : source_color, filter_linear_mipmap;
uniform float planet_radius = 100.0; // Valor default para debug
uniform float noise_amplitude = 10.0; 

// Varying para pasar altura al fragment shader
varying float h_val;

void vertex() {
    // Proyección esférica
    vec3 direction = normalize(VERTEX); // Asumimos que la malla base es esférica o cuboide
    
    // Leer altura de la textura (Bakeada en Compute)
    h_val = texture(height_map_gpu, direction).r;
    
    // Aplicar desplazamiento
    // Radio Base + (Altura * Amplitud)
    float final_radius = planet_radius + (h_val * noise_amplitude);
    
    // Deformar vértice
    VERTEX = direction * final_radius;
    
    // Recalcular normales (Aproximación simple para iluminación)
    NORMAL = direction; 
}

void fragment() {
    // Visualización de Debug:
    // Azul = Valle/Océano, Blanco = Montaña
    vec3 color_low = vec3(0.0, 0.1, 0.4); // Azul oscuro
    vec3 color_high = vec3(1.0, 1.0, 1.0); // Blanco
    
    // Mapeamos h_val (aprox -1 a 1) a 0 a 1
    float t = clamp((h_val + 0.5) * 0.8, 0.0, 1.0);
    
    ALBEDO = mix(color_low, color_high, t);
    ROUGHNESS = 0.8;
}