using Godot;
using System;

[GlobalClass]
public partial class SimulationConfig : Resource
{
    [ExportGroup("World Settings")]
    [Export] public int WorldSeed = 1111;
    [Export] public bool RandomizeSeed = true;
    
    // --- NUEVO: FÍSICA DEL PLANETA ---
    [ExportGroup("Planet Physics")]
    [Export(PropertyHint.Range, "100, 5000")] public float Radius = 1000.0f;
    [Export(PropertyHint.Range, "0, 500")] public float OceanLevel = 0.0f;

    // --- NUEVO: FORMA DEL TERRENO (Noise) ---
    [ExportGroup("Terrain Shape")]
    [Export] public float NoiseScale = 1.5f;
    [Export] public float NoiseHeight = 70.0f; // Altura máxima de montañas
    [Export] public float WarpStrength = 0.15f;
    [Export] public float MountainRoughness = 2.0f; // Lacunarity

    // --- DETALLES ---
    [ExportGroup("Terrain Details")]
    [Export] public float DetailFrequency = 4.0f;
    [Export] public float RidgeSharpness = 2.5f;
    [Export(PropertyHint.Range, "0,1")] public float MaskStart = 0.6f;
    [Export(PropertyHint.Range, "0,1")] public float MaskEnd = 0.75f;

    // --- SIMULACIÓN LÓGICA ---
    [ExportGroup("Simulation Grid")]
    [Export(PropertyHint.Range, "32,256")] public int LogicResolution = 64; // Para Agentes/POIs
    [Export] public int TextureResolution = 1024; // Para el Shader visual (ResolutionF)
}