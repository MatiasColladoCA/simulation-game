using Godot; // Necesario para Vector3
using System;

[Serializable] // Opcional, util si usas atributos de serialización custom
public struct PlanetParamsData
{
	public float Radius;
	public float Resolution;
	public Vector3 NoiseOffset;

	// --- PARÁMETROS GLOBALES ---
	public float NoiseScale;     // Frecuencia base (Zoom global)
	public float NoiseHeight;    // Altura global (Amplitud máxima)

	// --- PARÁMETROS ESPECÍFICOS "LAGUE" ---
	// Controlan la forma orgánica de los continentes y montañas
	
	// 1. Continentes (Capa Base)
	public float OceanFloorLevel; // (Min Value) Recorta el ruido para hacer océanos planos.
								  // Valor sugerido: 1.0 (Lague usa esto para separar mar de tierra)

	// 2. Montañas (Capa Rigid)
	public float MountainRoughness; // (Roughness) Cuánto detalle se añade en cada octava.
	public float WeightMultiplier;  // (Weight) EL MÁS IMPORTANTE. 
									// 0.8 = Picos afilados y valles limpios. 
									// 1.0 = Ruido caótico por todas partes.
}
