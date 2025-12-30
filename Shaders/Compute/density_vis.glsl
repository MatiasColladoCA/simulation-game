#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) buffer DensityBuffer { uint density_grid[]; };
layout(set = 0, binding = 1, rgba8) writeonly uniform image2D out_density_texture;

layout(push_constant) uniform Params {
    uint grid_res;
    float max_observed_density;
} params;

void main() {
    ivec2 tex_pos = ivec2(gl_GlobalInvocationID.xy);
    // Muestreamos una "rebanada" o una proyección simplificada de la grilla 3D
    // Para este ejemplo, proyectamos la densidad máxima en la columna Y
    uint combined_density = 0;
    for(uint y = 0; y < params.grid_res; y++) {
        uint idx = uint(tex_pos.x + (y * params.grid_res) + (tex_pos.y * params.grid_res * params.grid_res));
        combined_density += density_grid[idx];
    }

    float norm = clamp(float(combined_density) / params.max_observed_density, 0.0, 1.0);
    imageStore(out_density_texture, tex_pos, vec4(vec3(norm), 1.0));
}