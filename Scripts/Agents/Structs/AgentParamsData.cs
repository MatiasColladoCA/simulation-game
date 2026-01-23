using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct AgentSimulationParams
{
    // --- Bloque de Floats (20 bytes) ---
    // layout(push_constant) uniform Params {
    //    float delta;
    public float Delta;
    //    float time;
    public float Time;
    //    float planet_radius;
    public float PlanetRadius;
    //    float noise_scale;
    public float NoiseScale;
    //    float noise_height;
    public float NoiseHeight;

    // --- Bloque de Uints (16 bytes) ---
    //    uint custom_param;
    public uint CustomParam;
    //    uint phase;
    public uint Phase;
    //    uint grid_res;
    public uint GridRes;
    //    uint tex_width;
    public uint TexWidth;
    
    // Total Real: 36 bytes.
    // Total Requerido por GPU (Alignment 16): 48 bytes.
    // NO agregues variables de padding manual aquí, lo manejaremos al enviar.
}