using Godot;
using System.Runtime.InteropServices; // ¡IMPORTANTE! Añade este using

[StructLayout(LayoutKind.Sequential, Pack = 4)] // Fuerza alineación de 4 bytes
public struct PlanetParamsData
{
	// --- vec4 noise_settings (16 bytes) ---
	public float NoiseScale;        // noise_settings.x
	public float NoiseHeight;       // noise_settings.y
	public float WarpStrength;      // noise_settings.z
	public float MountainRoughness; // noise_settings.w (Lacunarity)

	// --- vec4 curve_params (16 bytes) ---
	public float OceanFloorLevel;   // curve_params.x
	public float WeightMultiplier;  // curve_params.y
	public float GroundDetailFreq;         // curve_params.z (sin usar, pero necesario)
	public float _padding2;         // curve_params.w (sin usar, pero necesario)

	// --- vec4 global_offset (16 bytes) ---
	public Vector3 NoiseOffset;     // global_offset.xyz
	public float _padding3;         // global_offset.w (necesario para alinear)

	// --- vec4 detail_params (16 bytes) ---
	public float DetailFrequency;   // detail_params.x
	public float RidgeSharpness;    // detail_params.y
	public float MaskStart;         // detail_params.z
	public float MaskEnd;           // detail_params.w

	// --- vec4 res_offset (16 bytes) ---
	// El shader espera floats, así que pasamos la resolución como float.
	public float ResolutionF;       // res_offset.x
	public float Radius;            // res_offset.y
	public float _padding4;         // res_offset.z (sin usar)
	public float _padding5;         // res_offset.w (sin usar)

	// --- vec4 pad_uv (16 bytes) ---
	// El shader tiene este bloque, por lo que nuestro struct debe coincidir.
	public float _padding6;         // pad_uv.x
	public float _padding7;         // pad_uv.y
	public float _padding8;         // pad_uv.z
	public float _padding9;         // pad_uv.w
}
