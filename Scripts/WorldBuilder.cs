using Godot;
using System;

public partial class WorldBuilder : Node
{
    // --- DEPENDENCIAS (Lo que necesita para construir) ---
    [Export] public PackedScene PlanetPrefab; 
    [Export] public PackedScene PoiVisualPrefab; 
    [Export] public PackedScene PoiMeshPrefab;

    // --- ESTADO INTERNO ---
    private int _planetIndex = 0;

    // --- API PÚBLICA ---
    public Planet BuildWorld(RenderingDevice rd, SimulationConfig config, PoiPainter poiPainter)
    {
        // 1. Calcular Semilla Única
        int currentSeed = config.RandomizeSeed ? (int)DateTime.Now.Ticks : config.WorldSeed;
        int uniqueSeed = HashCode.Combine(currentSeed, _planetIndex++);

        // 2. Traducir Config (Resource) -> Data (Struct optimizado)
        PlanetParamsData planetData = GeneratePlanetConfig(config, uniqueSeed);

        // 3. Instanciar (El esqueleto)
        if (PlanetPrefab == null)
        {
            GD.PrintErr("[WorldBuilder] Falta asignar PlanetPrefab.");
            return null;
        }

        var planetNode = PlanetPrefab.Instantiate<Planet>();
        
        // 4. Inyección de Assets
        planetNode.PoiVisualScene = PoiVisualPrefab ?? PoiMeshPrefab;

        // 5. Inicialización (Darle vida)
        // Nota: Agregamos el nodo al árbol ANTES de inicializar para que sus hijos (_Ready) se activen si es necesario
        // Pero idealmente, Initialize no debería depender de estar en el árbol.
        // Lo retornamos sin añadirlo a Main todavía, Main decidirá dónde ponerlo.
        
        bool success = planetNode.Initialize(rd, planetData, poiPainter, config.GridResolution);

        if (!success)
        {
            planetNode.QueueFree();
            return null;
        }

        return planetNode;
    }

    // Lógica movida desde Main
    public PlanetParamsData GeneratePlanetConfig(SimulationConfig cfg, int seed)
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)seed;

        // Mapeo directo de Resource a Struct
        return new PlanetParamsData
        {
            PlanetSeed = seed,
            Radius = 1000.0f, // Podrías mover esto al Config también
            ResolutionF = 1024.0f,
            
            // Noise & Shape
            NoiseScale = 1.5f,
            NoiseHeight = 70.0f,
            WarpStrength = cfg.WarpStrength,
            MountainRoughness = 2.0f,
            
            // Ocean & Weight
            OceanFloorLevel = 0.0f,
            WeightMultiplier = 2.5f,
            
            // Random Offset
            NoiseOffset = new Vector3(rng.Randf(), rng.Randf(), rng.Randf()) * 10000.0f,
            
            // Texturing & Masks
            DetailFrequency = cfg.DetailFrequency,
            RidgeSharpness = cfg.RidgeSharpness,
            MaskStart = cfg.MaskStart,
            MaskEnd = cfg.MaskEnd,
            GroundDetailFreq = 4.0f
        };
    }
}