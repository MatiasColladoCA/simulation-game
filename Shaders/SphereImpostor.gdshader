shader_type spatial;
// depth_prepass_alpha: Ayuda a que las sombras respeten el círculo
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, depth_prepass_alpha;

// Usamos COLOR porque en C# usas SetInstanceColor
varying vec4 v_color;

void vertex() {
    // 1. BILLBOARD ROBUSTO (Spherical Billboard)
    // Calculamos la posición del centro del agente en "Ojos de la Cámara" (View Space)
    vec3 center_view = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;

    // Extraemos la escala original del objeto (para que respete el tamaño 2.0 o lo que pongas)
    // Asumimos escala uniforme extraída de la primera columna
    float scale = length(MODEL_MATRIX[0].xyz);

    // Reconstruimos la matriz MODELVIEW.
    // Al poner la matriz de rotación como Identidad (1,0,0 / 0,1,0 / 0,0,1),
    // forzamos a que el Quad siempre esté alineado con la pantalla.
    MODELVIEW_MATRIX = mat4(
        vec4(scale, 0.0, 0.0, 0.0),
        vec4(0.0, scale, 0.0, 0.0),
        vec4(0.0, 0.0, 1.0, 0.0),
        vec4(center_view, 1.0)
    );

    // 2. Pasar el color desde C# al Fragment Shader
    // SetInstanceColor en C# -> COLOR en Shader
    v_color = COLOR;
}

void fragment() {
    // Mapear UV de [0,1] a [-1,1]
    vec2 uv_centered = UV * 2.0 - 1.0;

    // Corregir la Y para que la iluminación venga de arriba, no de abajo
    uv_centered.y *= -1.0;

    // Calcular distancia al centro
    float dist_sq = dot(uv_centered, uv_centered);

    // Recortar lo que no sea círculo
    if (dist_sq > 1.0) {
        discard;
    }

    // Calcular la curvatura (Z) para simular la esfera
    float z = sqrt(1.0 - dist_sq);
    vec3 sphere_normal = vec3(uv_centered, z);

    // Asignar normal y color
    NORMAL = normalize(sphere_normal);
    ALBEDO = v_color.rgb;

    // Propiedades del material (Plástico brillante)
    ROUGHNESS = 0.2;
    METALLIC = 0.0;
}